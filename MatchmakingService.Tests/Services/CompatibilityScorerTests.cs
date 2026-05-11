using MatchmakingService.Data;
using MatchmakingService.Models;
using MatchmakingService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace MatchmakingService.Tests.Services;

/// <summary>
/// T525: Unit tests for CompatibilityScorer (spec 005 Phase 3).
/// Validates: identical answers → 100, opposite → ~0, partial → proportional,
/// missing answers, voice answers, category aggregation, top reasons / friction.
/// </summary>
public class CompatibilityScorerTests : IDisposable
{
    private readonly MatchmakingDbContext _db;
    private readonly CompatibilityScorer _scorer;

    private const string UserA = "user-a";
    private const string UserB = "user-b";

    public CompatibilityScorerTests()
    {
        var options = new DbContextOptionsBuilder<MatchmakingDbContext>()
            .UseInMemoryDatabase($"CompatScorer_{Guid.NewGuid()}")
            .Options;
        _db = new MatchmakingDbContext(options);
        _scorer = new CompatibilityScorer(_db, Mock.Of<ILogger<CompatibilityScorer>>());
    }

    public void Dispose() => _db.Dispose();

    private static CompatibilityQuestion MakeQuestion(int id, QuestionCategory category, int optionCount = 4, double weight = 1.0)
    {
        var opts = string.Join(",", Enumerable.Range(1, optionCount)
            .Select(i => $"{{\"label\":\"O{i}\",\"labelSv\":\"O{i}\",\"value\":{i}}}"));
        return new CompatibilityQuestion
        {
            Id = id,
            Category = category,
            Emoji = "🙂",
            TextEn = $"Question {id}",
            TextSv = $"Fråga {id}",
            OptionsJson = $"[{opts}]",
            SortOrder = id,
            IsActive = true,
            Weight = weight,
        };
    }

    private static UserQuestionAnswer Answer(string user, int qid, int value, string type = "tap")
        => new()
        {
            KeycloakId = user,
            QuestionId = qid,
            Value = value,
            AnswerType = type,
            AnsweredAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task IdenticalAnswers_ReturnsHighScore()
    {
        _db.CompatibilityQuestions.AddRange(
            MakeQuestion(1, QuestionCategory.Personality),
            MakeQuestion(2, QuestionCategory.Values),
            MakeQuestion(3, QuestionCategory.Attachment),
            MakeQuestion(4, QuestionCategory.Lifestyle));
        _db.UserQuestionAnswers.AddRange(
            Answer(UserA, 1, 2), Answer(UserB, 1, 2),
            Answer(UserA, 2, 3), Answer(UserB, 2, 3),
            Answer(UserA, 3, 1), Answer(UserB, 3, 1),
            Answer(UserA, 4, 4), Answer(UserB, 4, 4));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);

        Assert.Equal(100.0, result.OverallScore, 1);
        Assert.Equal(4, result.SharedAnswerCount);
        Assert.NotEmpty(result.TopReasons);
        Assert.Empty(result.FrictionPoints);
    }

    [Fact]
    public async Task OppositeAnswers_ReturnsLowScore()
    {
        _db.CompatibilityQuestions.Add(MakeQuestion(1, QuestionCategory.Personality, optionCount: 4));
        _db.UserQuestionAnswers.AddRange(Answer(UserA, 1, 1), Answer(UserB, 1, 4));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);

        // Max ordinal distance: agreement = 1 - 3/3 = 0.
        Assert.Equal(0.0, result.OverallScore, 1);
        Assert.NotEmpty(result.FrictionPoints);
    }

    [Fact]
    public async Task PartialOverlap_ReturnsProportionalScore()
    {
        // 4 options. Values 2 vs 3: diff=1, agreement = 1 - 1/3 ≈ 0.667 → 66.7
        _db.CompatibilityQuestions.Add(MakeQuestion(1, QuestionCategory.Personality, optionCount: 4));
        _db.UserQuestionAnswers.AddRange(Answer(UserA, 1, 2), Answer(UserB, 1, 3));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);

        Assert.InRange(result.OverallScore, 60.0, 75.0);
    }

    [Fact]
    public async Task NoSharedAnswers_ReturnsNeutral()
    {
        _db.CompatibilityQuestions.AddRange(
            MakeQuestion(1, QuestionCategory.Personality),
            MakeQuestion(2, QuestionCategory.Values));
        _db.UserQuestionAnswers.AddRange(Answer(UserA, 1, 2), Answer(UserB, 2, 3));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);

        Assert.Equal(50.0, result.OverallScore);
        Assert.Equal(0, result.SharedAnswerCount);
    }

    [Fact]
    public async Task NoAnswersAtAll_ReturnsNeutral()
    {
        var result = await _scorer.ScoreAsync(UserA, UserB);
        Assert.Equal(50.0, result.OverallScore);
        Assert.Equal(0, result.SharedAnswerCount);
    }

    [Fact]
    public async Task SameUser_ReturnsNeutral()
    {
        _db.CompatibilityQuestions.Add(MakeQuestion(1, QuestionCategory.Personality));
        _db.UserQuestionAnswers.Add(Answer(UserA, 1, 2));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserA);
        Assert.Equal(50.0, result.OverallScore);
    }

    [Fact]
    public async Task VoiceAnswer_GivesBaseAgreement()
    {
        // One voice answer pair → 0.7 * 100 = 70.
        _db.CompatibilityQuestions.Add(MakeQuestion(1, QuestionCategory.Personality));
        _db.UserQuestionAnswers.AddRange(
            Answer(UserA, 1, 0, type: "voice"),
            Answer(UserB, 1, 0, type: "voice"));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);
        Assert.Equal(70.0, result.OverallScore, 1);
    }

    [Fact]
    public async Task CategoryWeighting_PrefersHigherWeightedCategories()
    {
        // Personality (weight 0.30) full match; Lifestyle (weight 0.20) full mismatch.
        // Expected weighted overall = (100*0.30 + 0*0.20) / (0.30 + 0.20) = 60.
        _db.CompatibilityQuestions.AddRange(
            MakeQuestion(1, QuestionCategory.Personality, optionCount: 4),
            MakeQuestion(2, QuestionCategory.Lifestyle, optionCount: 4));
        _db.UserQuestionAnswers.AddRange(
            Answer(UserA, 1, 2), Answer(UserB, 1, 2),
            Answer(UserA, 2, 1), Answer(UserB, 2, 4));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);

        Assert.Equal(100.0, result.PersonalityScore, 1);
        Assert.Equal(0.0, result.LifestyleScore, 1);
        Assert.Equal(60.0, result.OverallScore, 1);
    }

    [Fact]
    public async Task TopReasons_PopulatedForStrongMatches()
    {
        _db.CompatibilityQuestions.AddRange(
            MakeQuestion(1, QuestionCategory.Personality),
            MakeQuestion(2, QuestionCategory.Values),
            MakeQuestion(3, QuestionCategory.Lifestyle));
        _db.UserQuestionAnswers.AddRange(
            Answer(UserA, 1, 2), Answer(UserB, 1, 2),
            Answer(UserA, 2, 3), Answer(UserB, 2, 3),
            Answer(UserA, 3, 1), Answer(UserB, 3, 1));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);

        Assert.Equal(3, result.TopReasons.Count);
        Assert.All(result.TopReasons, r => Assert.Contains("agree", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InactiveQuestion_Ignored()
    {
        var q = MakeQuestion(1, QuestionCategory.Personality);
        q.IsActive = false;
        _db.CompatibilityQuestions.Add(q);
        _db.UserQuestionAnswers.AddRange(Answer(UserA, 1, 2), Answer(UserB, 1, 2));
        await _db.SaveChangesAsync();

        var result = await _scorer.ScoreAsync(UserA, UserB);
        Assert.Equal(50.0, result.OverallScore);
        Assert.Equal(0, result.SharedAnswerCount);
    }
}
