namespace MatchmakingService.Services
{
    /// <summary>
    /// Computes pairwise compatibility between two users from their question answers.
    /// Spec 005 Phase 3 (T520).
    /// </summary>
    public interface ICompatibilityScorer
    {
        /// <summary>
        /// Calculates compatibility for the given user pair. Loads both users' answers
        /// from the DbContext provided to the implementation.
        /// </summary>
        /// <param name="keycloakIdA">First user (order does not matter).</param>
        /// <param name="keycloakIdB">Second user.</param>
        Task<CompatibilityResult> ScoreAsync(string keycloakIdA, string keycloakIdB, CancellationToken ct = default);
    }

    /// <summary>Result of pairwise compatibility scoring.</summary>
    public sealed record CompatibilityResult(
        double OverallScore,
        double PersonalityScore,
        double ValuesScore,
        double AttachmentScore,
        double LifestyleScore,
        int SharedAnswerCount,
        IReadOnlyList<string> TopReasons,
        IReadOnlyList<string> FrictionPoints
    )
    {
        /// <summary>Default neutral result when one or both users have no answers.</summary>
        public static CompatibilityResult Neutral() => new(
            50.0, 50.0, 50.0, 50.0, 50.0, 0,
            Array.Empty<string>(), Array.Empty<string>());
    }
}
