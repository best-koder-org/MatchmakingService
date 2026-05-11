using Microsoft.EntityFrameworkCore;
using MatchmakingService.Models;

namespace MatchmakingService.Data
{
    public class MatchmakingDbContext : DbContext
    {
        public MatchmakingDbContext(DbContextOptions<MatchmakingDbContext> options) : base(options) { }

        public DbSet<UserInteraction> UserInteractions { get; set; }
        public DbSet<Match> Matches { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<UserProfile> UserProfiles { get; set; }
        public DbSet<MatchScore> MatchScores { get; set; }
        public DbSet<MatchPreference> MatchPreferences { get; set; }
        public DbSet<MatchingAlgorithmMetric> MatchingAlgorithmMetrics { get; set; }
        public DbSet<DailyPick> DailyPicks { get; set; }
        public DbSet<CompatibilityQuestion> CompatibilityQuestions { get; set; }
        public DbSet<UserQuestionAnswer> UserQuestionAnswers { get; set; }
        public DbSet<CompatibilityScore> CompatibilityScores { get; set; }
        public DbSet<MatchInsight> MatchInsights { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Apply performance optimizations
            DbContextOptimizations.ApplyOptimizations(modelBuilder);

            // ─── DailyPick entity configuration (single, de-duplicated) ───
            modelBuilder.Entity<DailyPick>(entity =>
            {
                entity.ToTable("daily_picks");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.UserId).IsRequired();
                entity.Property(e => e.CandidateUserId).IsRequired();
                entity.Property(e => e.Score);
                entity.Property(e => e.Rank);
                entity.Property(e => e.GeneratedAt).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
                entity.Property(e => e.ExpiresAt).IsRequired();
                entity.HasIndex(e => new { e.UserId, e.ExpiresAt }).HasDatabaseName("IX_DailyPick_UserExpiry");
                entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("IX_DailyPick_ExpiresAt");
                entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).HasPrincipalKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(e => e.Candidate).WithMany().HasForeignKey(e => e.CandidateUserId).HasPrincipalKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
            });

            // ─── Match entity configuration ───
            modelBuilder.Entity<Match>()
                .HasIndex(m => m.User1Id)
                .HasDatabaseName("IX_Match_User1Id");

            modelBuilder.Entity<Match>()
                .HasIndex(m => m.User2Id)
                .HasDatabaseName("IX_Match_User2Id");

            modelBuilder.Entity<Match>()
                .HasIndex(m => new { m.User1Id, m.User2Id })
                .IsUnique()
                .HasDatabaseName("IX_Match_User1Id_User2Id");

            modelBuilder.Entity<Match>()
                .HasIndex(m => m.CreatedAt)
                .HasDatabaseName("IX_Match_CreatedAt");

            modelBuilder.Entity<Match>()
                .ToTable(t => t.HasCheckConstraint("CK_Match_UserOrder", "User1Id < User2Id"));

            // ─── UserProfile entity configuration ───
            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => up.UserId)
                .IsUnique()
                .HasDatabaseName("IX_UserProfile_UserId");

            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => new { up.Latitude, up.Longitude })
                .HasDatabaseName("IX_UserProfile_Location");

            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => up.Age)
                .HasDatabaseName("IX_UserProfile_Age");

            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => up.Gender)
                .HasDatabaseName("IX_UserProfile_Gender");

            // T164: New field column configurations
            modelBuilder.Entity<UserProfile>()
                .Property(up => up.LookingFor)
                .HasMaxLength(50);

            // T530 (spec 005): Keycloak ID link for compatibility scoring
            modelBuilder.Entity<UserProfile>()
                .Property(up => up.KeycloakId)
                .HasMaxLength(50);

            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => up.KeycloakId)
                .HasDatabaseName("IX_UserProfile_KeycloakId");

            // T533 (spec 005): MatchInsight indexes — unique per (match, viewer)
            modelBuilder.Entity<MatchInsight>()
                .HasIndex(mi => new { mi.MatchId, mi.ForKeycloakId })
                .IsUnique()
                .HasDatabaseName("IX_MatchInsight_MatchUser");

            modelBuilder.Entity<MatchInsight>()
                .HasIndex(mi => mi.ForKeycloakId)
                .HasDatabaseName("IX_MatchInsight_ForKeycloakId");

            modelBuilder.Entity<UserProfile>()
                .Property(up => up.DesirabilityScore)
                .HasDefaultValue(50.0);

            modelBuilder.Entity<UserProfile>()
                .Property(up => up.LastActiveAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP(6)");

            // T165: Composite indexes for filter pipeline performance
            modelBuilder.Entity<UserInteraction>()
                .HasIndex(ui => new { ui.UserId, ui.TargetUserId })
                .HasDatabaseName("IX_UserInteraction_UserLookup");

            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => new { up.IsActive, up.DesirabilityScore })
                .HasDatabaseName("IX_UserProfile_Desirability");

            // T165: Composite index for active user search — covers the hot path
            // (IsActive, Gender, Age, LastActiveAt) used by filter pipeline
            modelBuilder.Entity<UserProfile>()
                .HasIndex(up => new { up.IsActive, up.Gender, up.Age, up.LastActiveAt })
                .HasDatabaseName("IX_UserProfile_ActiveSearch");

            // ─── MatchScore entity configuration ───
            modelBuilder.Entity<MatchScore>()
                .HasIndex(ms => ms.UserId)
                .HasDatabaseName("IX_MatchScore_UserId");

            modelBuilder.Entity<MatchScore>()
                .HasIndex(ms => new { ms.UserId, ms.TargetUserId })
                .IsUnique()
                .HasDatabaseName("IX_MatchScore_UserId_TargetUserId");

            modelBuilder.Entity<MatchScore>()
                .HasIndex(ms => ms.OverallScore)
                .HasDatabaseName("IX_MatchScore_OverallScore");

            modelBuilder.Entity<MatchScore>()
                .HasIndex(ms => ms.CalculatedAt)
                .HasDatabaseName("IX_MatchScore_CalculatedAt");

            // ─── MatchPreference entity configuration ───
            modelBuilder.Entity<MatchPreference>()
                .HasIndex(mp => mp.UserId)
                .HasDatabaseName("IX_MatchPreference_UserId");

            modelBuilder.Entity<MatchPreference>()
                .HasIndex(mp => new { mp.UserId, mp.PreferenceType })
                .IsUnique()
                .HasDatabaseName("IX_MatchPreference_UserId_Type");

            // ─── MatchingAlgorithmMetric entity configuration ───
            modelBuilder.Entity<MatchingAlgorithmMetric>()
                .HasIndex(mam => mam.UserId)
                .HasDatabaseName("IX_MatchingAlgorithmMetric_UserId");

            modelBuilder.Entity<MatchingAlgorithmMetric>()
                .HasIndex(mam => mam.CalculatedAt)
                .HasDatabaseName("IX_MatchingAlgorithmMetric_CalculatedAt");

            // ─── Compatibility Questions ───
            modelBuilder.Entity<CompatibilityQuestion>(entity =>
            {
                entity.ToTable("compatibility_questions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.TextEn).IsRequired().HasMaxLength(300);
                entity.Property(e => e.TextSv).IsRequired().HasMaxLength(300);
                entity.Property(e => e.Emoji).HasMaxLength(10);
                entity.Property(e => e.OptionsJson).IsRequired();
                entity.Property(e => e.Category).HasConversion<string>().HasMaxLength(20);
                entity.Property(e => e.VoicePromptText).HasMaxLength(500);
                entity.Property(e => e.VoicePromptTextSv).HasMaxLength(500);
                entity.HasIndex(e => e.SortOrder).HasDatabaseName("IX_CompatQ_SortOrder");
            });

            modelBuilder.Entity<UserQuestionAnswer>(entity =>
            {
                entity.ToTable("user_question_answers");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.KeycloakId).IsRequired().HasMaxLength(50);
                entity.Property(e => e.AnswerType).IsRequired().HasMaxLength(10).HasDefaultValue("tap");
                entity.Property(e => e.VoiceTranscript).HasMaxLength(2000);
                entity.Property(e => e.QualityBreakdown).HasMaxLength(500);
                entity.HasIndex(e => new { e.KeycloakId, e.QuestionId })
                    .IsUnique()
                    .HasDatabaseName("IX_UserAnswer_User_Question");
                entity.HasOne(e => e.Question)
                    .WithMany(q => q.Answers)
                    .HasForeignKey(e => e.QuestionId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ─── CompatibilityScore (T521/T522) ───
            modelBuilder.Entity<CompatibilityScore>(entity =>
            {
                entity.ToTable("compatibility_scores");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.KeycloakId1).IsRequired().HasMaxLength(50);
                entity.Property(e => e.KeycloakId2).IsRequired().HasMaxLength(50);
                entity.Property(e => e.TopReasonsJson).IsRequired();
                entity.Property(e => e.FrictionPointsJson).IsRequired();
                entity.HasIndex(e => new { e.KeycloakId1, e.KeycloakId2 })
                    .IsUnique()
                    .HasDatabaseName("IX_CompatScore_Pair");
                entity.HasIndex(e => e.CalculatedAt).HasDatabaseName("IX_CompatScore_CalculatedAt");
            });
        }
    }
}
