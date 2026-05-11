using System.Text.Json;
using System.Net.Http;
using System.Security.Claims;
using MatchmakingService.Models;
using MatchmakingService.Services;
using MatchmakingService.Metrics;
using MatchmakingService.Data;
using MatchmakingService.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace MatchmakingService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MatchmakingController : ControllerBase
    {
        private readonly IUserServiceClient _userServiceClient;
        private readonly MatchmakingService.Services.MatchmakingService _matchmakingService;
        private readonly IAdvancedMatchingService _advancedMatchingService;
        private readonly INotificationService _notificationService;
        private readonly IDailySuggestionTracker _suggestionTracker;
        private readonly MatchmakingDbContext _context;
        private readonly ILogger<MatchmakingController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly MatchmakingServiceMetrics? _metrics;
        private readonly IMatchInsightService? _matchInsightService;

        public MatchmakingController(
            IUserServiceClient userServiceClient,
            MatchmakingService.Services.MatchmakingService matchmakingService,
            IAdvancedMatchingService advancedMatchingService,
            INotificationService notificationService,
            IDailySuggestionTracker suggestionTracker,
            MatchmakingDbContext context,
            ILogger<MatchmakingController> logger,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            MatchmakingServiceMetrics? metrics = null,
            IMatchInsightService? matchInsightService = null)
        {
            _userServiceClient = userServiceClient;
            _matchmakingService = matchmakingService;
            _advancedMatchingService = advancedMatchingService;
            _notificationService = notificationService;
            _suggestionTracker = suggestionTracker;
            _context = context;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _metrics = metrics;
            _matchInsightService = matchInsightService;
        }

        // POST: Handle mutual match notifications from SwipeService
        [HttpPost("matches")]
        public async Task<IActionResult> HandleMutualMatch([FromBody] MutualMatchRequest request)
        {
            try
            {
                var user1 = Math.Min(request.User1Id, request.User2Id);
                var user2 = Math.Max(request.User1Id, request.User2Id);

                // Check if match already exists (idempotent — safe for retries and repeated test runs)
                var existing = await _context.Matches
                    .FirstOrDefaultAsync(m => m.User1Id == user1 && m.User2Id == user2);

                if (existing != null)
                {
                    _logger.LogInformation("Match already exists between users {User1} and {User2} (ID {MatchId}), returning existing",
                        user1, user2, existing.Id);
                    return Ok(new
                    {
                        Message = "Match already exists",
                        MatchId = existing.Id,
                        CompatibilityScore = Math.Round(existing.CompatibilityScore, 1)
                    });
                }

                // Calculate compatibility score if not provided
                double compatibilityScore;
                if (request.CompatibilityScore.HasValue)
                {
                    compatibilityScore = request.CompatibilityScore.Value;
                }
                else
                {
                    try
                    {
                        compatibilityScore = await _advancedMatchingService.CalculateCompatibilityScoreAsync(request.User1Id, request.User2Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to calculate compatibility score for {U1}↔{U2}, using default", request.User1Id, request.User2Id);
                        compatibilityScore = 50.0;
                    }
                }

                // Save the match with enhanced details
                var match = new Match
                {
                    User1Id = user1,
                    User2Id = user2,
                    CreatedAt = DateTime.UtcNow,
                    CompatibilityScore = compatibilityScore,
                    MatchSource = request.Source,
                    IsActive = true
                };

                _context.Matches.Add(match);
                await _context.SaveChangesAsync();
                _metrics?.MatchCreated();

                // T532 (spec 005): generate per-user MatchInsight rows for "Why You Matched" card.
                // Soft enrichment — service swallows errors and is no-op when not registered.
                if (_matchInsightService != null)
                {
                    await _matchInsightService.GenerateForMatchAsync(match.Id, user1, user2, compatibilityScore);
                }

                // Send match notifications to both users
                await _notificationService.NotifyMatchAsync(request.User1Id, request.User2Id, match.Id);

                _logger.LogInformation($"Match created between users {request.User1Id} and {request.User2Id} with score {compatibilityScore}");

                return Ok(new
                {
                    Message = "Match saved successfully!",
                    MatchId = match.Id,
                    CompatibilityScore = compatibilityScore
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling mutual match between users {request.User1Id} and {request.User2Id}");
                return StatusCode(500, "Error processing match");
            }
        }

        // GET: Find potential matches for a user using advanced algorithm
        /// <summary>
        /// T033: Enhanced endpoint with daily suggestion limit tracking
        /// </summary>
        [HttpPost("find-matches")]
        public async Task<IActionResult> FindMatches([FromBody] FindMatchesRequest request)
        {
            try
            {
                if (request.UserId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                var isPremium = request.IsPremium ?? false;

                // Get current status before making request
                var statusBefore = await _suggestionTracker.GetStatusAsync(request.UserId, isPremium);

                // Check if user has reached daily limit
                if (statusBefore.SuggestionsRemaining <= 0)
                {
                    _logger.LogInformation("User {UserId} has reached daily suggestion limit ({Count}/{Max})",
                        request.UserId, statusBefore.SuggestionsShownToday, statusBefore.MaxDailySuggestions);

                    return Ok(new FindMatchesResponse
                    {
                        Matches = new List<MatchSuggestionResponse>(),
                        Count = 0,
                        RequestId = Guid.NewGuid().ToString(),
                        SuggestionsRemaining = 0,
                        DailyLimitReached = true,
                        NextResetAt = statusBefore.NextResetDate,
                        QueueExhausted = false,
                        Message = isPremium
                            ? $"You've viewed all {statusBefore.MaxDailySuggestions} daily suggestions. Check back tomorrow!"
                            : $"You've reached your daily limit of {statusBefore.MaxDailySuggestions} profiles. Upgrade to Premium for {50 - statusBefore.MaxDailySuggestions} more!"
                    });
                }

                var matches = await _advancedMatchingService.FindMatchesAsync(request);

                // Get updated status after matches found
                var statusAfter = await _suggestionTracker.GetStatusAsync(request.UserId, isPremium);

                // Determine if queue is exhausted (no more candidates available)
                var queueExhausted = matches.Count == 0 && statusAfter.SuggestionsRemaining > 0;

                var message = queueExhausted
                    ? "No more profiles available right now. Try broadening your preferences!"
                    : matches.Count > 0
                        ? $"Found {matches.Count} compatible profiles"
                        : statusAfter.SuggestionsRemaining > 0
                            ? "No matches found. Try adjusting your filters."
                            : "Daily limit reached. Check back tomorrow!";

                _logger.LogInformation("Found {Count} matches for user {UserId}. Remaining suggestions: {Remaining}/{Max}",
                    matches.Count, request.UserId, statusAfter.SuggestionsRemaining, statusAfter.MaxDailySuggestions);

                return Ok(new FindMatchesResponse
                {
                    Matches = matches,
                    Count = matches.Count,
                    RequestId = Guid.NewGuid().ToString(),
                    SuggestionsRemaining = statusAfter.SuggestionsRemaining,
                    DailyLimitReached = statusAfter.SuggestionsRemaining <= 0,
                    NextResetAt = statusAfter.NextResetDate,
                    QueueExhausted = queueExhausted,
                    Message = message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error finding matches for user {request.UserId}");
                return StatusCode(500, "Error finding matches");
            }
        }

        // GET: Check daily suggestion status without consuming a suggestion
        /// <summary>
        /// T033: Get current daily suggestion limit status for a user
        /// </summary>
        [HttpGet("daily-suggestions/status/{userId}")]
        public async Task<IActionResult> GetDailySuggestionStatus(int userId, [FromQuery] bool isPremium = false)
        {
            try
            {
                if (userId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                var status = await _suggestionTracker.GetStatusAsync(userId, isPremium);

                var response = new DailySuggestionStatusResponse
                {
                    SuggestionsShownToday = status.SuggestionsShownToday,
                    MaxDailySuggestions = status.MaxDailySuggestions,
                    SuggestionsRemaining = status.SuggestionsRemaining,
                    LastResetDate = status.LastResetDate,
                    NextResetDate = status.NextResetDate,
                    QueueExhausted = status.QueueExhausted,
                    IsPremium = isPremium,
                    Tier = isPremium ? "premium" : "free"
                };

                _logger.LogInformation("Daily suggestion status for user {UserId}: {Shown}/{Max} shown, {Remaining} remaining",
                    userId, status.SuggestionsShownToday, status.MaxDailySuggestions, status.SuggestionsRemaining);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting daily suggestion status for user {UserId}", userId);
                return StatusCode(500, "Error retrieving daily suggestion status");
            }
        }

        // GET: Get compatibility score between two users
        [HttpGet("compatibility/{userId}/{targetUserId}")]
        public async Task<IActionResult> GetCompatibilityScore(int userId, int targetUserId)
        {
            try
            {
                if (userId <= 0 || targetUserId <= 0 || userId == targetUserId)
                {
                    return BadRequest("Invalid user IDs");
                }

                var score = await _advancedMatchingService.CalculateCompatibilityScoreAsync(userId, targetUserId);

                return Ok(new
                {
                    UserId = userId,
                    TargetUserId = targetUserId,
                    CompatibilityScore = Math.Round(score, 1),
                    CalculatedAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error calculating compatibility between users {userId} and {targetUserId}");
                return StatusCode(500, "Error calculating compatibility");
            }
        }

        // GET: Retrieve matches for authenticated user (JWT-based, more RESTful)
        /// <summary>
        /// T001: New endpoint - Get matches for currently authenticated user
        /// Uses JWT claims to identify user, no userId parameter needed
        /// </summary>
        [HttpGet("matches")]
        public async Task<IActionResult> GetMyMatches([FromQuery] bool includeInactive = false, [FromQuery] int? page = 1, [FromQuery] int? pageSize = 20)
        {
            try
            {
                // Extract userId from JWT claims (when auth is enabled)
                // For now, expect userId in query string or header for backwards compatibility
                var userIdClaim = User.FindFirst("sub")?.Value ?? User.FindFirst("user_id")?.Value;

                if (string.IsNullOrEmpty(userIdClaim))
                {
                    // Fallback: check query parameter for demo/testing purposes
                    var userIdParam = Request.Query["userId"].FirstOrDefault();
                    if (string.IsNullOrEmpty(userIdParam))
                    {
                        return BadRequest(new
                        {
                            Error = "User ID not found in authentication token",
                            Message = "Please include userId query parameter or ensure valid JWT token"
                        });
                    }
                    userIdClaim = userIdParam;
                }

                if (!int.TryParse(userIdClaim, out int userId) || userId <= 0)
                {
                    return BadRequest("Invalid user ID in token");
                }

                // Use AsNoTracking() for read-only query optimization
                var query = _context.Matches
                    .AsNoTracking()
                    .Where(m => m.User1Id == userId || m.User2Id == userId);

                if (!includeInactive)
                {
                    query = query.Where(m => m.IsActive);
                }

                // Get total count before pagination
                var totalCount = await query.CountAsync();

                // Apply pagination
                var skip = ((page ?? 1) - 1) * (pageSize ?? 20);
                var matches = await query
                    .OrderByDescending(m => m.LastMessageAt ?? m.CreatedAt)
                    .Skip(skip)
                    .Take(pageSize ?? 20)
                    .Select(m => new
                    {
                        MatchId = m.Id,
                        MatchedUserId = m.User1Id == userId ? m.User2Id : m.User1Id,
                        MatchedAt = m.CreatedAt,
                        CompatibilityScore = Math.Round(m.CompatibilityScore, 1),
                        IsActive = m.IsActive,
                        MatchSource = m.MatchSource,
                        LastMessageAt = m.LastMessageAt,
                        LastMessageByUserId = m.LastMessageByUserId,
                        UnmatchedAt = m.UnmatchedAt,
                        UnmatchedByUserId = m.UnmatchedByUserId
                    })
                    .ToListAsync();

                _logger.LogInformation("Retrieved {Count} matches for authenticated user {UserId} (page {Page}/{PageSize})",
                    matches.Count, userId, page, pageSize);

                return Ok(new
                {
                    Matches = matches,
                    TotalCount = totalCount,
                    ActiveCount = matches.Count(m => m.IsActive),
                    Page = page ?? 1,
                    PageSize = pageSize ?? 20,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)(pageSize ?? 20))
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving matches for authenticated user");
                return StatusCode(500, "Error retrieving matches");
            }
        }

        // GET: Retrieve matches for a user — accepts UUID string or integer ID
        [HttpGet("matches/{userId}")]
        public async Task<IActionResult> GetMatchesForUser(string userId, [FromQuery] bool includeInactive = false)
        {
            try
            {
                int profileId;

                if (int.TryParse(userId, out profileId))
                {
                    // Already an integer — use directly
                }
                else
                {
                    // UUID string from Flutter — resolve to integer profile ID via UserService
                    var userServiceUrl = _configuration["Services:UserService:BaseUrl"]
                        ?? "http://localhost:8082";
                    var client = _httpClientFactory.CreateClient();
                    var authHeader = Request.Headers["Authorization"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(authHeader))
                        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);

                    var meResponse = await client.GetAsync($"{userServiceUrl}/api/profiles/me");
                    if (!meResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to resolve UUID {UserId} to profile ID", userId);
                        return Ok(new List<object>());
                    }

                    var meContent = await meResponse.Content.ReadAsStringAsync();
                    using var meDoc = JsonDocument.Parse(meContent);
                    if (meDoc.RootElement.TryGetProperty("data", out var meData) &&
                        meData.TryGetProperty("id", out var meId))
                    {
                        profileId = meId.GetInt32();
                        _logger.LogInformation("Resolved UUID {UserId} to profile ID {ProfileId}", userId, profileId);
                    }
                    else
                    {
                        _logger.LogWarning("Could not extract profile ID from /api/profiles/me for {UserId}", userId);
                        return Ok(new List<object>());
                    }
                }

                if (profileId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                var query = _context.Matches
                    .AsNoTracking()
                    .Where(m => m.User1Id == profileId || m.User2Id == profileId);

                if (!includeInactive)
                {
                    query = query.Where(m => m.IsActive);
                }

                var matches = await query
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new
                    {
                        matchId = m.Id,
                        matchedUserId = m.User1Id == profileId ? m.User2Id : m.User1Id,
                        matchedAt = m.CreatedAt,
                        compatibilityScore = m.CompatibilityScore,
                        isActive = m.IsActive,
                        matchSource = m.MatchSource,
                        lastMessageAt = m.LastMessageAt,
                        lastMessageByUserId = m.LastMessageByUserId
                    })
                    .ToListAsync();

                _logger.LogInformation("Returning {Count} matches for user {UserId} (profile {ProfileId})",
                    matches.Count, userId, profileId);

                // Fallback to SwipeService when local Matches table is empty
                if (matches.Count == 0)
                {
                    _logger.LogWarning("No matches in local DB for user {UserId}, trying SwipeService fallback", userId);
                    try
                    {
                        var swipeServiceUrl = _configuration["Services:SwipeService:BaseUrl"]
                            ?? "http://localhost:8087";
                        var client = _httpClientFactory.CreateClient();
                        var authHeader = Request.Headers["Authorization"].FirstOrDefault();
                        if (!string.IsNullOrEmpty(authHeader))
                            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);

                        var swipeResp = await client.GetAsync($"{swipeServiceUrl}/api/Swipes/matches/{profileId}");
                        if (swipeResp.IsSuccessStatusCode)
                        {
                            var swipeContent = await swipeResp.Content.ReadAsStringAsync();
                            using var swipeDoc = System.Text.Json.JsonDocument.Parse(swipeContent);

                            // SwipeService returns {success:true, data:[...]}
                            System.Text.Json.JsonElement matchArray;
                            if (swipeDoc.RootElement.TryGetProperty("data", out var dataElem) &&
                                dataElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                matchArray = dataElem;
                            }
                            else if (swipeDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                matchArray = swipeDoc.RootElement;
                            }
                            else
                            {
                                _logger.LogWarning("Unexpected SwipeService response format");
                                return Ok(new List<object>());
                            }

                            var swipeMatches = new List<object>();
                            foreach (var item in matchArray.EnumerateArray())
                            {
                                swipeMatches.Add(new
                                {
                                    matchId = item.TryGetProperty("id", out var sid) ? sid.GetInt32() : 0,
                                    matchedUserId = item.TryGetProperty("matchedUserId", out var mid) ? mid.GetInt32() : 0,
                                    matchedAt = item.TryGetProperty("matchedAt", out var mat) ? mat.GetString() : null,
                                    compatibilityScore = (double?)null,
                                    isActive = item.TryGetProperty("isActive", out var ia) && ia.GetBoolean(),
                                    matchSource = "SwipeService",
                                    lastMessageAt = (string?)null,
                                    lastMessageByUserId = (int?)null
                                });
                            }
                            _logger.LogInformation("SwipeService fallback returned {Count} matches for user {UserId}",
                                swipeMatches.Count, userId);
                            return Ok(swipeMatches);
                        }
                        else
                        {
                            _logger.LogWarning("SwipeService returned {StatusCode} for matches of user {UserId}",
                                (int)swipeResp.StatusCode, userId);
                        }
                    }
                    catch (Exception swipeEx)
                    {
                        _logger.LogError(swipeEx, "SwipeService fallback failed for user {UserId}", userId);
                    }
                }

                // Return as flat JSON array (Flutter expects List<dynamic>)
                return Ok(matches);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving matches for user {UserId}", userId);
                return StatusCode(500, "Error retrieving matches");
            }
        }

        // GET: Consolidated match list with user profiles and message previews
        [HttpGet("matches/{userId}/consolidated")]
        public async Task<IActionResult> GetConsolidatedMatches(int userId, [FromQuery] bool includeInactive = false)
        {
            try
            {
                if (userId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                // Use AsNoTracking() for read-only query optimization
                var query = _context.Matches
                    .AsNoTracking()
                    .Where(m => m.User1Id == userId || m.User2Id == userId);

                if (!includeInactive)
                {
                    query = query.Where(m => m.IsActive);
                }

                var matches = await query
                    .OrderByDescending(m => m.LastMessageAt ?? m.CreatedAt)
                    .ToListAsync();

                var consolidatedMatches = new List<ConsolidatedMatchDto>();
                int unreadCount = 0;

                foreach (var match in matches)
                {
                    var matchedUserId = match.User1Id == userId ? match.User2Id : match.User1Id;

                    // Fetch user profile from UserService
                    var userProfile = await _userServiceClient.GetUserProfileAsync(matchedUserId);

                    if (userProfile == null)
                    {
                        _logger.LogWarning("Could not fetch profile for user {UserId}", matchedUserId);
                        continue; // Skip this match if profile unavailable
                    }

                    var consolidatedMatch = new ConsolidatedMatchDto
                    {
                        MatchId = match.Id,
                        MatchedUserId = matchedUserId,
                        MatchedAt = match.CreatedAt,
                        CompatibilityScore = Math.Round(match.CompatibilityScore, 1),
                        MatchSource = match.MatchSource,

                        // User profile details (from matchmaking-local UserProfile)
                        Name = $"User {matchedUserId}", // TODO: Fetch from UserService API when available
                        Age = userProfile.Age,
                        Bio = null, // TODO: Fetch from UserService API
                        PrimaryPhotoUrl = $"/api/photos/{matchedUserId}/primary",
                        City = userProfile.City,
                        DistanceKm = null, // Could calculate from lat/long if needed

                        // Message preview (stub - to be implemented when messaging API available)
                        LastMessagePreview = null,
                        LastMessageAt = match.LastMessageAt,
                        IsLastMessageFromMe = match.LastMessageByUserId == userId,
                        UnreadCount = null, // TODO: Call messaging service for unread count

                        // Status
                        IsActive = match.IsActive,
                        IsOnline = false // TODO: Could integrate with presence service
                    };

                    consolidatedMatches.Add(consolidatedMatch);
                }

                var response = new ConsolidatedMatchListResponse
                {
                    Matches = consolidatedMatches,
                    TotalCount = consolidatedMatches.Count,
                    ActiveCount = consolidatedMatches.Count(m => m.IsActive),
                    UnreadMessagesCount = unreadCount
                };

                _logger.LogInformation("Retrieved {Count} consolidated matches for user {UserId}",
                    consolidatedMatches.Count, userId);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving consolidated matches for user {UserId}", userId);
                return StatusCode(500, "Error retrieving matches");
            }
        }

        // GET: Get match statistics for a user
        [HttpGet("stats/{userId}")]
        public async Task<IActionResult> GetMatchStats(int userId)
        {
            try
            {
                if (userId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                var stats = await _advancedMatchingService.GetMatchStatsAsync(userId);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving match stats for user {userId}");
                return StatusCode(500, "Error retrieving match stats");
            }
        }

        // DELETE: Unmatch users
        [HttpDelete("matches/{userId}/{targetUserId}")]
        public async Task<IActionResult> UnmatchUsers(int userId, int targetUserId)
        {
            try
            {
                if (userId <= 0 || targetUserId <= 0)
                {
                    return BadRequest("Invalid user IDs");
                }

                var match = await _context.Matches
                    .FirstOrDefaultAsync(m =>
                        (m.User1Id == Math.Min(userId, targetUserId) && m.User2Id == Math.Max(userId, targetUserId)) &&
                        m.IsActive);

                if (match == null)
                {
                    return NotFound("Active match not found");
                }

                match.IsActive = false;
                match.UnmatchedAt = DateTime.UtcNow;
                match.UnmatchedByUserId = userId;

                await _context.SaveChangesAsync();
                _metrics?.MatchCreated();

                _logger.LogInformation($"Users {userId} and {targetUserId} unmatched");

                return Ok(new { Message = "Users unmatched successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error unmatching users {userId} and {targetUserId}");
                return StatusCode(500, "Error unmatching users");
            }
        }

        // T534 (spec 005): "Why You Matched" insight card — tiered response.
        // Free tier: overall score + top 2 reasons. Premium (future): full 4-section card.
        [HttpGet("matches/{matchId}/insight")]
        [Authorize]
        public async Task<IActionResult> GetMatchInsight(int matchId)
        {
            if (matchId <= 0) return BadRequest("Invalid match ID");

            var keycloakId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(keycloakId)) return Unauthorized();

            // Verify the caller is part of this match before exposing any insight.
            var profile = await _context.UserProfiles
                .Where(p => p.KeycloakId == keycloakId)
                .Select(p => new { p.UserId })
                .FirstOrDefaultAsync();
            if (profile == null) return NotFound("Profile not found");

            var match = await _context.Matches
                .Where(m => m.Id == matchId && (m.User1Id == profile.UserId || m.User2Id == profile.UserId))
                .FirstOrDefaultAsync();
            if (match == null) return NotFound("Match not found");

            var insight = await _context.MatchInsights
                .Where(mi => mi.MatchId == matchId && mi.ForKeycloakId == keycloakId)
                .FirstOrDefaultAsync();
            if (insight == null)
            {
                // No insight generated (e.g. one side hasn't onboarded, or pre-T532 match).
                return Ok(new
                {
                    matchId,
                    overallScore = Math.Round(match.CompatibilityScore, 1),
                    reasons = Array.Empty<string>(),
                    frictions = Array.Empty<string>(),
                    tier = "free",
                    available = false,
                });
            }

            string[] reasons;
            string[] frictions;
            try
            {
                reasons = JsonSerializer.Deserialize<string[]>(insight.ReasonsJson) ?? Array.Empty<string>();
                frictions = JsonSerializer.Deserialize<string[]>(insight.FrictionJson) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                reasons = Array.Empty<string>();
                frictions = Array.Empty<string>();
            }

            // Tiered: free users see overall score + top 2 reasons.
            // Premium gate is a placeholder until T570+ wires entitlements.
            bool isPremium = User.HasClaim("tier", "premium");

            return Ok(new
            {
                matchId,
                overallScore = Math.Round(insight.OverallScore, 1),
                reasons = isPremium ? reasons : reasons.Take(2).ToArray(),
                frictions = isPremium ? frictions : Array.Empty<string>(),
                tier = isPremium ? "premium" : "free",
                available = true,
            });
        }

        // POST: Unmatch by match ID with reason tracking (preferred method)
        [HttpPost("matches/{matchId}/unmatch")]
        public async Task<IActionResult> UnmatchByMatchId(int matchId, [FromBody] UnmatchRequest request)
        {
            try
            {
                if (matchId <= 0)
                {
                    return BadRequest("Invalid match ID");
                }

                if (request.UserId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                var match = await _context.Matches
                    .FirstOrDefaultAsync(m => m.Id == matchId && m.IsActive);

                if (match == null)
                {
                    return NotFound("Active match not found");
                }

                // Verify the requesting user is a participant in this match
                if (match.User1Id != request.UserId && match.User2Id != request.UserId)
                {
                    return Forbid(); // User is not part of this match
                }

                // Soft delete the match with reason tracking
                match.IsActive = false;
                match.UnmatchedAt = DateTime.UtcNow;
                match.UnmatchedByUserId = request.UserId;
                match.UnmatchReason = request.Reason ?? "not_specified";

                await _context.SaveChangesAsync();
                _metrics?.MatchCreated();

                var otherUserId = match.User1Id == request.UserId ? match.User2Id : match.User1Id;

                _logger.LogInformation(
                    "Match {MatchId} unmatched by user {UserId} (other user: {OtherUserId}). Reason: {Reason}",
                    matchId, request.UserId, otherUserId, match.UnmatchReason);

                return Ok(new UnmatchResponse
                {
                    Success = true,
                    Message = "Match ended successfully",
                    MatchId = matchId,
                    UnmatchedAt = match.UnmatchedAt.Value
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unmatching match {MatchId} by user {UserId}", matchId, request.UserId);
                return StatusCode(500, "Error ending match");
            }
        }

        // POST: Update user's matching preferences

        [HttpPost("preferences")]
        public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
        {
            try
            {
                if (request.UserId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                // Note: Not using AsNoTracking() here because we need to track changes for update
                var userProfile = await _context.UserProfiles
                    .FirstOrDefaultAsync(up => up.UserId == request.UserId);

                if (userProfile == null)
                {
                    return NotFound("User profile not found");
                }

                // Update preferences
                userProfile.PreferredGender = request.PreferredGender;
                userProfile.MinAge = request.MinAge;
                userProfile.MaxAge = request.MaxAge;
                userProfile.MaxDistance = request.MaxDistance;
                userProfile.Interests = System.Text.Json.JsonSerializer.Serialize(request.Interests);

                // Update algorithm weights if provided
                if (request.AlgorithmWeights.ContainsKey("location"))
                    userProfile.LocationWeight = request.AlgorithmWeights["location"];
                if (request.AlgorithmWeights.ContainsKey("age"))
                    userProfile.AgeWeight = request.AlgorithmWeights["age"];
                if (request.AlgorithmWeights.ContainsKey("interests"))
                    userProfile.InterestsWeight = request.AlgorithmWeights["interests"];
                if (request.AlgorithmWeights.ContainsKey("education"))
                    userProfile.EducationWeight = request.AlgorithmWeights["education"];
                if (request.AlgorithmWeights.ContainsKey("lifestyle"))
                    userProfile.LifestyleWeight = request.AlgorithmWeights["lifestyle"];

                await _advancedMatchingService.UpdateUserProfileAsync(userProfile);

                return Ok(new { Message = "Preferences updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating preferences for user {request.UserId}");
                return StatusCode(500, "Error updating preferences");
            }
        }

        // POST: Record swipe history to improve recommendations
        [HttpPost("swipe-history")]
        public async Task<IActionResult> RecordSwipeHistory([FromBody] SwipeHistoryRequest request)
        {
            try
            {
                if (request.UserId <= 0)
                {
                    return BadRequest("Invalid user ID");
                }

                await _advancedMatchingService.RecordSwipeHistoryAsync(request);

                return Ok(new { Message = "Swipe history recorded successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error recording swipe history for user {request.UserId}");
                return StatusCode(500, "Error recording swipe history");
            }
        }

        [HttpGet("userprofile/{userId}")]
        public async Task<IActionResult> GetUserProfile(int userId)
        {
            try
            {
                var userProfile = await _userServiceClient.GetUserProfileAsync(userId);
                if (userProfile == null)
                {
                    return NotFound($"User profile with ID {userId} not found.");
                }

                return Ok(userProfile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving user profile for user {userId}");
                return StatusCode(500, "Error retrieving user profile");
            }
        }

        /// <summary>
        /// Delete all matches for a specific user (used during account deletion)
        /// </summary>
        [HttpDelete("user/{userProfileId:int}/matches")]
        [AllowAnonymous]
        public async Task<IActionResult> DeleteUserMatches(int userProfileId)
        {
            try
            {
                _logger.LogInformation("Deleting all matches for user {UserProfileId}", userProfileId);

                // Delete matches where user is either user1 or user2
                var matches = await _context.Matches
                    .Where(m => m.User1Id == userProfileId || m.User2Id == userProfileId)
                    .ToListAsync();

                var count = matches.Count;
                _context.Matches.RemoveRange(matches);
                await _context.SaveChangesAsync();
                _metrics?.MatchCreated();

                _logger.LogInformation("Deleted {Count} matches for user {UserProfileId}", count, userProfileId);
                return Ok(count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting matches for user {UserProfileId}", userProfileId);
                return StatusCode(500, "An error occurred while deleting user matches");
            }
        }
    }
}
