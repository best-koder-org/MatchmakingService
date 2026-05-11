using System.Text.RegularExpressions;

namespace MatchmakingService.Services
{
    /// <summary>Result of analyzing a voice answer for vector-data richness.</summary>
    public record QualityResult(int Score, int Stars, string Feedback, QualityBreakdown Breakdown);

    /// <summary>
    /// Breakdown of answer richness - not judging WHAT they say (personal),
    /// but HOW MUCH useful signal exists for vector embeddings and matching.
    /// </summary>
    public record QualityBreakdown(
        int WordCountScore,       // 0-25: raw amount of material
        int VocabularyScore,      // 0-25: diverse words = richer vectors
        int ExpressionScore,      // 0-25: explanations, examples, self-reference
        int SpecificityScore      // 0-25: concrete details, not just vague statements
    );

    public static class AnswerQualityService
    {
        /// <summary>
        /// Scores a voice answer transcript for vector-data richness.
        /// NOT judging content quality (that's personal) — measuring how much
        /// useful signal the answer provides for building embeddings.
        /// Returns 0-100 score + 1-5 richness level + encouraging feedback.
        /// </summary>
        public static QualityResult ScoreTranscript(string transcript, double durationSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(transcript))
                return new QualityResult(5, 1, "Try speaking a bit more!", new QualityBreakdown(5, 0, 0, 0));

            var text = transcript.Trim();
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wordCount = words.Length;

            // 1. Word count (0-25): more words = more material for embeddings
            var wordCountScore = wordCount switch
            {
                < 10 => 5,
                < 25 => 10,
                < 50 => 16,
                < 80 => 21,
                _ => 25
            };

            // 2. Vocabulary diversity (0-25): unique words / total words
            // Higher ratio = richer signal, more dimensions for vectors
            var uniqueWords = words
                .Select(w => w.ToLowerInvariant().Trim('.', ',', '!', '?', ':', ';'))
                .Where(w => w.Length > 1)
                .Distinct()
                .Count();
            var diversityRatio = wordCount > 0 ? (double)uniqueWords / wordCount : 0;
            var vocabularyScore = 0;
            if (wordCount < 5) {
                vocabularyScore = 3;
            } else {
                // Typical speech has 0.5-0.7 diversity. Higher is better for vectors.
                vocabularyScore = diversityRatio switch
                {
                    < 0.3 => 5,
                    < 0.45 => 10,
                    < 0.55 => 15,
                    < 0.65 => 20,
                    _ => 25
                };
            }

            // 3. Expression richness (0-25): explanations, feelings, self-reference
            // These create the most meaningful vector dimensions for personality matching
            var expressionScore = 0;
            var sentences = Regex.Split(text, @"[.!?]+").Where(s => s.Trim().Length > 0).Count();
            expressionScore += sentences switch { 1 => 2, 2 => 5, 3 => 10, _ => 13 };

            // Explanation signals
            var explanationPatterns = new[] {
                @"\b(because|since|the reason|for me|I think|I feel|I believe|I like|I love|I prefer|I need|I want)\b",
                @"\b(för att|eftersom|för mig|jag tycker|jag känner|jag tror|jag gillar|jag älskar|jag föredrar|jag behöver|jag vill)\b"
            };
            var explanationMatches = explanationPatterns.Sum(p =>
                Regex.Matches(text, p, RegexOptions.IgnoreCase).Count);
            expressionScore += Math.Min(12, explanationMatches * 3);

            // 4. Specificity (0-25): concrete details that differentiate this person
            // Names, numbers, examples, comparisons — the stuff that makes embeddings unique
            var specificityScore = 0;

            // Numbers or quantities
            if (Regex.IsMatch(text, @"\d+"))
                specificityScore += 5;

            // Comparisons and contrast
            var contrastPatterns = new[] {
                @"\b(but|however|although|on the other hand|rather than|instead of|more than|less than)\b",
                @"\b(men|dock|även om|å andra sidan|hellre|istället|mer än|mindre än)\b"
            };
            if (contrastPatterns.Any(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase)))
                specificityScore += 5;

            // Specific examples
            var examplePatterns = new[] {
                @"\b(like when|for example|such as|one time|last|recently|usually|always|never)\b",
                @"\b(som när|till exempel|typ|en gång|senast|nyligen|brukar|alltid|aldrig)\b"
            };
            if (examplePatterns.Any(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase)))
                specificityScore += 5;

            // Named entities or places (capitalized mid-sentence)
            var namedEntities = Regex.Matches(text, @"(?<=\s)[A-ZÅÄÖ][a-zåäö]{2,}").Count;
            specificityScore += Math.Min(5, namedEntities * 2);

            // Emotional/descriptive adjectives (rich for personality vectors)
            var adjectivePatterns = new[] {
                @"\b(happy|excited|calm|nervous|comfortable|passionate|creative|adventurous|quiet|loud|intense|relaxed|curious|ambitious|gentle)\b",
                @"\b(glad|lycklig|lugn|nervös|bekväm|passionerad|kreativ|äventyrlig|tyst|intensiv|avslappnad|nyfiken|ambitiös)\b"
            };
            var adjectiveCount = adjectivePatterns.Sum(p =>
                Regex.Matches(text, p, RegexOptions.IgnoreCase).Count);
            specificityScore += Math.Min(5, adjectiveCount * 2);

            var totalScore = Math.Min(100, wordCountScore + vocabularyScore + expressionScore + specificityScore);
            var stars = totalScore switch
            {
                < 20 => 1,
                < 40 => 2,
                < 60 => 3,
                < 80 => 4,
                _ => 5
            };

            var feedback = stars switch
            {
                1 => "Brief answer — try sharing more details next time 💬",
                2 => "Decent answer! A bit more detail helps us find your match 👍",
                3 => "Good answer — we're getting a sense of who you are ✨",
                4 => "Rich answer! Lots of signal for finding great matches 🔥",
                _ => "Very rich answer! This gives the deepest matching possible 🌟"
            };

            return new QualityResult(
                totalScore, stars, feedback,
                new QualityBreakdown(wordCountScore, vocabularyScore, expressionScore, specificityScore)
            );
        }

        /// <summary>
        /// Quick duration-based richness estimate for instant UI feedback
        /// before transcript is available. Based on ~2.5 words/sec speaking rate.
        /// </summary>
        public static (int stars, string feedback) QuickEstimateFromDuration(double seconds)
        {
            return seconds switch
            {
                < 10 => (1, "Brief — try saying more for better matches"),
                < 20 => (2, "Decent start! More detail = better matches"),
                < 35 => (3, "Good! We're learning about you 🎙️"),
                < 50 => (4, "Rich answer! Great for finding your match 🔥"),
                _ => (5, "Very rich! Deep signal for matching 🌟")
            };
        }
    }
}
