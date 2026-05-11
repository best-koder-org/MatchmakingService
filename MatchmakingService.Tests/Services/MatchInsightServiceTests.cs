using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using MatchmakingService.Data;
using MatchmakingService.Models;
using MatchmakingService.Services;

namespace MatchmakingService.Tests.Services;

/// <summary>
/// T532+T533 (spec 005 Phase 4): MatchInsightService generates and persists
/// per-user MatchInsight rows when a Match is created.
/// </summary>
public class MatchInsightServiceTests : IDisposable
{
    private readonly MatchmakingDbContext _context;
    private readonly Mock<ICompatibilityScorer> _scorer = new();
    private readonly MatchInsightService _service;

    public MatchInsightServiceTests()
    {
        var options = new DbContextOptionsBuilder<MatchmakingDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new MatchmakingDbContext(options);
        _service = new MatchInsightService(_context, _scorer.Object, new Mock<ILogger<MatchInsightService>>().Object);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task SeedPair(string? kc1 = "kc-a", string? kc2 = "kc-b")
    {
        _context.UserProfiles.AddRange(
            new UserProfile { UserId = 1, KeycloakId = kc1, Age = 28, MinAge = 25, MaxAge = 35, MaxDistance = 50, IsActive = true },
            new UserProfile { UserId = 2, KeycloakId = kc2, Age = 30, MinAge = 25, MaxAge = 35, MaxDistance = 50, IsActive = true });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GenerateForMatch_WithSharedAnswers_PersistsBothUserRows()
    {
        await SeedPair();
        _scorer.Setup(s => s.ScoreAsync("kc-a", "kc-b", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CompatibilityResult(
                   OverallScore: 82.5,
                   PersonalityScore: 80, ValuesScore: 85, AttachmentScore: 80, LifestyleScore: 85,
                   SharedAnswerCount: 6,
                   TopReasons: new[] { "Both value family", "Both love travel" },
                   FrictionPoints: new[] { "Different views on city vs country" }));

        await _service.GenerateForMatchAsync(matchId: 42, user1Id: 1, user2Id: 2, fallbackScore: 70.0);

        var insights = await _context.MatchInsights.Where(mi => mi.MatchId == 42).ToListAsync();
        Assert.Equal(2, insights.Count);
        Assert.Contains(insights, mi => mi.ForKeycloakId == "kc-a");
        Assert.Contains(insights, mi => mi.ForKeycloakId == "kc-b");
        foreach (var mi in insights)
        {
            Assert.Equal(82.5, mi.OverallScore);
            var reasons = JsonSerializer.Deserialize<string[]>(mi.ReasonsJson)!;
            Assert.Equal(2, reasons.Length);
            Assert.Contains("Both value family", reasons);
        }
    }

    [Fact]
    public async Task GenerateForMatch_NoSharedAnswers_UsesFallbackScore()
    {
        await SeedPair();
        _scorer.Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CompatibilityResult.Neutral());

        await _service.GenerateForMatchAsync(matchId: 1, user1Id: 1, user2Id: 2, fallbackScore: 67.5);

        var insights = await _context.MatchInsights.ToListAsync();
        Assert.Equal(2, insights.Count);
        Assert.All(insights, mi => Assert.Equal(67.5, mi.OverallScore));
    }

    [Fact]
    public async Task GenerateForMatch_MissingKeycloakId_SkipsGeneration()
    {
        await SeedPair(kc1: "kc-a", kc2: null);

        await _service.GenerateForMatchAsync(matchId: 1, user1Id: 1, user2Id: 2, fallbackScore: 60.0);

        Assert.Empty(_context.MatchInsights);
        _scorer.Verify(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateForMatch_CalledTwice_IsIdempotent()
    {
        await SeedPair();
        _scorer.Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CompatibilityResult.Neutral());

        await _service.GenerateForMatchAsync(matchId: 9, user1Id: 1, user2Id: 2, fallbackScore: 50.0);
        await _service.GenerateForMatchAsync(matchId: 9, user1Id: 1, user2Id: 2, fallbackScore: 99.0);

        var insights = await _context.MatchInsights.Where(mi => mi.MatchId == 9).ToListAsync();
        Assert.Equal(2, insights.Count); // Still just one per user
        Assert.All(insights, mi => Assert.Equal(50.0, mi.OverallScore)); // First write wins
    }

    [Fact]
    public async Task GenerateForMatch_ScorerThrows_SwallowsExceptionAndWritesNoRows()
    {
        await SeedPair();
        _scorer.Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("boom"));

        await _service.GenerateForMatchAsync(matchId: 1, user1Id: 1, user2Id: 2, fallbackScore: 50.0);

        Assert.Empty(_context.MatchInsights);
    }

    [Fact]
    public async Task GenerateForMatch_AsymmetricByDesign_SameContentForBothUsers()
    {
        // Phase 4: symmetric content stored per-user. Future phases may diverge.
        await SeedPair();
        _scorer.Setup(s => s.ScoreAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CompatibilityResult(
                   75, 75, 75, 75, 75, SharedAnswerCount: 3,
                   TopReasons: new[] { "shared-reason" },
                   FrictionPoints: new[] { "shared-friction" }));

        await _service.GenerateForMatchAsync(matchId: 5, user1Id: 1, user2Id: 2, fallbackScore: null);

        var rows = await _context.MatchInsights.Where(mi => mi.MatchId == 5).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].ReasonsJson, rows[1].ReasonsJson);
        Assert.Equal(rows[0].FrictionJson, rows[1].FrictionJson);
        Assert.NotEqual(rows[0].ForKeycloakId, rows[1].ForKeycloakId);
    }
}
