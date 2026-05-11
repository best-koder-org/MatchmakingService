using MatchmakingService.Data;
using MatchmakingService.Filters;
using MatchmakingService.Models;
using MatchmakingService.Services;
using MatchmakingService.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace MatchmakingService.Services.Background;

/// <summary>
/// T178: Background service that generates daily curated picks for all active users.
///
/// Runs once per day at the configured UTC time (default 03:00).
/// For each active user:
///   1. Run the LiveScoringStrategy to get top candidates
///   2. Insert top N as DailyPick rows with 24h expiry
///   3. Clean up expired picks from previous days
///
/// T177: Adaptive scheduling — adjusts generation timing based on user count.
///   - Under 1K users: generate all at once
///   - 1K to 10K users: batch with 100ms delays
///   - Over 10K users: batch with 500ms delays + parallel batches
///
/// T535 (spec 005): Compatibility blending is inherited automatically.
/// LiveScoringStrategy → AdvancedMatchingService.CalculateCompatibilityScoreAsync
/// already weights pairwise CompatibilityScorer results into the score
/// (default 30%, configurable via ScoringConfiguration.CompatibilityWeight).
/// Users with answered Big Five / attachment / values questions therefore
/// rank higher in daily picks; users without answers fall back to the
/// legacy weighted score without breaking generation.
/// </summary>
public class DailyPickGenerationService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyPickGenerationService> _logger;
    private readonly IOptionsMonitor<CandidateOptions> _config;

    public DailyPickGenerationService(
        IServiceProvider serviceProvider,
        ILogger<DailyPickGenerationService> logger,
        IOptionsMonitor<CandidateOptions> config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyPickGenerationService starting");
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); // Let app warm up

        while (!stoppingToken.IsCancellationRequested)
        {
            var dpConfig = _config.CurrentValue.DailyPicks;

            if (!dpConfig.Enabled)
            {
                _logger.LogDebug("Daily picks disabled, sleeping 5 min");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            // Wait until scheduled generation time
            var waitTime = CalculateWaitUntilNextRun(dpConfig.GenerationTimeUtc);
            _logger.LogInformation("Next daily pick generation in {Wait}", waitTime);
            await Task.Delay(waitTime, stoppingToken);

            try
            {
                var sw = Stopwatch.StartNew();
                var (usersProcessed, picksGenerated) = await GenerateAllPicksAsync(dpConfig, stoppingToken);
                sw.Stop();

                _logger.LogInformation(
                    "Daily pick generation complete: {Users} users, {Picks} picks in {Elapsed:F1}s",
                    usersProcessed, picksGenerated, sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily pick generation failed, will retry next cycle");
            }

            // Sleep at least 1 hour to avoid double-generation if restart happens near schedule time
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task<(int UsersProcessed, int PicksGenerated)> GenerateAllPicksAsync(
        DailyPicksOptions config, CancellationToken ct)
    {
        int totalUsers = 0, totalPicks = 0;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatchmakingDbContext>();
        var liveStrategy = scope.ServiceProvider.GetRequiredService<LiveScoringStrategy>();

        // 1. Clean up expired picks
        var expiredCount = await CleanExpiredPicksAsync(db, ct);
        _logger.LogInformation("Cleaned {Count} expired daily picks", expiredCount);

        // 2. Get all active users
        var activeUsers = await db.UserProfiles
            .Where(u => u.IsActive)
            .Select(u => u.UserId)
            .ToListAsync(ct);

        _logger.LogInformation("Generating daily picks for {Count} active users", activeUsers.Count);

        // T177: Adaptive scheduling based on user count
        var (batchSize, delayMs) = GetAdaptiveScheduling(activeUsers.Count);

        // 3. Generate picks in batches
        for (int i = 0; i < activeUsers.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = activeUsers.Skip(i).Take(batchSize);
            foreach (var userId in batch)
            {
                try
                {
                    var picksForUser = await GeneratePicksForUserAsync(
                        userId, config.PicksPerUser, config.ExpiryHours,
                        db, liveStrategy, ct);
                    totalPicks += picksForUser;
                    totalUsers++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate picks for user {UserId}", userId);
                }
            }

            // Adaptive delay between batches
            if (delayMs > 0 && i + batchSize < activeUsers.Count)
            {
                await Task.Delay(delayMs, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        return (totalUsers, totalPicks);
    }

    private async Task<int> GeneratePicksForUserAsync(
        int userId, int picksPerUser, int expiryHours,
        MatchmakingDbContext db, LiveScoringStrategy strategy,
        CancellationToken ct)
    {
        // Use LiveScoringStrategy to get top candidates
        var request = new CandidateRequest(
            Limit: picksPerUser * 2, // Over-fetch to account for filtering
            MinScore: 10 // Minimum quality threshold for daily picks
        );

        var result = await strategy.GetCandidatesAsync(userId, request, ct);

        var expiresAt = DateTime.UtcNow.AddHours(expiryHours);
        var rank = 0;

        foreach (var candidate in result.Candidates.Take(picksPerUser))
        {
            rank++;
            db.DailyPicks.Add(new DailyPick
            {
                UserId = userId,
                CandidateUserId = candidate.Profile.UserId,
                Score = candidate.FinalScore,
                Rank = rank,
                GeneratedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                Seen = false,
                Acted = false
            });
        }

        return rank;
    }

    private static async Task<int> CleanExpiredPicksAsync(MatchmakingDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.DailyPicks
            .Where(dp => dp.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// T177: Adaptive scheduling — adjusts batch size and delay based on user count.
    /// </summary>
    private static (int BatchSize, int DelayMs) GetAdaptiveScheduling(int userCount)
    {
        return userCount switch
        {
            < 1_000 => (userCount, 0),         // Small app: all at once
            < 10_000 => (100, 100),             // Medium: 100-user batches, 100ms gap
            < 100_000 => (200, 500),            // Large: 200-user batches, 500ms gap
            _ => (500, 1000)                    // Very large: 500-user batches, 1s gap
        };
    }

    private static TimeSpan CalculateWaitUntilNextRun(string timeUtc)
    {
        if (!TimeSpan.TryParse(timeUtc, out var scheduledTime))
            scheduledTime = TimeSpan.FromHours(3); // Default 03:00 UTC

        var now = DateTime.UtcNow;
        var nextRun = now.Date.Add(scheduledTime);

        if (nextRun <= now)
            nextRun = nextRun.AddDays(1);

        return nextRun - now;
    }
}
