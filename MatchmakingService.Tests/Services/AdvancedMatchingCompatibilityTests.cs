using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using MatchmakingService.Services;
using MatchmakingService.Data;
using MatchmakingService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatchmakingService.Tests.Services;

/// <summary>
/// T530 (spec 005 Phase 4): Tests that AdvancedMatchingService blends pairwise
/// compatibility (from question answers) into the final candidate score using
/// the configurable CompatibilityWeight, and falls back gracefully when one
/// or both users have no answers.
/// </summary>
public class AdvancedMatchingCompatibilityTests : IDisposable
{
    private readonly MatchmakingDbContext _context;
    private readonly Mock<IUserServiceClient> _mockUserServiceClient = new();
    private readonly Mock<ISafetyServiceClient> _mockSafetyServiceClient = new();
    private readonly Mock<ILogger<AdvancedMatchingService>> _mockLogger = new();
    private readonly Mock<IDailySuggestionTracker> _mockTracker = new();
    private readonly Mock<ISwipeServiceClient> _mockSwipe = new();
    private readonly Mock<IOptionsMonitor<ScoringConfiguration>> _mockConfig = new();
    private readonly ScoringConfiguration _config = new();

    public AdvancedMatchingCompatibilityTests()
    {
        var options = new DbContextOptionsBuilder<MatchmakingDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new MatchmakingDbContext(options);
        _mockConfig.Setup(x => x.CurrentValue).Returns(_config);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private AdvancedMatchingService BuildService(ICompatibilityScorer? scorer)
    {
        return new AdvancedMatchingService(
            _context,
            _mockUserServiceClient.Object,
            _mockSafetyServiceClient.Object,
            _mockSwipe.Object,
            _mockConfig.Object,
            _mockLogger.Object,
            _mockTracker.Object,
            scorer);
    }

    private static UserProfile Profile(int id, string? keycloakId)
    {
        return new UserProfile
        {
            UserId = id,
            KeycloakId = keycloakId,
            Gender = id % 2 == 1 ? "Male" : "Female",
            Age = 28,
            Latitude = 37.7749,
            Longitude = -122.4194,
            PreferredGender = id % 2 == 1 ? "Female" : "Male",
            MinAge = 25,
            MaxAge = 35,
            MaxDistance = 50,
            IsActive = true,
            LocationWeight = 1.0,
            AgeWeight = 1.0,
            InterestsWeight = 1.0,
            EducationWeight = 1.0,
            LifestyleWeight = 1.0,
            SmokingStatus = "Never",
            DrinkingStatus = "Sometimes",
        };
    }

    [Fact]
    public async Task CompatibilityBlend_NoScorer_LeavesLegacyScoreUnchanged()
    {
        await _context.UserProfiles.AddRangeAsync(Profile(1, "kc-a"), Profile(2, "kc-b"));
        await _context.SaveChangesAsync();

        var service = BuildService(scorer: null);
        var score = await service.CalculateCompatibilityScoreAsync(1, 2);

        // Same-location, age-compatible — should be a high legacy score, unaffected.
        Assert.True(score >= 80, $"Expected high legacy score, got {score}");
    }

    [Fact]
    public async Task CompatibilityBlend_NoKeycloakIds_FallsBackToLegacyScore()
    {
        // Profiles without KeycloakId should skip compatibility entirely.
        await _context.UserProfiles.AddRangeAsync(Profile(1, null), Profile(2, null));
        await _context.SaveChangesAsync();

        var scorer = new Mock<ICompatibilityScorer>(MockBehavior.Strict);
        var service = BuildService(scorer.Object);

        var score = await service.CalculateCompatibilityScoreAsync(1, 2);

        Assert.True(score > 0);
        scorer.Verify(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "Scorer should not be invoked when KeycloakIds are null");
    }

    [Fact]
    public async Task CompatibilityBlend_NeutralResult_LeavesLegacyScoreUnchanged()
    {
        // Scorer returns Neutral (SharedAnswerCount=0) when users have no answers —
        // legacy score must pass through unchanged so non-onboarded users still match.
        await _context.UserProfiles.AddRangeAsync(Profile(1, "kc-a"), Profile(2, "kc-b"));
        await _context.SaveChangesAsync();

        var scorer = new Mock<ICompatibilityScorer>();
        scorer.Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
              .ReturnsAsync(CompatibilityResult.Neutral());
        var service = BuildService(scorer.Object);

        var withScorer = await service.CalculateCompatibilityScoreAsync(1, 2);

        // Compare against fresh DB run with no scorer.
        _context.MatchScores.RemoveRange(_context.MatchScores);
        await _context.SaveChangesAsync();
        var legacyService = BuildService(scorer: null);
        var legacy = await legacyService.CalculateCompatibilityScoreAsync(1, 2);

        Assert.Equal(legacy, withScorer);
    }

    [Fact]
    public async Task CompatibilityBlend_HighCompatibility_RaisesScore()
    {
        await _context.UserProfiles.AddRangeAsync(Profile(1, "kc-a"), Profile(2, "kc-b"));
        await _context.SaveChangesAsync();

        // Establish legacy score baseline.
        var legacyService = BuildService(scorer: null);
        var legacy = await legacyService.CalculateCompatibilityScoreAsync(1, 2);
        _context.MatchScores.RemoveRange(_context.MatchScores);
        await _context.SaveChangesAsync();

        // Scorer reports a perfect compat score — final should shift toward 100.
        var scorer = new Mock<ICompatibilityScorer>();
        scorer.Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new CompatibilityResult(
                  OverallScore: 100.0,
                  PersonalityScore: 100, ValuesScore: 100, AttachmentScore: 100, LifestyleScore: 100,
                  SharedAnswerCount: 8,
                  TopReasons: new[] { "Both love hiking" },
                  FrictionPoints: Array.Empty<string>()));
        var service = BuildService(scorer.Object);

        var blended = await service.CalculateCompatibilityScoreAsync(1, 2);

        // With default 30% weight and compat=100, blended must be >= legacy.
        Assert.True(blended >= legacy,
            $"Expected blended ({blended}) >= legacy ({legacy}) when compat is perfect");
        // Expected: legacy*0.7 + 100*0.3
        var expected = (legacy * 0.7) + (100.0 * 0.3);
        Assert.True(Math.Abs(blended - expected) < 0.5,
            $"Expected ~{expected:F2}, got {blended:F2}");
    }

    [Fact]
    public async Task CompatibilityBlend_LowCompatibility_LowersScore()
    {
        await _context.UserProfiles.AddRangeAsync(Profile(1, "kc-a"), Profile(2, "kc-b"));
        await _context.SaveChangesAsync();

        var legacyService = BuildService(scorer: null);
        var legacy = await legacyService.CalculateCompatibilityScoreAsync(1, 2);
        _context.MatchScores.RemoveRange(_context.MatchScores);
        await _context.SaveChangesAsync();

        var scorer = new Mock<ICompatibilityScorer>();
        scorer.Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new CompatibilityResult(
                  OverallScore: 0.0,
                  PersonalityScore: 0, ValuesScore: 0, AttachmentScore: 0, LifestyleScore: 0,
                  SharedAnswerCount: 5,
                  TopReasons: Array.Empty<string>(),
                  FrictionPoints: new[] { "Opposite values" }));
        var service = BuildService(scorer.Object);

        var blended = await service.CalculateCompatibilityScoreAsync(1, 2);

        Assert.True(blended < legacy,
            $"Expected blended ({blended}) < legacy ({legacy}) when compat is zero");
        var expected = legacy * 0.7;
        Assert.True(Math.Abs(blended - expected) < 0.5,
            $"Expected ~{expected:F2}, got {blended:F2}");
    }

    [Fact]
    public async Task CompatibilityBlend_ZeroWeight_DisablesBlending()
    {
        // T531: setting CompatibilityWeight=0 disables blending entirely.
        _config.CompatibilityWeight = 0.0;

        await _context.UserProfiles.AddRangeAsync(Profile(1, "kc-a"), Profile(2, "kc-b"));
        await _context.SaveChangesAsync();

        var scorer = new Mock<ICompatibilityScorer>(MockBehavior.Strict);
        var service = BuildService(scorer.Object);

        var score = await service.CalculateCompatibilityScoreAsync(1, 2);

        Assert.True(score > 0);
        scorer.Verify(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "Scorer must not be called when CompatibilityWeight is 0");
    }

    [Fact]
    public async Task CompatibilityBlend_ScorerThrows_FallsBackToLegacyScore()
    {
        // Compatibility is a soft input — a scorer exception must not break matching.
        await _context.UserProfiles.AddRangeAsync(Profile(1, "kc-a"), Profile(2, "kc-b"));
        await _context.SaveChangesAsync();

        var legacyService = BuildService(scorer: null);
        var legacy = await legacyService.CalculateCompatibilityScoreAsync(1, 2);
        _context.MatchScores.RemoveRange(_context.MatchScores);
        await _context.SaveChangesAsync();

        var scorer = new Mock<ICompatibilityScorer>();
        scorer.Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));
        var service = BuildService(scorer.Object);

        var score = await service.CalculateCompatibilityScoreAsync(1, 2);

        Assert.Equal(legacy, score);
    }

    [Fact]
    public void ScoringConfiguration_DefaultCompatibilityWeight_IsThirty()
    {
        var cfg = new ScoringConfiguration();
        Assert.Equal(0.30, cfg.CompatibilityWeight);
    }
}
