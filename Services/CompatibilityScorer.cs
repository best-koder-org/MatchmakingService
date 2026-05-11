using System.Text.Json;
using MatchmakingService.Data;
using MatchmakingService.Models;
using Microsoft.EntityFrameworkCore;

namespace MatchmakingService.Services
{
    /// <summary>
    /// Categorical compatibility scorer (T520, spec 005 Phase 3).
    ///
    /// Scoring approach for the categorical+voice schema:
    /// - For each question both users answered (tap+tap), compute per-question agreement
    ///   in [0,1] using ordinal distance over the option list:
    ///       agreement = 1 - |valueA - valueB| / (optionCount - 1)
    /// - Voice answers (Value=0, AnswerType="voice") get a base agreement of 0.7 — they
    ///   indicate engagement but cannot be directly compared without embeddings.
    ///   Future work: use VoiceTranscript + embeddings for richer scoring.
    /// - Per-question score weighted by Question.Weight.
    /// - Per-category score = weighted-average per-question agreement * 100.
    /// - Overall = weighted average of category scores using DefaultCategoryWeights.
    /// - TopReasons: up to 3 questions with the highest agreement (>= 0.8).
    /// - FrictionPoints: up to 2 questions with the lowest agreement (&lt; 0.4).
    ///
    /// If either user has no answers, returns <see cref="CompatibilityResult.Neutral"/>.
    /// </summary>
    public class CompatibilityScorer : ICompatibilityScorer
    {
        private readonly MatchmakingDbContext _db;
        private readonly ILogger<CompatibilityScorer> _logger;

        // Tunable weights — Phase 4 (T531) will move these to ScoringConfiguration.
        public static readonly IReadOnlyDictionary<QuestionCategory, double> DefaultCategoryWeights =
            new Dictionary<QuestionCategory, double>
            {
                [QuestionCategory.Personality] = 0.30,
                [QuestionCategory.Values]      = 0.30,
                [QuestionCategory.Attachment]  = 0.20,
                [QuestionCategory.Lifestyle]   = 0.20,
            };

        private const double VoiceBaseAgreement = 0.7;

        public CompatibilityScorer(MatchmakingDbContext db, ILogger<CompatibilityScorer> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<CompatibilityResult> ScoreAsync(string keycloakIdA, string keycloakIdB, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(keycloakIdA) || string.IsNullOrWhiteSpace(keycloakIdB))
                return CompatibilityResult.Neutral();
            if (string.Equals(keycloakIdA, keycloakIdB, StringComparison.Ordinal))
                return CompatibilityResult.Neutral();

            var ids = new[] { keycloakIdA, keycloakIdB };
            var answers = await _db.UserQuestionAnswers
                .Where(a => ids.Contains(a.KeycloakId))
                .ToListAsync(ct);

            if (answers.Count == 0)
                return CompatibilityResult.Neutral();

            var byUser = answers.GroupBy(a => a.KeycloakId)
                .ToDictionary(g => g.Key, g => g.ToList());

            if (!byUser.ContainsKey(keycloakIdA) || !byUser.ContainsKey(keycloakIdB))
                return CompatibilityResult.Neutral();

            var aById = byUser[keycloakIdA].ToDictionary(a => a.QuestionId);
            var bById = byUser[keycloakIdB].ToDictionary(a => a.QuestionId);
            var sharedQuestionIds = aById.Keys.Intersect(bById.Keys).ToList();
            if (sharedQuestionIds.Count == 0)
                return CompatibilityResult.Neutral();

            var questions = await _db.CompatibilityQuestions
                .Where(q => sharedQuestionIds.Contains(q.Id))
                .ToListAsync(ct);
            var questionsById = questions.ToDictionary(q => q.Id);

            // Per-question records for category aggregation + reasons/frictions.
            var perQuestion = new List<(CompatibilityQuestion Q, double Agreement, double Weight)>();

            foreach (var qid in sharedQuestionIds)
            {
                if (!questionsById.TryGetValue(qid, out var q)) continue;
                if (!q.IsActive) continue;

                var agreement = CalculateAgreement(q, aById[qid], bById[qid]);
                perQuestion.Add((q, agreement, q.Weight));
            }

            if (perQuestion.Count == 0)
                return CompatibilityResult.Neutral();

            // Aggregate per category.
            var categoryScores = new Dictionary<QuestionCategory, double>();
            foreach (var category in DefaultCategoryWeights.Keys)
            {
                var inCat = perQuestion.Where(p => p.Q.Category == category).ToList();
                if (inCat.Count == 0) continue;
                var totalWeight = inCat.Sum(p => p.Weight);
                if (totalWeight <= 0) continue;
                var weighted = inCat.Sum(p => p.Agreement * p.Weight) / totalWeight;
                categoryScores[category] = weighted * 100.0;
            }

            // Overall = weighted average across categories the pair has data for.
            double overall;
            if (categoryScores.Count == 0)
            {
                overall = 50.0;
            }
            else
            {
                var totalCatWeight = categoryScores.Keys.Sum(c => DefaultCategoryWeights[c]);
                overall = categoryScores.Sum(kv => kv.Value * DefaultCategoryWeights[kv.Key]) / totalCatWeight;
            }

            // Reasons / frictions: pick by extreme per-question agreement.
            var topReasons = perQuestion
                .Where(p => p.Agreement >= 0.8)
                .OrderByDescending(p => p.Agreement)
                .ThenByDescending(p => p.Weight)
                .Take(3)
                .Select(p => $"You both agree on \"{p.Q.TextEn}\"")
                .ToList();

            var frictions = perQuestion
                .Where(p => p.Agreement < 0.4)
                .OrderBy(p => p.Agreement)
                .ThenByDescending(p => p.Weight)
                .Take(2)
                .Select(p => $"Different views on \"{p.Q.TextEn}\"")
                .ToList();

            _logger.LogDebug(
                "Compatibility {A}↔{B}: shared={Shared}, overall={Overall:F1}",
                keycloakIdA, keycloakIdB, perQuestion.Count, overall);

            return new CompatibilityResult(
                Math.Round(overall, 1),
                categoryScores.TryGetValue(QuestionCategory.Personality, out var pe) ? Math.Round(pe, 1) : 50.0,
                categoryScores.TryGetValue(QuestionCategory.Values, out var va) ? Math.Round(va, 1) : 50.0,
                categoryScores.TryGetValue(QuestionCategory.Attachment, out var at) ? Math.Round(at, 1) : 50.0,
                categoryScores.TryGetValue(QuestionCategory.Lifestyle, out var li) ? Math.Round(li, 1) : 50.0,
                perQuestion.Count,
                topReasons,
                frictions
            );
        }

        /// <summary>Compute agreement [0,1] between two answers to the same question.</summary>
        internal static double CalculateAgreement(CompatibilityQuestion q, UserQuestionAnswer a, UserQuestionAnswer b)
        {
            // Any voice answer: use base score (we don't compare transcripts yet).
            if (a.AnswerType == "voice" || b.AnswerType == "voice")
                return VoiceBaseAgreement;

            if (a.Value == b.Value) return 1.0;

            var optionCount = CountOptions(q.OptionsJson);
            if (optionCount <= 1) return 0.0;

            // Treat option positions as ordinal; partial credit for adjacent options.
            var diff = Math.Abs(a.Value - b.Value);
            return Math.Max(0.0, 1.0 - (double)diff / (optionCount - 1));
        }

        private static int CountOptions(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.GetArrayLength()
                    : 0;
            }
            catch (JsonException)
            {
                return 0;
            }
        }
    }
}
