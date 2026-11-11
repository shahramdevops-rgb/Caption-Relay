using System;
using System.Linq;
using CaptionRelay.Captions;
using Xunit;

namespace CaptionRelay.Tests
{
    public class TranscriptMergerTests
    {
        private static TranscriptMerger MergeAll(params string[] windows)
        {
            var m = new TranscriptMerger();
            foreach (string w in windows)
            {
                m.Merge(w);
            }

            return m;
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                n++;
            }

            return n;
        }

        // ---------- Spec cases ----------

        [Fact]
        public void NormalGrowth_TranscriptEqualsLatestWindow()
        {
            var m = MergeAll("Hello", "Hello world", "Hello world, how are", "Hello world, how are you today?");
            Assert.Equal("Hello world, how are you today?", m.Full);
        }

        [Fact]
        public void FirstWindow_IsAppended()
        {
            var m = new TranscriptMerger();
            MergeResult r = m.Merge("  First words  ");
            Assert.Equal(MergeKind.Appended, r.Kind);
            Assert.Equal("First words", m.Full);
        }

        [Fact]
        public void ScrollingOffTheTop_KeepsEverything()
        {
            var m = MergeAll(
                "So today we are going to talk about the history of the Roman Empire",
                "So today we are going to talk about the history of the Roman Empire and how it shaped modern Europe.",
                "the Roman Empire and how it shaped modern Europe. The first emperor was Augustus.",
                "modern Europe. The first emperor was Augustus. He ruled for forty years.");

            Assert.Equal(
                "So today we are going to talk about the history of the Roman Empire and how it shaped modern Europe. The first emperor was Augustus. He ruled for forty years.",
                m.Full);
        }

        [Fact]
        public void ThreeMinuteSpeech_RollingWindowOf50Words_AllWordsOnceInOrder()
        {
            string[] words = Enumerable.Range(1, 90)
                .SelectMany(i => $"Point {i} of the question is about part {i} of the system.".Split(' '))
                .ToArray(); // 900 words

            var m = new TranscriptMerger();
            for (int end = 4; end <= words.Length; end += 4)
            {
                m.Merge(string.Join(" ", words[Math.Max(0, end - 50)..end]));
            }

            Assert.Equal(string.Join(" ", words), m.Full);
        }

        [Fact]
        public void LastWordCorrected_BarToBank()
        {
            var m = MergeAll("we walked all the way down to the bar", "we walked all the way down to the bank and then went home");
            Assert.Equal("we walked all the way down to the bank and then went home", m.Full);
        }

        [Fact]
        public void PartialLastWord_BaToBank()
        {
            var m = MergeAll("we drove all the way down to the ba", "we drove all the way down to the bank and parked");
            Assert.Equal("we drove all the way down to the bank and parked", m.Full);
        }

        [Fact]
        public void TwoLastWordsCorrected()
        {
            var m = MergeAll(
                "the meeting is scheduled for next Tuesday after",
                "the meeting is scheduled for next Thursday afternoon at three");
            Assert.Equal("the meeting is scheduled for next Thursday afternoon at three", m.Full);
        }

        [Fact]
        public void CapitalsAndPunctuationAddedLater_CSharpToCSharpSymbol()
        {
            var m = MergeAll(
                "hello can you tell me what dependency injection is in c sharp",
                "Hello, can you tell me what dependency injection is in C#?");
            Assert.Equal("Hello, can you tell me what dependency injection is in C#?", m.Full);
        }

        [Fact]
        public void CapturesClearedAndRestarted_NewTextAppended()
        {
            var m = new TranscriptMerger();
            m.Merge("The first conversation was about databases and indexes.");
            Assert.Equal(MergeKind.Unchanged, m.Merge("").Kind);
            Assert.Equal("The first conversation was about databases and indexes.", m.Full);

            MergeResult r = m.Merge("Completely different sentence about the weather.");
            Assert.Equal(MergeKind.Appended, r.Kind);
            Assert.Equal("The first conversation was about databases and indexes. Completely different sentence about the weather.", m.Full);
        }

        [Fact]
        public void RestartedWithShortCommonWord_DoesNotTruncate()
        {
            var m = MergeAll("the quick brown fox jumps over the lazy dog");
            m.Merge("the");
            Assert.Equal("the quick brown fox jumps over the lazy dog", m.Full);

            m.Merge("the next topic is testing");
            Assert.Equal("the quick brown fox jumps over the lazy dog the next topic is testing", m.Full);
        }

        [Fact]
        public void RestartedWithCommonPhrase_DoesNotDropOldWords()
        {
            var m = MergeAll("We talked for a while and then I went home");
            m.Merge("and then I realized it was late");
            Assert.Contains("went home", m.Full);
            Assert.EndsWith("and then I realized it was late", m.Full);
        }

        [Fact]
        public void RepeatedIdenticalPolls_ChangeNothing()
        {
            var m = new TranscriptMerger();
            m.Merge("one two three four five");
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(MergeKind.Unchanged, m.Merge("one two three four five").Kind);
            }

            Assert.Equal("one two three four five", m.Full);
        }

        [Fact]
        public void ShorterWindowInsideTranscript_ChangesNothing()
        {
            var m = MergeAll("alpha beta gamma delta epsilon zeta eta theta iota kappa");
            MergeResult r = m.Merge("gamma delta epsilon zeta");
            Assert.Equal(MergeKind.Unchanged, r.Kind);
            Assert.Equal("alpha beta gamma delta epsilon zeta eta theta iota kappa", m.Full);
        }

        // ---------- Facts learned in LiveCaptionsGemini ----------

        [Fact]
        public void BigRewriteOfTrailingWords_IIII_ToII_NoDuplicate()
        {
            var m = MergeAll(
                "We were talking about the data model for the products and the images. And then I I I I",
                "We were talking about the data model for the products and the images. And then II realized it was wrong.");

            Assert.Equal(1, Count(m.Full, "data model"));
            Assert.Equal("We were talking about the data model for the products and the images. And then II realized it was wrong.", m.Full);
        }

        [Fact]
        public void BigRewrite_WhileScrolling_NoDuplicate()
        {
            string start = "So the first thing I want to ask is about your experience with distributed systems. ";
            var m = MergeAll(
                start + "In my last job I I I I worked on a platform that",
                "experience with distributed systems. In my last job II worked on a platform that processed payments for many shops.");

            Assert.Equal(1, Count(m.Full, "distributed systems"));
            Assert.Equal(start + "In my last job II worked on a platform that processed payments for many shops.", m.Full);
        }

        [Fact]
        public void ManyWordsRewrittenInsideWindow_NoDuplicateNoLoss()
        {
            string w1 = "Tell me about a time you had a conflict with a colleague and how you solved it in the end we talked about it for a long time";
            string w2 = "Tell me about a time you had a conflict with a colleague and how you solved it. In the end, we talked about it for a long time, and we agreed on a plan.";
            var m = MergeAll(w1, w2);
            Assert.Equal(w2, m.Full);
        }

        [Fact]
        public void RepeatedPhrase_MatchesTheRightOccurrence()
        {
            var m = MergeAll(
                "you know what I mean, and you know what I mean",
                "you know what I mean, and you know what I mean again and again");
            Assert.Equal("you know what I mean, and you know what I mean again and again", m.Full);
        }

        [Fact]
        public void SameQuestionAskedTwice_QuestionsInBetweenKept()
        {
            const string q = "Okay, so let me start with the basics. What is C sharp? How does garbage collection work? What is a delegate? What is C sharp?";
            var m = MergeAll(
                "Okay, so let me start with the basics. What is C sharp?",
                "Okay, so let me start with the basics. What is C sharp? How does garbage collection work?",
                q);
            Assert.Equal(q, m.Full);
        }

        [Fact]
        public void WindowScrollsWhileSpeakerRepeatsSentence_NoLoss()
        {
            var m = MergeAll(
                "Is that clear? Let me say it again. The cache is invalidated on every write.",
                "Let me say it again. The cache is invalidated on every write. The cache is invalidated on every write.");
            Assert.Equal("Is that clear? Let me say it again. The cache is invalidated on every write. The cache is invalidated on every write.", m.Full);
        }

        [Fact]
        public void HeadOfWindowRewritten_RetriesWithLaterWords()
        {
            var m = MergeAll(
                "we use c sharp and dot net for the back end and react for the front end of the site",
                "C# and .NET for the back end and React for the front end of the site, and we deploy to Azure.");
            Assert.Equal(1, Count(m.Full, "front end"));
            Assert.EndsWith("we deploy to Azure.", m.Full);
            Assert.StartsWith("we use c sharp and dot net", m.Full, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PunctuationOnlyChange_IsApplied()
        {
            var m = MergeAll("so what is c sharp tell me", "So, what is C sharp? Tell me.");
            Assert.Equal("So, what is C sharp? Tell me.", m.Full);
        }

        [Fact]
        public void ApostropheVariants_TreatedAsSameWord()
        {
            var m = MergeAll("I dont think thats the right way to do it", "I don't think that's the right way to do it, honestly.");
            Assert.Equal("I don't think that's the right way to do it, honestly.", m.Full);
        }

        [Fact]
        public void LongSessionWithManyCorrections_FinalTextExact()
        {
            // The final truth, revealed word by word with temporary mistakes, inside a 40-word rolling window.
            string[] truth = ("Thanks for coming in today. Can you walk me through how you would design a URL shortener? " +
                              "Think about the database, the caching layer, and how you would handle millions of requests per day. " +
                              "Also tell me how you would generate the short codes and avoid collisions. " +
                              "Finally, what metrics would you monitor in production and why?").Split(' ');
            var m = new TranscriptMerger();
            for (int end = 1; end <= truth.Length; end++)
            {
                string[] visible = truth[Math.Max(0, end - 40)..end].ToArray();

                // While a word is new, Live Captions first shows it lower-case without punctuation and maybe wrong.
                string last = visible[^1];
                string rough = new string(last.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (end % 7 == 0)
                {
                    rough = rough.Length > 2 ? rough.Substring(0, 2) : rough + "x"; // partial / wrong word
                }

                visible[^1] = rough;
                m.Merge(string.Join(" ", visible));
                m.Merge(string.Join(" ", truth[Math.Max(0, end - 40)..end]));
            }

            Assert.Equal(string.Join(" ", truth), m.Full);
        }

        // ---------- MapIndex (sentIndex tracking) ----------

        [Fact]
        public void MapIndex_GrowthKeepsPosition()
        {
            var m = MergeAll("So tell me about your last project");
            int sent = m.Full.Length;
            MergeResult r = m.Merge("So tell me about your last project and the team.");
            int mapped = r.MapIndex(sent);
            Assert.Equal("and the team.", m.Full.Substring(mapped).Trim());
        }

        [Fact]
        public void MapIndex_RewrittenLastWord_IsSentAgain()
        {
            var m = MergeAll("we drove all the way down to the ba");
            int sent = m.Full.Length;
            MergeResult r = m.Merge("we drove all the way down to the bank and parked");
            Assert.Equal("bank and parked", m.Full.Substring(r.MapIndex(sent)).Trim());
        }

        [Fact]
        public void MapIndex_AfterScrolling_PointsToSameWord()
        {
            var m = MergeAll("one two three four five six seven eight nine ten");
            int sent = m.Full.IndexOf("six", StringComparison.Ordinal) + "six".Length;
            MergeResult r = m.Merge("four five six seven eight nine ten eleven twelve");
            Assert.Equal("seven eight nine ten eleven twelve", m.Full.Substring(r.MapIndex(sent)).Trim());
        }

        [Fact]
        public void MapIndex_AppendKeepsOldPositions()
        {
            var m = MergeAll("The first conversation was about databases.");
            int sent = m.Full.Length;
            MergeResult r = m.Merge("Completely different sentence about the weather.");
            Assert.Equal(MergeKind.Appended, r.Kind);
            Assert.Equal("Completely different sentence about the weather.", m.Full.Substring(r.MapIndex(sent)).Trim());
        }

        [Fact]
        public void Tokenize_SkipsPartialFirstWordAndKeepsSpans()
        {
            var words = TranscriptMerger.Tokenize("hello world, it's C#!", 2);
            Assert.Equal(new[] { "world", "its", "c" }, words.Select(w => w.Norm).ToArray());
            Assert.Equal("world", "hello world, it's C#!".Substring(words[0].Start, words[0].End - words[0].Start));
        }
    }
}
