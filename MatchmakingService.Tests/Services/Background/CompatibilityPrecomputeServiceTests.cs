using MatchmakingService.Data;
using MatchmakingService.Models;
using MatchmakingService.Services;
using MatchmakingService.Services.Background;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace MatchmakingService.Tests.Services.Background;

/// <summary>
/// T524 (spec 005 Phase 3): Tests for the background pre-computation service.
/// Verifies cold-start behaviour, cache reuse, staleness recompute, and exception
/// safety (a single failing pair must not break the cycle).
/// </summary>
public class CompatibilityPrecomputeServiceTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<ICompatibilityScorer> _mockScorer = new();
    private readonly CompatibilityPrecomputeService _service;
    private readonly CompatibilityPrecomputeOptions _options = new()
    {
        Enabled = true,
        IntervalMinutes = 60,
        MaxUsersPerCycle = 50,
        MaxPairsPerCycle = 100,
        StaleAfterHours = 24,
    };

    public CompatibilityPrecomputeServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MatchmakingDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddSingleton(_mockScorer.Object);
        _serviceProvider = services.BuildServiceProvider();

        var optionsMonitor = new Mock<IOptionsMonitor<CompatibilityPrecomputeOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(_options);

        _service = new CompatibilityPrecomputeService(
            _serviceProvider,
            new Mock<ILogger<CompatibilityPrecomputeService>>().Object,
            optionsMonitor.Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    private MatchmakingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MatchmakingDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static UserQuestionAnswer Answer(string keycloakId, int questionId)
        => new() { KeycloakId = keycloakId, QuestionId = questionId, Value = 3 };

    private static CompatibilityResult HighScore() =>
        new(85.0, 80, 90, 85, 80, 5, new[] { "Both adventurous" }, Array.Empty<string>());

    [Fact]
    public async Task RunCycle_NoAnsweredUsers_ReturnsZero()
    {
        var (pairs, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);
        Assert.Equal(0, pairs);
        Assert.Equal(0, computed);
        _mockScorer.Verify(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycle_SingleUserWithAnswers_NoPairsComputed()
    {
        using (var db = NewContext())
        {
            db.UserQuestionAnswers.Add(Answer("kc-a", 1));
            await db.SaveChangesAsync();
        }

        var (pairs, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);

        Assert.Equal(0, pairs);
        Assert.Equal(0, computed);
    }

    [Fact]
    public async Task RunCycle_TwoAnsweredUsers_ComputesAndPersistsScore()
    {
        _mockScorer
            .Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighScore());

        using (var db = NewContext())
        {
            db.UserQuestionAnswers.AddRange(Answer("kc-a", 1), Answer("kc-b", 1));
            await db.SaveChangesAsync();
        }

        var (pairs, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);

        Assert.Equal(1, pairs);
        Assert.Equal(1, computed);

        using var verifyDb = NewContext();
        var cached = await verifyDb.CompatibilityScores.SingleAsync();
        Assert.Equal("kc-a", cached.KeycloakId1);
        Assert.Equal("kc-b", cached.KeycloakId2);
        Assert.Equal(85.0, cached.OverallScore);
        Assert.Equal(5, cached.SharedAnswerCount);
        Assert.Contains("Both adventurous", cached.TopReasonsJson);
    }

    [Fact]
    public async Task RunCycle_OrdersKeycloakIdsAlphabetically()
    {
        // Insertion order shouldn't dictate pair key order — must always be alphabetical.
        _mockScorer
            .Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighScore());

        using (var db = NewContext())
        {
            db.UserQuestionAnswers.AddRange(Answer("zzz-user", 1), Answer("aaa-user", 1));
            await db.SaveChangesAsync();
        }

        await _service.RunCycleAsync(_options, CancellationToken.None);

        using var verifyDb = NewContext();
        var cached = await verifyDb.CompatibilityScores.SingleAsync();
        Assert.Equal("aaa-user", cached.KeycloakId1);
        Assert.Equal("zzz-user", cached.KeycloakId2);
    }

    [Fact]
    public async Task RunCycle_FreshCachedScore_SkipsRecompute()
    {
        using (var db = NewContext())
        {
            db.UserQuestionAnswers.AddRange(Answer("kc-a", 1), Answer("kc-b", 1));
            db.CompatibilityScores.Add(new CompatibilityScore
            {
                KeycloakId1 = "kc-a",
                KeycloakId2 = "kc-b",
                OverallScore = 50,
                CalculatedAt = DateTime.UtcNow.AddMinutes(-30), // recent
            });
            await db.SaveChangesAsync();
        }

        var (pairs, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);

        Assert.Equal(1, pairs);
        Assert.Equal(0, computed); // skipped — still fresh
        _mockScorer.Verify(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunCycle_StaleCachedScore_RecomputesAndUpdates()
    {
        _mockScorer
            .Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighScore());

        using (var db = NewContext())
        {
            db.UserQuestionAnswers.AddRange(Answer("kc-a", 1), Answer("kc-b", 1));
            db.CompatibilityScores.Add(new CompatibilityScore
            {
                KeycloakId1 = "kc-a",
                KeycloakId2 = "kc-b",
                OverallScore = 10,
                CalculatedAt = DateTime.UtcNow.AddDays(-3), // stale
            });
            await db.SaveChangesAsync();
        }

        var (_, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);

        Assert.Equal(1, computed);

        using var verifyDb = NewContext();
        var cached = await verifyDb.CompatibilityScores.SingleAsync();
        Assert.Equal(85.0, cached.OverallScore); // updated
        Assert.True(cached.CalculatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RunCycle_ScorerThrows_ContinuesWithNextPair()
    {
        // One bad pair must not bring down the whole cycle.
        _mockScorer
            .Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _mockScorer
            .Setup(s => s.ScoreAsync("kc-a", "kc-c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighScore());
        _mockScorer
            .Setup(s => s.ScoreAsync("kc-b", "kc-c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighScore());

        using (var db = NewContext())
        {
            db.UserQuestionAnswers.AddRange(
                Answer("kc-a", 1), Answer("kc-b", 1), Answer("kc-c", 1));
            await db.SaveChangesAsync();
        }

        var (pairs, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);

        Assert.Equal(3, pairs);
        Assert.Equal(2, computed); // a↔b failed, others succeeded

        using var verifyDb = NewContext();
        Assert.Equal(2, await verifyDb.CompatibilityScores.CountAsync());
    }

    [Fact]
    public async Task RunCycle_RespectsMaxPairsPerCycle()
    {
        _mockScorer
            .Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HighScore());

        using (var db = NewContext())
        {
            for (int i = 0; i < 5; i++)
                db.UserQuestionAnswers.Add(Answer($"kc-{i:00}", 1));
            await db.SaveChangesAsync();
        }
        // 5 users = C(5,2) = 10 possible pairs, cap at 3.
        _options.MaxPairsPerCycle = 3;

        var (_, computed) = await _service.RunCycleAsync(_options, CancellationToken.None);

        Assert.Equal(3, computed);

        using var verifyDb = NewContext();
        Assert.Equal(3, await verifyDb.CompatibilityScores.CountAsync());
    }
}
