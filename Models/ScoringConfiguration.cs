namespace MatchmakingService.Models
{
    /// <summary>
    /// Recommended scoring weights and algorithm configuration
    /// These values have been tuned based on testing and can be customized per-user
    /// </summary>
    public class ScoringConfiguration
    {
        /// <summary>
        /// Default weight for location/distance factor (0-10 scale)
        /// Higher = distance matters more
        /// Recommendation: 1.5 - Proximity is nice but not critical
        /// </summary>
        public double DefaultLocationWeight { get; set; } = 1.5;

        /// <summary>
        /// Default weight for age compatibility (0-10 scale)
        /// Higher = age matching matters more
        /// Recommendation: 2.0 - Age range is important for relationship compatibility
        /// </summary>
        public double DefaultAgeWeight { get; set; } = 2.0;

        /// <summary>
        /// Default weight for shared interests (0-10 scale)
        /// Higher = common hobbies/interests matter more
        /// Recommendation: 1.8 - Shared interests help connection but aren't everything
        /// </summary>
        public double DefaultInterestsWeight { get; set; } = 1.8;

        /// <summary>
        /// Default weight for education level compatibility (0-10 scale)
        /// Higher = similar education matters more
        /// Recommendation: 1.0 - Moderate importance, preferences vary
        /// </summary>
        public double DefaultEducationWeight { get; set; } = 1.0;

        /// <summary>
        /// Default weight for lifestyle compatibility (smoking, drinking, children) (0-10 scale)
        /// Higher = lifestyle alignment matters more
        /// Recommendation: 2.5 - Lifestyle compatibility is critical for long-term success
        /// </summary>
        public double DefaultLifestyleWeight { get; set; } = 2.5;

        /// <summary>
        /// Weight for activity/recency score in overall compatibility (0-10 scale)
        /// Applied as a fixed weight alongside user-customizable weights.
        /// Recommendation: 0.5 - Small influence, rewards active users
        /// </summary>
        public double ActivityScoreWeight { get; set; } = 0.5;

        /// <summary>
        /// Minimum compatibility score required for match suggestions (0-100)
        /// Recommendation: 60 - Only suggest reasonably compatible matches
        /// </summary>
        public double MinimumCompatibilityThreshold { get; set; } = 60.0;

        /// <summary>
        /// Maximum distance in kilometers for potential matches
        /// Recommendation: 50 - Keep matches within reasonable travel distance by default
        /// </summary>
        public double DefaultMaxDistance { get; set; } = 50.0;

        /// <summary>
        /// Cache validity duration in hours
        /// Recommendation: 24 - Scores stay valid for a day
        /// </summary>
        public int ScoreCacheHours { get; set; } = 24;

        /// <summary>
        /// Penalty for children preference mismatch (points deducted from lifestyle score)
        /// Applied when WantsChildren differs between users.
        /// Recommendation: 30 - Strong dealbreaker for many people
        /// </summary>
        public double ChildrenMismatchPenalty { get; set; } = 30.0;

        /// <summary>
        /// Penalty when one user has children and the other doesn't (points deducted)
        /// Applied on top of ChildrenMismatchPenalty if WantsChildren also differs.
        /// Recommendation: 15 - Moderate additional factor
        /// </summary>
        public double HasChildrenMismatchPenalty { get; set; } = 15.0;

        /// <summary>
        /// Penalty for smoking habit mismatch (points deducted)
        /// Recommendation: 20 - Significant lifestyle factor
        /// </summary>
        public double SmokingMismatchPenalty { get; set; } = 20.0;

        /// <summary>
        /// Penalty for drinking habit mismatch (points deducted)
        /// Recommendation: 15 - Moderate lifestyle factor
        /// </summary>
        public double DrinkingMismatchPenalty { get; set; } = 15.0;

        /// <summary>
        /// Penalty for religion mismatch (points deducted)
        /// Recommendation: 10 - Lower penalty, many people are flexible
        /// </summary>
        public double ReligionMismatchPenalty { get; set; } = 10.0;

        /// <summary>
        /// Half-life in days for activity score exponential decay.
        /// After this many days of inactivity, the activity score drops to 50%.
        /// Recommendation: 7 - Weekly active users get good scores
        /// </summary>
        public double ActivityScoreHalfLifeDays { get; set; } = 7.0;

        /// <summary>
        /// T531 (spec 005): Weight of pairwise compatibility (from question answers)
        /// blended into the final score. Range 0.0–1.0. Default 0.30 means 30% of the
        /// final score comes from compatibility, 70% from the legacy weighted factors.
        /// Set to 0 to disable compatibility blending entirely.
        /// </summary>
        public double CompatibilityWeight { get; set; } = 0.30;
    }
}
