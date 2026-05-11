using System.ComponentModel.DataAnnotations;

namespace MatchmakingService.Models
{
    /// <summary>
    /// Cached pairwise compatibility score between two users (T521 / spec 005 Phase 3).
    /// Key pair is stored alphabetically: <see cref="KeycloakId1"/> &lt; <see cref="KeycloakId2"/>.
    /// </summary>
    public class CompatibilityScore
    {
        public int Id { get; set; }

        [Required, MaxLength(50)]
        public string KeycloakId1 { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string KeycloakId2 { get; set; } = string.Empty;

        /// <summary>Overall weighted compatibility 0-100.</summary>
        public double OverallScore { get; set; }

        public double PersonalityScore { get; set; }
        public double ValuesScore { get; set; }
        public double AttachmentScore { get; set; }
        public double LifestyleScore { get; set; }

        /// <summary>How many questions both users answered.</summary>
        public int SharedAnswerCount { get; set; }

        /// <summary>JSON array of short positive reasons (top matches per category).</summary>
        public string TopReasonsJson { get; set; } = "[]";

        /// <summary>JSON array of friction points (largest disagreements).</summary>
        public string FrictionPointsJson { get; set; } = "[]";

        public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;
    }
}
