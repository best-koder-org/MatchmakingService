using System.Diagnostics;
using System.Text.Json;
using MatchmakingService.Data;
using MatchmakingService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatchmakingService.Services.Background;

/// <summary>
/// T524 (spec 005 Phase 3): Background pre-computation of pairwise compatibility
/// scores. Walks users who have answered at least one compatibility question and
/// pre-fills the <c>CompatibilityScores</c> cache table for the candidate pairs
/// that the CompatibilityController would otherwise compute on first read.
///
/// Cold-start is handled gracefully: when the table is empty, the first cycle
/// just begins filling it. The scorer itself returns <see cref="CompatibilityResult.Neutral"/>
/// for users without shared answers, so pre-computation never explodes.
/// </summary>
public class CompatibilityPrecomputeService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CompatibilityPrecomputeService> _logger;
    private readonly IOptionsMonitor<CompatibilityPrecomputeOptions> _config;

    public CompatibilityPrecomputeService(
        IServiceProvider serviceProvider,
        ILogger<CompatibilityPrecomputeService> logger,
        IOptionsMonitor<CompatibilityPrecomputeOptions> config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CompatibilityPrecomputeService starting");

        // Let the app finish startup before kicking off heavy DB work.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = _config.CurrentValue;
            if (!cfg.Enabled)
            {
                _logger.LogDebug("Precompute disabled, sleeping {Interval}m", cfg.IntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(cfg.IntervalMinutes), stoppingToken);
                continue;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var (pairs, computed) = await RunCycleAsync(cfg, stoppingToken);
                sw.Stop();

                _logger.LogInformation(
                    "Compatibility precompute cycle: {Computed}/{Pairs} pairs in {Elapsed:F1}s",
                    computed, pairs, sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("CompatibilityPrecomputeService stopping gracefully");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compatibility precompute cycle failed; will retry next interval");
            }

            await Task.Delay(TimeSpan.FromMinutes(cfg.IntervalMinutes), stoppingToken);
        }

        _logger.LogInformation("CompatibilityPrecomputeService stopped");
    }

    /// <summary>Public for unit tests: run exactly one precompute cycle.</summary>
    public async Task<(int Pairs, int Computed)> RunCycleAsync(
        CompatibilityPrecomputeOptions cfg, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatchmakingDbContext>();
        var scorer = scope.ServiceProvider.GetRequiredService<ICompatibilityScorer>();

        // Users who have answered at least one question — these are the only
        // pairs that can produce a non-Neutral score.
        var answeredUsers = await db.UserQuestionAnswers
            .Select(a => a.KeycloakId)
            .Distinct()
            .OrderBy(k => k)
            .Take(cfg.MaxUsersPerCycle)
            .ToListAsync(ct);

        if (answeredUsers.Count < 2)
        {
            return (0, 0);
        }

        var staleCutoff = DateTime.UtcNow - TimeSpan.FromHours(cfg.StaleAfterHours);
        int totalPairs = 0;
        int computed = 0;

        for (int i = 0; i < answeredUsers.Count && !ct.IsCancellationRequested; i++)
        {
            for (int j = i + 1; j < answeredUsers.Count && !ct.IsCancellationRequested; j++)
            {
                if (computed >= cfg.MaxPairsPerCycle) break;

                var (id1, id2) = OrderPair(answeredUsers[i], answeredUsers[j]);
                totalPairs++;

                var existing = await db.CompatibilityScores
                    .FirstOrDefaultAsync(s => s.KeycloakId1 == id1 && s.KeycloakId2 == id2, ct);

                if (existing != null && existing.CalculatedAt >= staleCutoff)
                    continue; // fresh enough

                CompatibilityResult result;
                try
                {
                    result = await scorer.ScoreAsync(id1, id2, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scorer failed for pair {Id1}↔{Id2}", id1, id2);
                    continue;
                }

                if (existing == null)
                {
                    db.CompatibilityScores.Add(new CompatibilityScore
                    {
                        KeycloakId1 = id1,
                        KeycloakId2 = id2,
                        OverallScore = result.OverallScore,
                        PersonalityScore = result.PersonalityScore,
                        ValuesScore = result.ValuesScore,
                        AttachmentScore = result.AttachmentScore,
                        LifestyleScore = result.LifestyleScore,
                        SharedAnswerCount = result.SharedAnswerCount,
                        TopReasonsJson = JsonSerializer.Serialize(result.TopReasons),
                        FrictionPointsJson = JsonSerializer.Serialize(result.FrictionPoints),
                        CalculatedAt = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.OverallScore = result.OverallScore;
                    existing.PersonalityScore = result.PersonalityScore;
                    existing.ValuesScore = result.ValuesScore;
                    existing.AttachmentScore = result.AttachmentScore;
                    existing.LifestyleScore = result.LifestyleScore;
                    existing.SharedAnswerCount = result.SharedAnswerCount;
                    existing.TopReasonsJson = JsonSerializer.Serialize(result.TopReasons);
                    existing.FrictionPointsJson = JsonSerializer.Serialize(result.FrictionPoints);
                    existing.CalculatedAt = DateTime.UtcNow;
                }

                computed++;
            }

            if (computed >= cfg.MaxPairsPerCycle) break;
        }

        if (computed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return (totalPairs, computed);
    }

    private static (string, string) OrderPair(string a, string b)
        => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
}

/// <summary>Options for <see cref="CompatibilityPrecomputeService"/>.</summary>
public class CompatibilityPrecomputeOptions
{
    public const string SectionName = "CompatibilityPrecompute";

    /// <summary>Master kill switch. Disabled by default in tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to run a precompute cycle.</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>Maximum users to enumerate per cycle (bounds N^2 work).</summary>
    public int MaxUsersPerCycle { get; set; } = 200;

    /// <summary>Maximum number of pairs to actually compute per cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 500;

    /// <summary>Cached scores older than this are recomputed.</summary>
    public int StaleAfterHours { get; set; } = 24;
}
