using MatchmakingService.Data;
using Microsoft.EntityFrameworkCore;
using MatchmakingService.Models;
using MatchmakingService.Strategies;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MatchmakingService.Controllers
{
    [ApiController]
    [Route("api/matchmaking")]
    public class ProfilesController : ControllerBase
    {
        private readonly StrategyResolver _strategyResolver;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ProfilesController> _logger;
        private readonly MatchmakingDbContext _db;

        public ProfilesController(
            StrategyResolver strategyResolver,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ProfilesController> logger,
            MatchmakingDbContext db)
        {
            _strategyResolver = strategyResolver;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _db = db;
        }

        /// <summary>
        /// GET /api/matchmaking/profiles/{userId}
        /// Returns scored, filtered, ranked candidate profiles for the Discover screen.
        /// T179: Strategy-backed. T180: Optional query params.
        /// </summary>
        [HttpGet("profiles/{userId}")]
        public async Task<IActionResult> GetProfiles(
            string userId,
            [FromQuery] int? limit = null,
            [FromQuery] double? minScore = null,
            [FromQuery] int? activeWithin = null,
            [FromQuery] bool? onlyVerified = null,
            [FromQuery] string? strategy = null)
        {
            try
            {
                // Resolve integer user ID from path (supports Keycloak UUID or integer)
                if (!int.TryParse(userId, out var userIdInt))
                {
                    _logger.LogWarning("Non-integer userId '{UserId}' — strategy requires integer ID", userId);
                    return Ok(new List<object>());
                }

                // Clamp query params (T180 — invalid values → defaults, never error)
                var clampedLimit = Math.Clamp(limit ?? 20, 1, 50);
                var clampedMinScore = Math.Clamp(minScore ?? 0, 0, 100);
                var clampedActiveWithin = activeWithin.HasValue
                    ? Math.Clamp(activeWithin.Value, 1, 365)
                    : (int?)null;

                var request = new CandidateRequest(
                    Limit: clampedLimit,
                    MinScore: clampedMinScore,
                    ActiveWithinDays: clampedActiveWithin,
                    OnlyVerified: onlyVerified ?? false);

                var resolvedStrategy = _strategyResolver.Resolve(strategy);
                var result = await resolvedStrategy.GetCandidatesAsync(userIdInt, request);

                // If strategy returns empty, fall back to legacy UserService demo search
                if (result.Candidates.Count == 0)
                {
                    _logger.LogWarning("Strategy returned 0 candidates for user {UserId}, trying legacy fallback", userId);
                    return await GetProfilesLegacy(userId);
                }

                // Enrich scored candidates with full profile data from UserService
                var enrichment = await FetchUserProfilesAsync(result.Candidates.Select(c => c.Profile.UserId).ToList());

                var response = result.Candidates.Select(c => MapToFlutterShape(c, enrichment)).ToList();

                _logger.LogInformation(
                    "Returning {Count} candidates for user {UserId} via {Strategy} in {Ms}ms",
                    response.Count, userId, result.StrategyUsed,
                    result.ExecutionTime.TotalMilliseconds);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Strategy pipeline failed for user {UserId}, falling back to legacy", userId);
                return await GetProfilesLegacy(userId);
            }
        }

        /// <summary>
        /// Fetch full profile data from UserService for a list of user IDs.
        /// Returns a dictionary of userId → profile JSON for enrichment.
        /// </summary>
        private async Task<Dictionary<int, JsonElement>> FetchUserProfilesAsync(List<int> userIds)
        {
            var profiles = new Dictionary<int, JsonElement>();
            var gatewayUrl = _configuration["Gateway:BaseUrl"] ?? "http://yarp:80";
            var client = _httpClientFactory.CreateClient();

            // Forward the caller's auth token
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);

            // Fetch profiles in parallel (max ~20 candidates)
            var tasks = userIds.Select(async id =>
            {
                try
                {
                    var resp = await client.GetAsync($"{gatewayUrl}/api/userprofiles/{id}");
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync();
                        var doc = JsonDocument.Parse(json);
                        // UserService wraps in { success: true, data: { ... } }
                        if (doc.RootElement.TryGetProperty("data", out var data))
                            return (id, data: (JsonElement?)data.Clone());
                        return (id, data: (JsonElement?)doc.RootElement.Clone());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch profile enrichment for user {UserId}", id);
                }
                return (id, data: (JsonElement?)null);
            });

            var results = await Task.WhenAll(tasks);
            foreach (var (id, data) in results)
            {
                if (data.HasValue)
                    profiles[id] = data.Value;
            }

            _logger.LogDebug("Enriched {Count}/{Total} candidate profiles from UserService", profiles.Count, userIds.Count);
            return profiles;
        }

        /// <summary>
        /// Maps a ScoredCandidate to the JSON shape Flutter expects, enriched with UserService data.
        /// Flutter MatchCandidate.fromJson reads: userId, displayName, age, bio,
        /// city, photoUrl, photoUrls, compatibility/compatibilityScore, interests, etc.
        /// </summary>
        private static object MapToFlutterShape(ScoredCandidate scored, Dictionary<int, JsonElement> enrichment)
        {
            var p = scored.Profile;
            enrichment.TryGetValue(p.UserId, out var userProfile);

            return new
            {
                userId = p.UserId,
                id = p.UserId,
                displayName = GetStringProp(userProfile, "name") ?? $"User {p.UserId}",
                name = GetStringProp(userProfile, "name") ?? $"User {p.UserId}",
                age = p.Age,
                bio = GetStringProp(userProfile, "bio") ?? "",
                city = p.City ?? "",
                gender = p.Gender ?? "",
                compatibility = scored.FinalScore,
                compatibilityScore = scored.CompatibilityScore,
                activityScore = scored.ActivityScore,
                desirabilityScore = scored.DesirabilityScore,
                finalScore = scored.FinalScore,
                strategyUsed = scored.StrategyUsed,
                interests = GetInterests(userProfile, p.Interests),
                isVerified = p.IsVerified,
                photoUrl = GetStringProp(userProfile, "primaryPhotoUrl"),
                photoUrls = GetStringArrayProp(userProfile, "photoUrls"),
                prompts = Array.Empty<object>(),
                voicePromptUrl = (string?)null,
                occupation = GetStringProp(userProfile, "occupation"),
                education = GetStringProp(userProfile, "education"),
                height = GetIntProp(userProfile, "height"),
                distanceKm = (double?)null,
            };
        }

        private static string? GetStringProp(JsonElement? element, string prop)
        {
            if (element == null) return null;
            if (element.Value.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return null;
        }

        private static int? GetIntProp(JsonElement? element, string prop)
        {
            if (element == null) return null;
            if (element.Value.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
                return val.GetInt32();
            return null;
        }

        private static string[] GetStringArrayProp(JsonElement? element, string prop)
        {
            if (element == null) return Array.Empty<string>();
            if (element.Value.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Array)
                return val.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            return Array.Empty<string>();
        }

        private static List<string> GetInterests(JsonElement? userProfile, string? matchmakingInterests)
        {
            // Prefer UserService interests (richer data)
            if (userProfile != null)
            {
                var arr = GetStringArrayProp(userProfile, "interests");
                if (arr.Length > 0) return arr.ToList();
            }
            // Fall back to matchmaking DB interests
            return ParseInterests(matchmakingInterests);
        }

        private static List<string> ParseInterests(string? interests)
        {
            if (string.IsNullOrWhiteSpace(interests)) return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(interests) ?? new List<string>();
            }
            catch
            {
                return interests.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }

        /// <summary>
        /// Legacy fallback: dumb-proxy to UserService. Only used if strategy pipeline throws.
        /// </summary>
        private async Task<IActionResult> GetProfilesLegacy(string userId)
        {
            try
            {
                var userServiceUrl = _configuration["Services:UserService:BaseUrl"]
                    ?? "http://localhost:8082";

                var client = _httpClientFactory.CreateClient();
                var authHeader = Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader))
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);

                int? myProfileId = null;
                try
                {
                    var meResponse = await client.GetAsync($"{userServiceUrl}/api/profiles/me");
                    if (meResponse.IsSuccessStatusCode)
                    {
                        var meContent = await meResponse.Content.ReadAsStringAsync();
                        var meDoc = JsonDocument.Parse(meContent);
                        if (meDoc.RootElement.TryGetProperty("data", out var meData) &&
                            meData.TryGetProperty("id", out var meId))
                        {
                            myProfileId = meId.GetInt32();
                        }
                        meDoc.Dispose();
                    }
                }
                catch { /* best effort */ }

                var searchResponse = await client.PostAsync(
                    $"{userServiceUrl}/api/demo/search",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

                if (!searchResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Legacy demo search returned {StatusCode}", searchResponse.StatusCode);
                    return Ok(new List<object>());
                }

                var content = await searchResponse.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                var results = new List<object>();

                JsonElement profileArray;
                bool found = false;

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("results", out profileArray))
                    found = true;
                else if (doc.RootElement.TryGetProperty("results", out profileArray))
                    found = true;

                // Bot filtering: if requester is a bot, exclude other bots from results
                var botUserIds = new HashSet<int>();
                if (int.TryParse(userId, out var legacyUid))
                {
                    var requester = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == legacyUid);
                    if (requester?.IsBot == true)
                    {
                        botUserIds = (await _db.UserProfiles.AsNoTracking()
                            .Where(u => u.IsBot && u.UserId != legacyUid)
                            .Select(u => u.UserId)
                            .ToListAsync())
                            .ToHashSet();
                        _logger.LogInformation("Bot user {UserId} — filtering out {Count} other bot IDs from legacy results", userId, botUserIds.Count);
                    }
                }

                if (found)
                {
                    foreach (var profile in profileArray.EnumerateArray())
                    {
                        if (myProfileId.HasValue &&
                            profile.TryGetProperty("id", out var idProp) &&
                            idProp.GetInt32() == myProfileId.Value)
                            continue;

                        // Skip other bots if requester is a bot
                        if (botUserIds.Count > 0 && profile.TryGetProperty("id", out var botIdProp) &&
                            botUserIds.Contains(botIdProp.GetInt32()))
                            continue;

                        var obj = JsonSerializer.Deserialize<object>(profile.GetRawText());
                        if (obj != null) results.Add(obj);
                    }
                }

                doc.Dispose();

                _logger.LogWarning("Returning {Count} UNSCORED profiles via legacy fallback for user {UserId}",
                    results.Count, userId);

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy fallback also failed for user {UserId}", userId);
                return StatusCode(500, "Error fetching profiles");
            }
        }
    }
}
