namespace MatchmakingService.Models
{
    public enum QuestionCategory
    {
        Personality,
        Values,
        Attachment,
        Lifestyle
    }

    public class CompatibilityQuestion
    {
        public int Id { get; set; }
        public QuestionCategory Category { get; set; }
        public string Emoji { get; set; } = string.Empty;
        public string TextEn { get; set; } = string.Empty;
        public string TextSv { get; set; } = string.Empty;

        /// <summary>JSON array of option objects: [{"label":"...","labelSv":"...","value":1}, ...]</summary>
        public string OptionsJson { get; set; } = "[]";

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public double Weight { get; set; } = 1.0;

        // ── Voice hybrid fields ──
        public bool VoiceEligible { get; set; }
        /// <summary>Open-ended voice prompt version of this question (EN).</summary>
        public string? VoicePromptText { get; set; }
        /// <summary>Open-ended voice prompt version of this question (SV).</summary>
        public string? VoicePromptTextSv { get; set; }

        public ICollection<UserQuestionAnswer> Answers { get; set; } = new List<UserQuestionAnswer>();
    }
}
