using System.ComponentModel.DataAnnotations.Schema;

namespace MatchmakingService.Models
{
    public class UserQuestionAnswer
    {
        public int Id { get; set; }
        public string KeycloakId { get; set; } = string.Empty;
        public int QuestionId { get; set; }
        public int Value { get; set; }
        public DateTime AnsweredAt { get; set; } = DateTime.UtcNow;

        // ── Voice hybrid fields ──
        /// <summary>"tap" or "voice"</summary>
        public string AnswerType { get; set; } = "tap";
        /// <summary>Whisper transcript of voice answer (null for tap answers).</summary>
        public string? VoiceTranscript { get; set; }
        /// <summary>Quality/depth score 0-100 (null for tap answers).</summary>
        public int? DepthScore { get; set; }
        /// <summary>JSON breakdown of quality dimensions.</summary>
        public string? QualityBreakdown { get; set; }
        /// <summary>Duration of voice recording in seconds.</summary>
        public double? VoiceDurationSeconds { get; set; }

        [ForeignKey(nameof(QuestionId))]
        public CompatibilityQuestion? Question { get; set; }
    }
}
