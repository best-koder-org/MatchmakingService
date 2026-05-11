using System.Text.Json;
using MatchmakingService.Data;
using MatchmakingService.Models;
using Microsoft.EntityFrameworkCore;

namespace MatchmakingService.Services
{
    /// <summary>
    /// Generates and persists <see cref="MatchInsight"/> rows for a newly created match
    /// (spec 005 T532/T533). Asymmetric per-user storage — one row per side so that
    /// future viewer-specific framing has a stable schema to land on.
    /// </summary>
    public interface IMatchInsightService
    {
        /// <summary>
        /// Generates insight rows for both users in the match. Idempotent — existing
        /// rows for the (matchId, keycloakId) pair are left alone. Never throws on the
        /// happy path; logs and swallows on errors since insight is a soft enrichment.
        /// </summary>
        Task GenerateForMatchAsync(int matchId, int user1Id, int user2Id, double? fallbackScore, CancellationToken ct = default);
    }

    public class MatchInsightService : IMatchInsightService
    {
        private readonly MatchmakingDbContext _db;
        private readonly ICompatibilityScorer _scorer;
        private readonly ILogger<MatchInsightService> _logger;

        public MatchInsightService(
            MatchmakingDbContext db,
            ICompatibilityScorer scorer,
            ILogger<MatchInsightService> logger)
        {
            _db = db;
            _scorer = scorer;
            _logger = logger;
        }

        public async Task GenerateForMatchAsync(
            int matchId, int user1Id, int user2Id, double? fallbackScore, CancellationToken ct = default)
        {
            try
            {
                var profiles = await _db.UserProfiles
                    .Where(p => p.UserId == user1Id || p.UserId == user2Id)
                    .Select(p => new { p.UserId, p.KeycloakId })
                    .ToListAsync(ct);

                var kc1 = profiles.FirstOrDefault(p => p.UserId == user1Id)?.KeycloakId;
                var kc2 = profiles.FirstOrDefault(p => p.UserId == user2Id)?.KeycloakId;

                if (string.IsNullOrWhiteSpace(kc1) || string.IsNullOrWhiteSpace(kc2))
                {
                    _logger.LogDebug(
                        "Skip MatchInsight generation for match {MatchId}: missing KeycloakId (u1={U1} u2={U2})",
                        matchId, user1Id, user2Id);
                    return;
                }

                var compat = await _scorer.ScoreAsync(kc1!, kc2!, ct);
                var overall = compat.SharedAnswerCount > 0 ? compat.OverallScore : (fallbackScore ?? 50.0);

                var reasonsJson = JsonSerializer.Serialize(compat.TopReasons);
                var frictionJson = JsonSerializer.Serialize(compat.FrictionPoints);
                const string growthJson = "[]"; // Reserved for T540+ complementary-strengths work.

                // Look up existing rows so the call is idempotent under retries.
                var existing = await _db.MatchInsights
                    .Where(mi => mi.MatchId == matchId && (mi.ForKeycloakId == kc1 || mi.ForKeycloakId == kc2))
                    .Select(mi => mi.ForKeycloakId)
                    .ToListAsync(ct);

                foreach (var kc in new[] { kc1!, kc2! })
                {
                    if (existing.Contains(kc)) continue;

                    _db.MatchInsights.Add(new MatchInsight
                    {
                        MatchId = matchId,
                        ForKeycloakId = kc,
                        ReasonsJson = reasonsJson,
                        FrictionJson = frictionJson,
                        GrowthJson = growthJson,
                        OverallScore = overall,
                        CreatedAt = DateTime.UtcNow,
                    });
                }

                await _db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "MatchInsight rows generated for match {MatchId}: score={Score:F1} shared={Shared} reasons={Reasons} frictions={Frictions}",
                    matchId, overall, compat.SharedAnswerCount, compat.TopReasons.Count, compat.FrictionPoints.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MatchInsight generation failed for match {MatchId} — non-fatal", matchId);
            }
        }
    }
}
