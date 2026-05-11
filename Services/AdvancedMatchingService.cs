using MatchmakingService.Data;
using MatchmakingService.Models;
using MatchmakingService.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MatchmakingService.Services
{
    public interface IAdvancedMatchingService
    {
        Task<List<MatchSuggestionResponse>> FindMatchesAsync(FindMatchesRequest request);
        Task<double> CalculateCompatibilityScoreAsync(int userId, int targetUserId);
        Task UpdateUserProfileAsync(UserProfile profile);
        Task RecordSwipeHistoryAsync(SwipeHistoryRequest request);
        Task<MatchStatsResponse> GetMatchStatsAsync(int userId);
    }

    public class AdvancedMatchingService : IAdvancedMatchingService
    {
        private readonly MatchmakingDbContext _context;
        private readonly IUserServiceClient _userServiceClient;
        private readonly ISafetyServiceClient _safetyServiceClient;
        private readonly ISwipeServiceClient _swipeServiceClient;
        private readonly IOptionsMonitor<ScoringConfiguration> _scoringConfig;
        private readonly ILogger<AdvancedMatchingService> _logger;
        private readonly IDailySuggestionTracker _suggestionTracker;
        private readonly ICompatibilityScorer? _compatibilityScorer;

        public AdvancedMatchingService(
            MatchmakingDbContext context,
            IUserServiceClient userServiceClient,
            ISafetyServiceClient safetyServiceClient,
            ISwipeServiceClient swipeServiceClient,
            IOptionsMonitor<ScoringConfiguration> scoringConfig,
            ILogger<AdvancedMatchingService> logger,
            IDailySuggestionTracker suggestionTracker,
            ICompatibilityScorer? compatibilityScorer = null)
        {
            _context = context;
            _userServiceClient = userServiceClient;
            _safetyServiceClient = safetyServiceClient;
            _swipeServiceClient = swipeServiceClient;
            _scoringConfig = scoringConfig;
            _logger = logger;
            _suggestionTracker = suggestionTracker;
            _compatibilityScorer = compatibilityScorer;
        }

        public async Task<List<MatchSuggestionResponse>> FindMatchesAsync(FindMatchesRequest request)
        {
            try
            {
                var userProfile = await GetUserProfileAsync(request.UserId);
                if (userProfile == null)
                {
                    _logger.LogWarning("User profile not found for user {UserId}", request.UserId);
                    return new List<MatchSuggestionResponse>();
                }

                // Check daily suggestion limit
                var isPremium = request.IsPremium ?? false;
                var (allowed, remaining) = await _suggestionTracker.CheckAndIncrementAsync(request.UserId, isPremium);

                if (!allowed)
                {
                    _logger.LogInformation("User {UserId} has reached daily suggestion limit", request.UserId);
                    return new List<MatchSuggestionResponse>();
                }

                // Get previously swiped users to exclude
                var swipedUserIds = new HashSet<int>();
                if (request.ExcludePreviouslySwiped)
                {
                    swipedUserIds = await GetSwipedUserIdsAsync(request.UserId);
                }

                // Get blocked users to exclude (critical for safety)
                var blockedUserIds = await _safetyServiceClient.GetBlockedUserIdsAsync(request.UserId);
                foreach (var blockedId in blockedUserIds)
                {
                    swipedUserIds.Add(blockedId); // Add to exclusion list
                }

                _logger.LogInformation("Excluding {BlockedCount} blocked users from matchmaking for user {UserId}",
                    blockedUserIds.Count, request.UserId);

                // Get potential matches based on basic criteria
                var potentialMatches = await GetPotentialMatchesQuery(userProfile, swipedUserIds)
                    .Take(request.Limit * 3) // Get more than needed for better filtering
                    .ToListAsync();

                var config = _scoringConfig.CurrentValue;
                var minScore = request.MinScore ?? config.MinimumCompatibilityThreshold;
                var suggestions = new List<MatchSuggestionResponse>();

                foreach (var targetProfile in potentialMatches)
                {
                    if (suggestions.Count >= request.Limit) break;

                    var compatibilityScore = await CalculateCompatibilityScoreAsync(request.UserId, targetProfile.UserId);

                    if (compatibilityScore >= minScore)
                    {
                        var suggestion = await CreateMatchSuggestion(userProfile, targetProfile, compatibilityScore);
                        suggestions.Add(suggestion);
                    }
                }

                // Sort by compatibility score descending
                suggestions = suggestions.OrderByDescending(s => s.CompatibilityScore).ToList();

                // Update algorithm metrics
                await UpdateAlgorithmMetricsAsync(request.UserId, suggestions.Count);

                return suggestions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error finding matches for user {UserId}", request.UserId);
                throw;
            }
        }

        public async Task<double> CalculateCompatibilityScoreAsync(int userId, int targetUserId)
        {
            var userProfile = await GetUserProfileAsync(userId);
            var targetProfile = await GetUserProfileAsync(targetUserId);

            if (userProfile == null || targetProfile == null)
                return 0.0;

            var config = _scoringConfig.CurrentValue;

            // Check if we have a cached score
            var cachedScore = await _context.MatchScores
                .AsNoTracking()
                .FirstOrDefaultAsync(ms => ms.UserId == userId && ms.TargetUserId == targetUserId && ms.IsValid);

            if (cachedScore != null && (DateTime.UtcNow - cachedScore.CalculatedAt).TotalHours < config.ScoreCacheHours)
            {
                return cachedScore.OverallScore;
            }

            // Calculate critical compatibility factors first - these are dealbreakers
            var ageScore = CalculateAgeScore(userProfile, targetProfile);
            if (ageScore == 0.0)
                return 0.0; // Age incompatibility is a dealbreaker

            var locationScore = CalculateLocationScore(userProfile, targetProfile);
            if (locationScore == 0.0)
                return 0.0; // Beyond max distance is a dealbreaker

            // Calculate compatibility scores
            var interestsScore = CalculateInterestsScore(userProfile, targetProfile);
            var educationScore = CalculateEducationScore(userProfile, targetProfile);
            var lifestyleScore = CalculateLifestyleScore(userProfile, targetProfile);
            var activityScore = await CalculateActivityScore(userId, targetUserId);

            // Apply user's preference weights + configurable activity weight
            var activityWeight = config.ActivityScoreWeight;
            var weightedScore =
                (locationScore * userProfile.LocationWeight) +
                (ageScore * userProfile.AgeWeight) +
                (interestsScore * userProfile.InterestsWeight) +
                (educationScore * userProfile.EducationWeight) +
                (lifestyleScore * userProfile.LifestyleWeight) +
                (activityScore * activityWeight);

            // Normalize to 0-100 scale
            var totalWeight = userProfile.LocationWeight + userProfile.AgeWeight +
                             userProfile.InterestsWeight + userProfile.EducationWeight +
                             userProfile.LifestyleWeight + activityWeight;

            var overallScore = Math.Min(100, weightedScore / totalWeight);

            // T530 (spec 005): Blend pairwise compatibility (from question answers) into the
            // overall score. Fall back gracefully when either user has no answers or no
            // shared questions — CompatibilityScorer returns a Neutral result in that case
            // and we leave the legacy score unchanged so onboarded users with answers can
            // rank higher than users without breaking matching for everyone else.
            var compatWeight = config.CompatibilityWeight;
            if (_compatibilityScorer != null
                && compatWeight > 0
                && !string.IsNullOrWhiteSpace(userProfile.KeycloakId)
                && !string.IsNullOrWhiteSpace(targetProfile.KeycloakId))
            {
                try
                {
                    var compat = await _compatibilityScorer.ScoreAsync(
                        userProfile.KeycloakId!, targetProfile.KeycloakId!);

                    if (compat.SharedAnswerCount > 0)
                    {
                        // Blend: final = legacy * (1 - w) + compat * w
                        overallScore = Math.Min(100,
                            (overallScore * (1.0 - compatWeight)) + (compat.OverallScore * compatWeight));

                        _logger.LogDebug(
                            "Compatibility blend for {UserId}↔{TargetUserId}: legacy={Legacy:F1} compat={Compat:F1} w={Weight:F2} final={Final:F1} shared={Shared}",
                            userId, targetUserId, weightedScore / totalWeight, compat.OverallScore, compatWeight, overallScore, compat.SharedAnswerCount);
                    }
                }
                catch (Exception ex)
                {
                    // Compatibility is a soft input — never let it break candidate scoring.
                    _logger.LogWarning(ex,
                        "Compatibility scoring failed for {UserId}↔{TargetUserId}, using legacy score only",
                        userId, targetUserId);
                }
            }

            // Cache the score
            await CacheMatchScore(userId, targetUserId, overallScore, locationScore, ageScore,
                                 interestsScore, educationScore, lifestyleScore, activityScore);

            return overallScore;
        }

        private double CalculateLocationScore(UserProfile user, UserProfile target)
        {
            var distance = CalculateDistance(user.Latitude, user.Longitude, target.Latitude, target.Longitude);

            if (distance > user.MaxDistance)
                return 0.0;

            // Score decreases with distance, max score at 0km, min score at maxDistance
            return Math.Max(0, 100 - (distance / user.MaxDistance * 100));
        }

        private double CalculateAgeScore(UserProfile user, UserProfile target)
        {
            if (target.Age < user.MinAge || target.Age > user.MaxAge)
                return 0.0;

            // Ideal age range gets full score, score decreases towards limits
            var ageRange = user.MaxAge - user.MinAge;
            var idealAge = user.MinAge + (ageRange / 2);
            var ageDifference = Math.Abs(target.Age - idealAge);
            var maxDifference = ageRange / 2;

            return Math.Max(0, 100 - (ageDifference / maxDifference * 50));
        }

        private double CalculateInterestsScore(UserProfile user, UserProfile target)
        {
            try
            {
                var userInterests = JsonSerializer.Deserialize<List<string>>(user.Interests ?? "[]") ?? new List<string>();
                var targetInterests = JsonSerializer.Deserialize<List<string>>(target.Interests ?? "[]") ?? new List<string>();

                if (!userInterests.Any() || !targetInterests.Any())
                    return 50.0; // Neutral score if no interests

                var commonInterests = userInterests.Intersect(targetInterests, StringComparer.OrdinalIgnoreCase).Count();
                var totalUniqueInterests = userInterests.Union(targetInterests, StringComparer.OrdinalIgnoreCase).Count();

                if (totalUniqueInterests == 0)
                    return 50.0;

                // Jaccard similarity * 100
                return (double)commonInterests / totalUniqueInterests * 100;
            }
            catch
            {
                return 50.0; // Neutral score on error
            }
        }

        private double CalculateEducationScore(UserProfile user, UserProfile target)
        {
            if (string.IsNullOrEmpty(user.Education) || string.IsNullOrEmpty(target.Education))
                return 70.0; // Neutral-positive score

            var educationLevels = new Dictionary<string, int>
            {
                { "High School", 1 },
                { "Some College", 2 },
                { "Bachelor's", 3 },
                { "Master's", 4 },
                { "PhD", 5 },
                { "Other", 2 }
            };

            if (!educationLevels.TryGetValue(user.Education, out var userLevel) ||
                !educationLevels.TryGetValue(target.Education, out var targetLevel))
                return 70.0;

            var difference = Math.Abs(userLevel - targetLevel);
            return Math.Max(50, 100 - (difference * 15)); // Max penalty of 50 points
        }

        private double CalculateLifestyleScore(UserProfile user, UserProfile target)
        {
            var config = _scoringConfig.CurrentValue;
            var score = 100.0;

            // Children compatibility — uses configurable penalties
            if (user.WantsChildren != target.WantsChildren)
                score -= config.ChildrenMismatchPenalty;

            if (user.HasChildren != target.HasChildren && (user.HasChildren || target.HasChildren))
                score -= config.HasChildrenMismatchPenalty;

            // Smoking compatibility — uses configurable max penalty
            score -= GetLifestylePenalty(user.SmokingStatus, target.SmokingStatus, config.SmokingMismatchPenalty);

            // Drinking compatibility — uses configurable max penalty
            score -= GetLifestylePenalty(user.DrinkingStatus, target.DrinkingStatus, config.DrinkingMismatchPenalty);

            // Religion compatibility — uses configurable penalty
            if (!string.IsNullOrEmpty(user.Religion) && !string.IsNullOrEmpty(target.Religion) &&
                !user.Religion.Equals(target.Religion, StringComparison.OrdinalIgnoreCase))
                score -= config.ReligionMismatchPenalty;

            return Math.Max(0, score);
        }

        private double GetLifestylePenalty(string userStatus, string targetStatus, double maxPenalty)
        {
            if (string.IsNullOrEmpty(userStatus) || string.IsNullOrEmpty(targetStatus))
                return 0;

            var statusScore = new Dictionary<string, int>
            {
                { "Never", 0 },
                { "Sometimes", 1 },
                { "Often", 2 }
            };

            if (!statusScore.TryGetValue(userStatus, out var userScore) ||
                !statusScore.TryGetValue(targetStatus, out var targetScore))
                return 0;

            var difference = Math.Abs(userScore - targetScore);
            return difference * (maxPenalty / 2);
        }

        private async Task<double> CalculateActivityScore(int userId, int targetUserId)
        {
            var config = _scoringConfig.CurrentValue;
            var halfLifeDays = config.ActivityScoreHalfLifeDays;

            // Use target user's UpdatedAt as proxy for last active time
            // (will be replaced with dedicated LastActiveAt field in T164)
            var targetProfile = await _context.UserProfiles
                .AsNoTracking()
                .Where(up => up.UserId == targetUserId)
                .Select(up => new { up.UpdatedAt })
                .FirstOrDefaultAsync();

            if (targetProfile == null)
                return 0.0;

            var daysSinceActive = (DateTime.UtcNow - targetProfile.UpdatedAt).TotalDays;

            if (daysSinceActive <= 0)
                return 100.0; // Active right now

            // Exponential decay: score = 100 * e^(-ln(2)/halfLife * days)
            // At halfLifeDays, score = 50. At 2*halfLifeDays, score = 25. Etc.
            var decayRate = Math.Log(2) / halfLifeDays;
            var score = 100.0 * Math.Exp(-decayRate * daysSinceActive);

            return Math.Max(0, Math.Round(score, 1));
        }

        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371; // Earth's radius in kilometers

            var dLat = ToRadians(lat2 - lat1);
            var dLon = ToRadians(lon2 - lon1);

            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private double ToRadians(double degrees) => degrees * Math.PI / 180;

        private async Task<UserProfile?> GetUserProfileAsync(int userId)
        {
            return await _context.UserProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(up => up.UserId == userId && up.IsActive);
        }

        private async Task<HashSet<int>> GetSwipedUserIdsAsync(int userId)
        {
            // Query SwipeService for all swiped user IDs (both likes and passes)
            var swipedIds = await _swipeServiceClient.GetSwipedUserIdsAsync(userId);

            // Also check local UserInteractions table as a fallback/supplement
            var localSwipedIds = await _context.UserInteractions
                .AsNoTracking()
                .Where(ui => ui.UserId == userId)
                .Select(ui => ui.TargetUserId)
                .ToListAsync();

            foreach (var id in localSwipedIds)
            {
                swipedIds.Add(id);
            }

            _logger.LogDebug(
                "Total swiped user IDs for user {UserId}: {Count} (remote: {RemoteCount}, local: {LocalCount})",
                userId, swipedIds.Count, swipedIds.Count - localSwipedIds.Count, localSwipedIds.Count);

            return swipedIds;
        }

        private IQueryable<UserProfile> GetPotentialMatchesQuery(UserProfile userProfile, HashSet<int> excludeUserIds)
        {
            return _context.UserProfiles
                .AsNoTracking()
                .Where(up => up.UserId != userProfile.UserId && up.IsActive)
                .Where(up => !excludeUserIds.Contains(up.UserId))
                .Where(up => up.Gender == userProfile.PreferredGender)
                .Where(up => up.Age >= userProfile.MinAge && up.Age <= userProfile.MaxAge)
                .Where(up => up.PreferredGender == userProfile.Gender)
                .Where(up => userProfile.Age >= up.MinAge && userProfile.Age <= up.MaxAge);
        }

        private async Task<MatchSuggestionResponse> CreateMatchSuggestion(
            UserProfile userProfile, UserProfile targetProfile, double compatibilityScore)
        {
            var distance = CalculateDistance(userProfile.Latitude, userProfile.Longitude,
                                           targetProfile.Latitude, targetProfile.Longitude);

            var interests = new List<string>();
            try
            {
                interests = JsonSerializer.Deserialize<List<string>>(targetProfile.Interests ?? "[]") ?? new List<string>();
            }
            catch { }

            return new MatchSuggestionResponse
            {
                UserId = userProfile.UserId,
                TargetUserId = targetProfile.UserId,
                CompatibilityScore = Math.Round(compatibilityScore, 1),
                UserProfile = new UserProfileSummary
                {
                    UserId = targetProfile.UserId,
                    Gender = targetProfile.Gender,
                    Age = targetProfile.Age,
                    City = targetProfile.City,
                    Distance = Math.Round(distance, 1),
                    Interests = interests,
                    Education = targetProfile.Education,
                    Occupation = targetProfile.Occupation,
                    Height = targetProfile.Height
                },
                MatchReason = GenerateMatchReason(userProfile, targetProfile, compatibilityScore)
            };
        }

        private string GenerateMatchReason(UserProfile user, UserProfile target, double score)
        {
            var reasons = new List<string>();

            var distance = CalculateDistance(user.Latitude, user.Longitude, target.Latitude, target.Longitude);
            if (distance < 10)
                reasons.Add("lives nearby");

            if (Math.Abs(user.Age - target.Age) <= 3)
                reasons.Add("similar age");

            if (user.Education == target.Education)
                reasons.Add("same education level");

            try
            {
                var userInterests = JsonSerializer.Deserialize<List<string>>(user.Interests ?? "[]") ?? new List<string>();
                var targetInterests = JsonSerializer.Deserialize<List<string>>(target.Interests ?? "[]") ?? new List<string>();
                var commonInterests = userInterests.Intersect(targetInterests, StringComparer.OrdinalIgnoreCase).Count();

                if (commonInterests > 0)
                    reasons.Add($"{commonInterests} shared interest{(commonInterests > 1 ? "s" : "")}");
            }
            catch { }

            if (score >= 90)
                reasons.Insert(0, "excellent compatibility");
            else if (score >= 80)
                reasons.Insert(0, "great compatibility");

            return reasons.Any() ? string.Join(", ", reasons) : "potential match";
        }

        private async Task CacheMatchScore(int userId, int targetUserId, double overallScore,
            double locationScore, double ageScore, double interestsScore,
            double educationScore, double lifestyleScore, double activityScore)
        {
            var matchScore = new MatchScore
            {
                UserId = userId,
                TargetUserId = targetUserId,
                OverallScore = overallScore,
                LocationScore = locationScore,
                AgeScore = ageScore,
                InterestsScore = interestsScore,
                EducationScore = educationScore,
                LifestyleScore = lifestyleScore,
                ActivityScore = activityScore,
                CalculatedAt = DateTime.UtcNow,
                IsValid = true
            };

            // Remove old cached scores
            var oldScores = _context.MatchScores
                .Where(ms => ms.UserId == userId && ms.TargetUserId == targetUserId);
            _context.MatchScores.RemoveRange(oldScores);

            _context.MatchScores.Add(matchScore);
            await _context.SaveChangesAsync();
        }

        private async Task UpdateAlgorithmMetricsAsync(int userId, int suggestionsCount)
        {
            var today = DateTime.UtcNow.Date;
            var metric = await _context.MatchingAlgorithmMetrics
                .FirstOrDefaultAsync(m => m.UserId == userId && m.CalculatedAt.Date == today);

            if (metric == null)
            {
                metric = new MatchingAlgorithmMetric
                {
                    UserId = userId,
                    AlgorithmVersion = "v1.0",
                    SuggestionsGenerated = suggestionsCount,
                    CalculatedAt = DateTime.UtcNow
                };
                _context.MatchingAlgorithmMetrics.Add(metric);
            }
            else
            {
                metric.SuggestionsGenerated += suggestionsCount;
            }

            await _context.SaveChangesAsync();
        }

        public async Task UpdateUserProfileAsync(UserProfile profile)
        {
            profile.UpdatedAt = DateTime.UtcNow;
            _context.UserProfiles.Update(profile);
            await _context.SaveChangesAsync();
        }

        public async Task RecordSwipeHistoryAsync(SwipeHistoryRequest request)
        {
            // Invalidate cached scores for swiped users
            var cachedScores = _context.MatchScores
                .Where(ms => ms.UserId == request.UserId &&
                            request.SwipedUserIds.Contains(ms.TargetUserId));

            foreach (var score in cachedScores)
            {
                score.IsValid = false;
            }

            await _context.SaveChangesAsync();
        }

        public async Task<MatchStatsResponse> GetMatchStatsAsync(int userId)
        {
            var totalMatches = await _context.Matches
                .AsNoTracking()
                .CountAsync(m => (m.User1Id == userId || m.User2Id == userId));

            var activeMatches = await _context.Matches
                .AsNoTracking()
                .CountAsync(m => (m.User1Id == userId || m.User2Id == userId) && m.IsActive);

            var lastMatch = await _context.Matches
                .AsNoTracking()
                .Where(m => m.User1Id == userId || m.User2Id == userId)
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync();

            var avgScore = await _context.MatchScores
                .AsNoTracking()
                .Where(ms => ms.UserId == userId && ms.IsValid)
                .AverageAsync(ms => (double?)ms.OverallScore) ?? 0.0;

            return new MatchStatsResponse
            {
                TotalMatches = totalMatches,
                ActiveMatches = activeMatches,
                MessagesReceived = 0, // Would need to integrate with messaging service
                AverageCompatibilityScore = Math.Round(avgScore, 1),
                LastMatchAt = lastMatch?.CreatedAt ?? DateTime.MinValue,
                TopMatchReasons = new List<string> { "shared interests", "similar age", "lives nearby" }
            };
        }
    }
}
