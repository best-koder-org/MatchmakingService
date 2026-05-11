using System.Security.Claims;
using System.Text.Json;
using MatchmakingService.Data;
using MatchmakingService.DTOs;
using MatchmakingService.Models;
using MatchmakingService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatchmakingService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CompatibilityController : ControllerBase
    {
        private readonly MatchmakingDbContext _db;
        private readonly ILogger<CompatibilityController> _logger;
        private readonly ICompatibilityScorer _scorer;

        public CompatibilityController(MatchmakingDbContext db, ICompatibilityScorer scorer, ILogger<CompatibilityController> logger)
        {
            _db = db;
            _scorer = scorer;
            _logger = logger;
        }

        /// <summary>Get all active compatibility questions (includes voice-eligible flags).</summary>
        [HttpGet("questions")]
        [Authorize]
        public async Task<ActionResult<List<CompatibilityQuestionDto>>> GetQuestions()
        {
            var questions = await _db.CompatibilityQuestions
                .Where(q => q.IsActive)
                .OrderBy(q => q.SortOrder)
                .ToListAsync();

            var dtos = questions.Select(q =>
            {
                var options = JsonSerializer.Deserialize<List<QuestionOptionDto>>(
                    q.OptionsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new List<QuestionOptionDto>();

                return new CompatibilityQuestionDto(
                    q.Id, q.Category.ToString(), q.Emoji,
                    q.TextEn, q.TextSv, options, q.SortOrder,
                    q.VoiceEligible, q.VoicePromptText, q.VoicePromptTextSv
                );
            }).ToList();

            return Ok(dtos);
        }

        /// <summary>Submit or update tap answers (upsert).</summary>
        [HttpPost("answers")]
        [Authorize]
        public async Task<ActionResult<SubmitAnswersResponse>> SubmitAnswers([FromBody] SubmitAnswersRequest request)
        {
            var keycloakId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(keycloakId))
                return Unauthorized();

            if (request.Answers == null || request.Answers.Count == 0)
                return BadRequest("No answers provided.");

            var questionIds = request.Answers.Select(a => a.QuestionId).ToList();
            var validIds = await _db.CompatibilityQuestions
                .Where(q => questionIds.Contains(q.Id) && q.IsActive)
                .Select(q => q.Id)
                .ToListAsync();

            var existing = await _db.UserQuestionAnswers
                .Where(a => a.KeycloakId == keycloakId && questionIds.Contains(a.QuestionId))
                .ToDictionaryAsync(a => a.QuestionId);

            int saved = 0;
            foreach (var answer in request.Answers)
            {
                if (!validIds.Contains(answer.QuestionId)) continue;

                if (existing.TryGetValue(answer.QuestionId, out var existingAnswer))
                {
                    existingAnswer.Value = answer.Value;
                    existingAnswer.AnswerType = "tap";
                    existingAnswer.AnsweredAt = DateTime.UtcNow;
                }
                else
                {
                    _db.UserQuestionAnswers.Add(new UserQuestionAnswer
                    {
                        KeycloakId = keycloakId,
                        QuestionId = answer.QuestionId,
                        Value = answer.Value,
                        AnswerType = "tap",
                        AnsweredAt = DateTime.UtcNow
                    });
                }
                saved++;
            }

            await _db.SaveChangesAsync();
            await InvalidateScoresForUserAsync(keycloakId);
            _logger.LogInformation("User {KeycloakId} saved {Count} tap answers", keycloakId, saved);
            return Ok(new SubmitAnswersResponse(saved, $"Saved {saved} answers"));
        }

        /// <summary>Submit a voice answer with transcript — scores quality and returns feedback.</summary>
        [HttpPost("voice-answer")]
        [Authorize]
        public async Task<ActionResult<VoiceAnswerResponse>> SubmitVoiceAnswer([FromBody] SubmitVoiceAnswerRequest request)
        {
            var keycloakId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(keycloakId))
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Transcript))
                return BadRequest("Transcript is required.");

            var question = await _db.CompatibilityQuestions
                .FirstOrDefaultAsync(q => q.Id == request.QuestionId && q.IsActive);
            if (question == null)
                return NotFound("Question not found.");

            // Score the transcript
            var quality = AnswerQualityService.ScoreTranscript(request.Transcript, request.DurationSeconds);
            var breakdownJson = JsonSerializer.Serialize(quality.Breakdown);

            // Upsert answer
            var existing = await _db.UserQuestionAnswers
                .FirstOrDefaultAsync(a => a.KeycloakId == keycloakId && a.QuestionId == request.QuestionId);

            if (existing != null)
            {
                existing.AnswerType = "voice";
                existing.VoiceTranscript = request.Transcript;
                existing.DepthScore = quality.Score;
                existing.QualityBreakdown = breakdownJson;
                existing.VoiceDurationSeconds = request.DurationSeconds;
                existing.AnsweredAt = DateTime.UtcNow;
            }
            else
            {
                _db.UserQuestionAnswers.Add(new UserQuestionAnswer
                {
                    KeycloakId = keycloakId,
                    QuestionId = request.QuestionId,
                    Value = 0, // Voice answers don't use the fixed-option value
                    AnswerType = "voice",
                    VoiceTranscript = request.Transcript,
                    DepthScore = quality.Score,
                    QualityBreakdown = breakdownJson,
                    VoiceDurationSeconds = request.DurationSeconds,
                    AnsweredAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            await InvalidateScoresForUserAsync(keycloakId);
            _logger.LogInformation("User {KeycloakId} submitted voice answer for Q{QuestionId}, score={Score}",
                keycloakId, request.QuestionId, quality.Score);

            return Ok(new VoiceAnswerResponse(
                request.QuestionId,
                quality.Score,
                quality.Stars,
                quality.Feedback,
                new QualityBreakdownDto(
                    quality.Breakdown.WordCountScore,
                    quality.Breakdown.VocabularyScore,
                    quality.Breakdown.ExpressionScore,
                    quality.Breakdown.SpecificityScore
                )
            ));
        }

        /// <summary>Get current user's profile depth score.</summary>
        [HttpGet("profile-depth")]
        [Authorize]
        public async Task<ActionResult<ProfileDepthResponse>> GetProfileDepth()
        {
            var keycloakId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(keycloakId))
                return Unauthorized();

            var answers = await _db.UserQuestionAnswers
                .Where(a => a.KeycloakId == keycloakId)
                .ToListAsync();

            var totalQuestions = await _db.CompatibilityQuestions.CountAsync(q => q.IsActive);
            var tapCount = answers.Count(a => a.AnswerType == "tap");
            var voiceCount = answers.Count(a => a.AnswerType == "voice");

            // Calculate overall score:
            // - Each tap answer = 3 points (answered, but shallow)
            // - Each voice answer = its depth score (0-100)
            // - Normalize to 0-100 based on total questions
            var tapPoints = tapCount * 3.0;
            var voicePoints = answers.Where(a => a.AnswerType == "voice").Sum(a => a.DepthScore ?? 0);
            var maxPossible = totalQuestions * 100.0;
            var overall = maxPossible > 0 ? (int)Math.Min(100, (tapPoints + voicePoints) / maxPossible * 100) : 0;

            var stars = overall switch
            {
                < 10 => 1,
                < 25 => 2,
                < 50 => 3,
                < 75 => 4,
                _ => 5
            };

            var feedback = stars switch
            {
                1 => "Answer more questions to boost your profile!",
                2 => "Good start! Voice answers give the biggest boost",
                3 => "Nice profile depth! You're ahead of most users",
                4 => "Great depth! Your matches will be more accurate",
                _ => "Top-tier profile! Best possible match quality 🌟"
            };

            return Ok(new ProfileDepthResponse(overall, stars, tapCount, voiceCount, totalQuestions, feedback));
        }

        /// <summary>Get current user's answers.</summary>
        [HttpGet("answers")]
        [Authorize]
        public async Task<ActionResult<List<UserAnswerDto>>> GetMyAnswers()
        {
            var keycloakId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(keycloakId))
                return Unauthorized();

            var answers = await _db.UserQuestionAnswers
                .Where(a => a.KeycloakId == keycloakId)
                .Select(a => new UserAnswerDto(a.QuestionId, a.Value, a.AnsweredAt, a.AnswerType, a.DepthScore))
                .ToListAsync();

            return Ok(answers);
        }

        /// <summary>
        /// T523: Get cached pairwise compatibility score, or compute + cache on demand.
        /// </summary>
        [HttpGet("score/{otherKeycloakId}")]
        [Authorize]
        public async Task<ActionResult<CompatibilityScoreDto>> GetScore(string otherKeycloakId)
        {
            var keycloakId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? User.FindFirstValue("sub");
            if (string.IsNullOrEmpty(keycloakId))
                return Unauthorized();
            if (string.IsNullOrWhiteSpace(otherKeycloakId))
                return BadRequest("otherKeycloakId is required.");
            if (string.Equals(keycloakId, otherKeycloakId, StringComparison.Ordinal))
                return BadRequest("Cannot compute compatibility with self.");

            var (id1, id2) = OrderPair(keycloakId, otherKeycloakId);

            var cached = await _db.CompatibilityScores
                .FirstOrDefaultAsync(s => s.KeycloakId1 == id1 && s.KeycloakId2 == id2);

            CompatibilityResult result;
            if (cached != null)
            {
                result = new CompatibilityResult(
                    cached.OverallScore,
                    cached.PersonalityScore,
                    cached.ValuesScore,
                    cached.AttachmentScore,
                    cached.LifestyleScore,
                    cached.SharedAnswerCount,
                    DeserializeStringList(cached.TopReasonsJson),
                    DeserializeStringList(cached.FrictionPointsJson)
                );
            }
            else
            {
                result = await _scorer.ScoreAsync(id1, id2);
                _db.CompatibilityScores.Add(new CompatibilityScore
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
                await _db.SaveChangesAsync();
            }

            return Ok(new CompatibilityScoreDto(
                otherKeycloakId,
                result.OverallScore,
                result.PersonalityScore,
                result.ValuesScore,
                result.AttachmentScore,
                result.LifestyleScore,
                result.SharedAnswerCount,
                result.TopReasons.ToList(),
                result.FrictionPoints.ToList(),
                cached?.CalculatedAt ?? DateTime.UtcNow
            ));
        }

        private static (string, string) OrderPair(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

        private static List<string> DeserializeStringList(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        /// <summary>Drop all cached pair scores involving this user (called when answers change).</summary>
        private async Task InvalidateScoresForUserAsync(string keycloakId)
        {
            var stale = await _db.CompatibilityScores
                .Where(s => s.KeycloakId1 == keycloakId || s.KeycloakId2 == keycloakId)
                .ToListAsync();
            if (stale.Count == 0) return;
            _db.CompatibilityScores.RemoveRange(stale);
            await _db.SaveChangesAsync();
            _logger.LogDebug("Invalidated {Count} cached compatibility scores for {User}", stale.Count, keycloakId);
        }

        /// <summary>Legacy: pseudo-score between two users (placeholder until real scoring).</summary>
        [HttpGet("{userId1}/{userId2}")]
        [Authorize]
        public IActionResult GetCompatibility(string userId1, string userId2)
        {
            var hash = Math.Abs((userId1 + userId2).GetHashCode());
            var interests = (hash % 40) + 30;
            var location = (hash % 30) + 40;
            var preference = (hash % 50) + 25;
            var overall = (int)((interests * 0.4) + (location * 0.3) + (preference * 0.3));

            return Ok(new
            {
                UserId1 = userId1,
                UserId2 = userId2,
                OverallScore = Math.Clamp(overall, 0, 100),
                InterestsScore = interests,
                LocationScore = location,
                PreferenceScore = preference,
            });
        }
    }
}
