using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CaptionRelay.Captions;
using Xunit;
using Xunit.Abstractions;

namespace CaptionRelay.Tests
{
    /// <summary>
    /// Replays real Live Captions snapshots (recorded with tools/CaptionRecorder while tools/tts/speak.ps1 read
    /// tools/tts/interview-script.txt aloud) through TranscriptMerger.
    /// </summary>
    public class ReplayTests
    {
        private const string Fixture = "livecaptions-tts-interview.jsonl";
        private const string Script = "interview-script.txt";

        private readonly ITestOutputHelper _out;

        public ReplayTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void RealSnapshots_TranscriptCoversScript_NoDuplicates()
        {
            List<string> snapshots = LoadSnapshots();
            var m = new TranscriptMerger();
            var kinds = new Dictionary<MergeKind, int>();
            foreach (string s in snapshots)
            {
                MergeKind k = m.Merge(s).Kind;
                kinds[k] = kinds.TryGetValue(k, out int n) ? n + 1 : 1;
            }

            string[] full = Words(m.Full);
            string[] script = ScriptWords();
            int lcs = Lcs(full, script);
            double coverage = (double)lcs / script.Length;

            // A 6-gram may repeat as often as in the script or in Live Captions' own final text (the script
            // repeats one question on purpose, and Live Captions heard "scoped" as "scope" both times).
            List<string> extraRepeats = RepeatedNGrams(full, script, 6)
                .Intersect(RepeatedNGrams(full, Words(snapshots[^1]), 6))
                .ToList();

            _out.WriteLine($"snapshots={snapshots.Count} longestSnapshotWords={snapshots.Max(s => Words(s).Length)}");
            _out.WriteLine("merge kinds: " + string.Join(", ", kinds.Select(p => $"{p.Key}={p.Value}")));
            _out.WriteLine($"scriptWords={script.Length} transcriptWords={full.Length} inOrderMatches={lcs} coverage={coverage:P1}");
            _out.WriteLine("repeated 6-grams not repeated in the script: " + extraRepeats.Count + (extraRepeats.Count > 0 ? " e.g. " + string.Join(" | ", extraRepeats.Take(5)) : ""));

            Assert.EndsWith(snapshots[^1], m.Full);
            if (Words(snapshots[^1]).Take(5).SequenceEqual(script.Take(5)))
            {
                // Nothing scrolled off in this recording: the transcript must be exactly Live Captions' final text.
                Assert.Equal(snapshots[^1], m.Full);
            }

            Assert.Empty(extraRepeats);
            Assert.True(full.Length <= script.Length * 1.1, $"transcript has {full.Length} words, script {script.Length}");
            Assert.True(coverage >= 0.85, $"coverage {coverage:P1}");
        }

        [Theory]
        [InlineData(7)]
        [InlineData(23)]
        [InlineData(61)]
        public void RealSnapshots_SendsAtAnyMoment_LoseNothing(int sendEvery)
        {
            List<string> snapshots = LoadSnapshots();
            var m = new TranscriptMerger();
            int sentIndex = 0;
            var sent = new List<string>();

            for (int i = 0; i < snapshots.Count; i++)
            {
                MergeResult r = m.Merge(snapshots[i]);
                sentIndex = r.MapIndex(sentIndex);
                if (i % sendEvery == sendEvery - 1)
                {
                    sent.Add(m.Full.Substring(sentIndex));
                    sentIndex = m.Full.Length;
                }
            }

            sent.Add(m.Full.Substring(sentIndex));

            string[] full = Words(m.Full);
            string[] delivered = sent.SelectMany(Words).ToArray();
            int lcs = Lcs(delivered, full);
            int duplicated = delivered.Length - lcs;
            List<Gap> gaps = DiffGaps(delivered, full);
            List<Gap> losses = gaps.Where(g => g.Missing.Count > 0 && g.Extra.Count == 0).ToList();
            _out.WriteLine($"sends={sent.Count} transcriptWords={full.Length} deliveredWords={delivered.Length} inOrder={lcs} extraWords={duplicated}");
            foreach (Gap g in gaps)
            {
                string kind = g.Missing.Count == 0 ? "duplicate" : g.Extra.Count == 0 ? "LOST" : "rewritten after send";
                _out.WriteLine($"  {kind}: delivered [{string.Join(" ", g.Extra)}] transcript [{string.Join(" ", g.Missing)}]");
            }

            // Every transcript word reached Gemini, either as it is now or in the form it had when it was sent
            // (Live Captions rewrites words long after: "dot net eight" -> ".net 8", "I I I" -> "III").
            Assert.Empty(losses);
            Assert.True(duplicated <= 3 * sent.Count, $"{duplicated} extra words over {sent.Count} sends");
        }

        /// <summary>
        /// Live Captions did not scroll during this 4-minute recording, so scrolling is simulated on the real
        /// snapshots: only the last N words of each one are visible (real rewrites + rolling window together).
        /// </summary>
        [Theory]
        [InlineData(40, 1)]
        [InlineData(80, 1)]
        [InlineData(150, 1)]
        [InlineData(80, 13)]
        [InlineData(150, 29)]
        public void RealSnapshots_WithSimulatedScrolling_LoseNothing_NoDuplicates(int visibleWords, int sendEvery)
        {
            List<string> snapshots = LoadSnapshots().Select(s => LastWords(s, visibleWords)).ToList();
            var m = new TranscriptMerger();
            int sentIndex = 0;
            var sent = new List<string>();
            var kinds = new Dictionary<MergeKind, int>();
            for (int i = 0; i < snapshots.Count; i++)
            {
                MergeResult r = m.Merge(snapshots[i]);
                kinds[r.Kind] = kinds.TryGetValue(r.Kind, out int n) ? n + 1 : 1;
                sentIndex = r.MapIndex(sentIndex);
                if (sendEvery > 1 && i % sendEvery == sendEvery - 1)
                {
                    sent.Add(m.Full.Substring(sentIndex));
                    sentIndex = m.Full.Length;
                }
            }

            sent.Add(m.Full.Substring(sentIndex));

            string[] truth = Words(LoadSnapshots()[^1]); // Live Captions' own final text of the whole recording
            string[] full = Words(m.Full);
            string[] delivered = sent.SelectMany(Words).ToArray();

            List<Gap> transcriptGaps = DiffGaps(full, truth);
            List<Gap> deliveredGaps = DiffGaps(delivered, full);
            _out.WriteLine("merge kinds: " + string.Join(", ", kinds.Select(p => $"{p.Key}={p.Value}")));
            _out.WriteLine($"truthWords={truth.Length} transcriptWords={full.Length} deliveredWords={delivered.Length} sends={sent.Count}");
            foreach (Gap g in transcriptGaps)
            {
                _out.WriteLine($"  transcript vs truth: transcript [{string.Join(" ", g.Extra)}] truth [{string.Join(" ", g.Missing)}]");
            }

            Assert.Empty(transcriptGaps.Where(g => g.Missing.Count > 0 && g.Extra.Count == 0)); // nothing lost
            Assert.True(transcriptGaps.Sum(g => Math.Max(0, g.Extra.Count - g.Missing.Count)) <= 6, "too many extra words in the transcript");
            Assert.Empty(RepeatedNGrams(full, truth, 6));
            Assert.Empty(deliveredGaps.Where(g => g.Missing.Count > 0 && g.Extra.Count == 0));
        }

        private static string LastWords(string text, int n)
        {
            var words = TranscriptMerger.Tokenize(text, 0);
            return words.Count <= n ? text : text.Substring(words[words.Count - n].Start);
        }

        private static List<string> LoadSnapshots()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
            var list = new List<string>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                using JsonDocument doc = JsonDocument.Parse(line);
                list.Add(doc.RootElement.GetProperty("text").GetString() ?? "");
            }

            return list;
        }

        private static string[] ScriptWords()
        {
            string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", Script));
            int marker = text.IndexOf("starts now.", StringComparison.OrdinalIgnoreCase);
            return Words(text.Substring(marker + "starts now.".Length));
        }

        private static string[] Words(string text) =>
            TranscriptMerger.Tokenize(text, 0).Select(w => w.Norm).ToArray();

        private static int Lcs(string[] a, string[] b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
                }

                (prev, cur) = (cur, prev);
            }

            return prev[b.Length];
        }

        private sealed class Gap
        {
            public List<string> Extra { get; } = new List<string>();   // only in the delivered text

            public List<string> Missing { get; } = new List<string>(); // only in the transcript
        }

        /// <summary>Differences between delivered words <paramref name="a"/> and transcript words <paramref name="b"/> (LCS diff).</summary>
        private static List<Gap> DiffGaps(string[] a, string[] b)
        {
            var t = new int[a.Length + 1, b.Length + 1];
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
                }
            }

            var gaps = new List<Gap>();
            Gap? current = null;
            int x = a.Length;
            int y = b.Length;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && a[x - 1] == b[y - 1])
                {
                    current = null;
                    x--;
                    y--;
                    continue;
                }

                if (current == null)
                {
                    current = new Gap();
                    gaps.Add(current);
                }

                if (x > 0 && (y == 0 || t[x - 1, y] >= t[x, y - 1]))
                {
                    current.Extra.Insert(0, a[x - 1]);
                    x--;
                }
                else
                {
                    current.Missing.Insert(0, b[y - 1]);
                    y--;
                }
            }

            gaps.Reverse();
            return gaps;
        }

        private static List<string> RepeatedNGrams(string[] text, string[] reference, int n)
        {
            Dictionary<string, int> Count(string[] words)
            {
                var d = new Dictionary<string, int>();
                for (int i = 0; i + n <= words.Length; i++)
                {
                    string g = string.Join(" ", words, i, n);
                    d[g] = d.TryGetValue(g, out int c) ? c + 1 : 1;
                }

                return d;
            }

            Dictionary<string, int> inText = Count(text);
            Dictionary<string, int> inRef = Count(reference);
            return inText.Where(p => p.Value > 1 && p.Value > (inRef.TryGetValue(p.Key, out int r) ? r : 0))
                         .Select(p => p.Key)
                         .ToList();
        }
    }
}
