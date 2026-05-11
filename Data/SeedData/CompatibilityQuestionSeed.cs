using MatchmakingService.Models;
using Microsoft.EntityFrameworkCore;

namespace MatchmakingService.Data.SeedData
{
    public static class CompatibilityQuestionSeed
    {
        public static async Task SeedAsync(MatchmakingDbContext db)
        {
            if (await db.CompatibilityQuestions.AnyAsync()) 
            {
                // Ensure voice fields are populated on existing data
                await EnsureVoiceFieldsAsync(db);
                return;
            }

            var questions = new List<CompatibilityQuestion>
            {
                // ── Personality (5 questions) ──
                new() {
                    Category = QuestionCategory.Personality, SortOrder = 1,
                    Emoji = "🎉", TextEn = "Friday night — what sounds best?",
                    TextSv = "Fredagkväll — vad låter bäst?",
                    OptionsJson = """[{"label":"Big party","labelSv":"Stor fest","value":1},{"label":"Dinner with friends","labelSv":"Middag med vänner","value":2},{"label":"Cozy night in","labelSv":"Mysig kväll hemma","value":3},{"label":"Solo adventure","labelSv":"Soloäventyr","value":4}]""",
                    VoiceEligible = true,
                    VoicePromptText = "Describe your perfect Friday night — what are you doing, who are you with?",
                    VoicePromptTextSv = "Beskriv din perfekta fredagkväll — vad gör du, vem är du med?"
                },
                new() {
                    Category = QuestionCategory.Personality, SortOrder = 2,
                    Emoji = "🗣️", TextEn = "In a group, you're usually the one who…",
                    TextSv = "I en grupp brukar du vara den som…",
                    OptionsJson = """[{"label":"Leads the conversation","labelSv":"Leder samtalet","value":1},{"label":"Cracks the jokes","labelSv":"Drar skämten","value":2},{"label":"Listens & asks questions","labelSv":"Lyssnar & ställer frågor","value":3},{"label":"Observes quietly","labelSv":"Observerar tyst","value":4}]"""
                },
                new() {
                    Category = QuestionCategory.Personality, SortOrder = 3,
                    Emoji = "🧠", TextEn = "When making big decisions, you go with…",
                    TextSv = "När du fattar stora beslut litar du på…",
                    OptionsJson = """[{"label":"Gut feeling","labelSv":"Magkänslan","value":1},{"label":"Careful research","labelSv":"Noggrann research","value":2},{"label":"Ask everyone I trust","labelSv":"Frågar alla jag litar på","value":3},{"label":"Flip a coin honestly","labelSv":"Singlar slant ärligt","value":4}]"""
                },
                new() {
                    Category = QuestionCategory.Personality, SortOrder = 4,
                    Emoji = "🌊", TextEn = "Plans change last minute — how do you feel?",
                    TextSv = "Planerna ändras i sista stund — hur känns det?",
                    OptionsJson = """[{"label":"Love it, spontaneity!","labelSv":"Älskar det, spontanitet!","value":1},{"label":"Slightly annoyed but fine","labelSv":"Lite irriterad men okej","value":2},{"label":"Really stressed","labelSv":"Riktigt stressad","value":3}]"""
                },
                new() {
                    Category = QuestionCategory.Personality, SortOrder = 5,
                    Emoji = "💬", TextEn = "After a long day, you recharge by…",
                    TextSv = "Efter en lång dag laddar du om genom att…",
                    OptionsJson = """[{"label":"Calling a friend","labelSv":"Ringa en vän","value":1},{"label":"Going for a walk","labelSv":"Ta en promenad","value":2},{"label":"Total alone time","labelSv":"Total ensamtid","value":3},{"label":"Something creative","labelSv":"Något kreativt","value":4}]"""
                },

                // ── Values (4 questions) ──
                new() {
                    Category = QuestionCategory.Values, SortOrder = 6,
                    Emoji = "❤️", TextEn = "What matters most in a partner?",
                    TextSv = "Vad är viktigast hos en partner?",
                    OptionsJson = """[{"label":"Humor","labelSv":"Humor","value":1},{"label":"Ambition","labelSv":"Ambition","value":2},{"label":"Kindness","labelSv":"Vänlighet","value":3},{"label":"Honesty","labelSv":"Ärlighet","value":4},{"label":"Adventure","labelSv":"Äventyr","value":5}]""",
                    VoiceEligible = true,
                    VoicePromptText = "What do you value most in a partner and why? Tell us what really matters to you.",
                    VoicePromptTextSv = "Vad värderar du mest hos en partner och varför? Berätta vad som verkligen betyder något."
                },
                new() {
                    Category = QuestionCategory.Values, SortOrder = 7,
                    Emoji = "🏠", TextEn = "Your ideal life in 5 years?",
                    TextSv = "Ditt ideala liv om 5 år?",
                    OptionsJson = """[{"label":"City life, career focus","labelSv":"Stadsliv, karriärfokus","value":1},{"label":"Settled down, maybe kids","labelSv":"Bofäst, kanske barn","value":2},{"label":"Traveling the world","labelSv":"Resa runt världen","value":3},{"label":"Countryside, simple life","labelSv":"Landsbygd, enkelt liv","value":4}]""",
                    VoiceEligible = true,
                    VoicePromptText = "Paint a picture of your ideal life in 5 years — where are you, what does a typical day look like?",
                    VoicePromptTextSv = "Måla en bild av ditt ideala liv om 5 år — var är du, hur ser en vanlig dag ut?"
                },
                new() {
                    Category = QuestionCategory.Values, SortOrder = 8,
                    Emoji = "🙏", TextEn = "How important is faith/spirituality?",
                    TextSv = "Hur viktigt är tro/andlighet?",
                    OptionsJson = """[{"label":"Central to my life","labelSv":"Centralt i mitt liv","value":1},{"label":"Somewhat important","labelSv":"Ganska viktigt","value":2},{"label":"Not really","labelSv":"Inte direkt","value":3},{"label":"Not at all","labelSv":"Inte alls","value":4}]"""
                },
                new() {
                    Category = QuestionCategory.Values, SortOrder = 9,
                    Emoji = "👶", TextEn = "Kids?",
                    TextSv = "Barn?",
                    OptionsJson = """[{"label":"Want them someday","labelSv":"Vill ha nån gång","value":1},{"label":"Already have some","labelSv":"Har redan","value":2},{"label":"Don't want kids","labelSv":"Vill inte ha barn","value":3},{"label":"Open to it","labelSv":"Öppen för det","value":4}]"""
                },

                // ── Attachment (3 questions) ──
                new() {
                    Category = QuestionCategory.Attachment, SortOrder = 10,
                    Emoji = "📱", TextEn = "Your partner hasn't texted in a while…",
                    TextSv = "Din partner har inte hört av sig på ett tag…",
                    OptionsJson = """[{"label":"Not worried, they're busy","labelSv":"Inte orolig, de är upptagna","value":1},{"label":"A bit anxious, double-check","labelSv":"Lite orolig, dubbelkollar","value":2},{"label":"I'd rather have space too","labelSv":"Jag vill också ha utrymme","value":3}]"""
                },
                new() {
                    Category = QuestionCategory.Attachment, SortOrder = 11,
                    Emoji = "🤝", TextEn = "In relationships, closeness feels…",
                    TextSv = "I relationer känns närhet…",
                    OptionsJson = """[{"label":"Natural & comforting","labelSv":"Naturligt & tryggt","value":1},{"label":"I want it but it's scary","labelSv":"Jag vill men det är läskigt","value":2},{"label":"I need my independence","labelSv":"Jag behöver mitt oberoende","value":3}]""",
                    VoiceEligible = true,
                    VoicePromptText = "How do you feel about closeness in relationships? What does a healthy balance look like for you?",
                    VoicePromptTextSv = "Hur känner du inför närhet i relationer? Hur ser en sund balans ut för dig?"
                },
                new() {
                    Category = QuestionCategory.Attachment, SortOrder = 12,
                    Emoji = "💔", TextEn = "After an argument, you usually…",
                    TextSv = "Efter ett bråk brukar du…",
                    OptionsJson = """[{"label":"Want to talk it out now","labelSv":"Vill prata ut direkt","value":1},{"label":"Need time, then discuss","labelSv":"Behöver tid, sen diskutera","value":2},{"label":"Avoid the topic","labelSv":"Undviker ämnet","value":3}]"""
                },

                // ── Lifestyle (3 questions) ──
                new() {
                    Category = QuestionCategory.Lifestyle, SortOrder = 13,
                    Emoji = "🌅", TextEn = "Morning person or night owl?",
                    TextSv = "Morgonmänniska eller nattugla?",
                    OptionsJson = """[{"label":"Early bird 🌅","labelSv":"Tidigt uppe 🌅","value":1},{"label":"Night owl 🦉","labelSv":"Nattugla 🦉","value":2},{"label":"Depends on the day","labelSv":"Beror på dagen","value":3}]"""
                },
                new() {
                    Category = QuestionCategory.Lifestyle, SortOrder = 14,
                    Emoji = "🏃", TextEn = "How active are you?",
                    TextSv = "Hur aktiv är du?",
                    OptionsJson = """[{"label":"Gym/sports regularly","labelSv":"Gym/sport regelbundet","value":1},{"label":"Casual walks & yoga","labelSv":"Promenader & yoga","value":2},{"label":"Couch is my gym","labelSv":"Soffan är mitt gym","value":3}]"""
                },
                new() {
                    Category = QuestionCategory.Lifestyle, SortOrder = 15,
                    Emoji = "🍷", TextEn = "Drinking?",
                    TextSv = "Alkohol?",
                    OptionsJson = """[{"label":"Socially","labelSv":"Socialt","value":1},{"label":"Rarely","labelSv":"Sällan","value":2},{"label":"Never","labelSv":"Aldrig","value":3},{"label":"Yes, often","labelSv":"Ja, ofta","value":4}]""",
                    VoiceEligible = true,
                    VoicePromptText = "Tell us about a perfect weekend activity — what do you love doing in your free time?",
                    VoicePromptTextSv = "Berätta om en perfekt helgaktivitet — vad älskar du att göra på fritiden?"
                },
            };

            db.CompatibilityQuestions.AddRange(questions);
            await db.SaveChangesAsync();
        }

        /// <summary>Backfill voice fields on existing questions (idempotent).</summary>
        private static async Task EnsureVoiceFieldsAsync(MatchmakingDbContext db)
        {
            var voiceMap = new Dictionary<int, (string en, string sv)>
            {
                [1] = ("Describe your perfect Friday night — what are you doing, who are you with?",
                       "Beskriv din perfekta fredagkväll — vad gör du, vem är du med?"),
                [6] = ("What do you value most in a partner and why? Tell us what really matters to you.",
                       "Vad värderar du mest hos en partner och varför? Berätta vad som verkligen betyder något."),
                [7] = ("Paint a picture of your ideal life in 5 years — where are you, what does a typical day look like?",
                       "Måla en bild av ditt ideala liv om 5 år — var är du, hur ser en vanlig dag ut?"),
                [11] = ("How do you feel about closeness in relationships? What does a healthy balance look like for you?",
                        "Hur känner du inför närhet i relationer? Hur ser en sund balans ut för dig?"),
                [15] = ("Tell us about a perfect weekend activity — what do you love doing in your free time?",
                        "Berätta om en perfekt helgaktivitet — vad älskar du att göra på fritiden?")
            };

            var questions = await db.CompatibilityQuestions
                .Where(q => voiceMap.Keys.Contains(q.Id))
                .ToListAsync();

            var updated = false;
            foreach (var q in questions)
            {
                if (!q.VoiceEligible && voiceMap.TryGetValue(q.Id, out var prompts))
                {
                    q.VoiceEligible = true;
                    q.VoicePromptText = prompts.en;
                    q.VoicePromptTextSv = prompts.sv;
                    updated = true;
                }
            }

            if (updated)
                await db.SaveChangesAsync();
        }
    }
}
