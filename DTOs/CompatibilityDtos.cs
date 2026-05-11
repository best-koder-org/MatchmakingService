namespace MatchmakingService.DTOs
{
    public record CompatibilityQuestionDto(
        int Id,
        string Category,
        string Emoji,
        string Text,
        string TextSv,
        List<QuestionOptionDto> Options,
        int SortOrder,
        bool VoiceEligible = false,
        string? VoicePromptText = null,
        string? VoicePromptTextSv = null
    );

    public record QuestionOptionDto(string Label, string LabelSv, int Value);

    public record SubmitAnswersRequest(List<AnswerDto> Answers);
    public record AnswerDto(int QuestionId, int Value);

    public record SubmitAnswersResponse(int Saved, string Message);

    public record UserAnswerDto(int QuestionId, int Value, DateTime AnsweredAt,
        string AnswerType = "tap", int? DepthScore = null);

    // ── Voice answer DTOs ──
    public record SubmitVoiceAnswerRequest(int QuestionId, string Transcript, double DurationSeconds);

    public record VoiceAnswerResponse(
        int QuestionId,
        int DepthScore,
        int Stars,
        string Feedback,
        QualityBreakdownDto Breakdown
    );

    public record QualityBreakdownDto(int WordCountScore, int VocabularyScore, int ExpressionScore, int SpecificityScore);

    public record ProfileDepthResponse(
        int OverallScore,
        int Stars,
        int TapAnswers,
        int VoiceAnswers,
        int TotalQuestions,
        string Feedback
    );

    /// <summary>T523: pairwise compatibility score returned to clients.</summary>
    public record CompatibilityScoreDto(
        string OtherKeycloakId,
        double OverallScore,
        double PersonalityScore,
        double ValuesScore,
        double AttachmentScore,
        double LifestyleScore,
        int SharedAnswerCount,
        List<string> TopReasons,
        List<string> FrictionPoints,
        DateTime CalculatedAt
    );
}
