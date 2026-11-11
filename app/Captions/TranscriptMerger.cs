using System;
using System.Collections.Generic;
using System.Text;

namespace CaptionRelay.Captions
{
    public enum MergeKind
    {
        /// <summary>Empty window, identical window, or a window already contained in the transcript.</summary>
        Unchanged,

        /// <summary>The window was located in the transcript tail; everything from there on was replaced by it.</summary>
        Replaced,

        /// <summary>The window did not overlap the transcript (new session or captions restarted); appended whole.</summary>
        Appended,

        /// <summary>
        /// The window clearly overlaps the transcript but could not be aligned safely to its end; the transcript was
        /// kept and only the window's unmatched end was appended (may duplicate a few words, never loses any).
        /// </summary>
        OverlapAppended,
    }

    /// <summary>Outcome of one <see cref="TranscriptMerger.Merge"/> call.</summary>
    public sealed class MergeResult
    {
        private readonly List<(int OldEnd, int NewEnd)> _pairs;
        private string? _oldText;
        private string? _newText;

        internal MergeResult(MergeKind kind, int stableLength, int newLength, List<(int OldEnd, int NewEnd)>? pairs = null)
        {
            Kind = kind;
            StableLength = stableLength;
            NewLength = newLength;
            _pairs = pairs ?? new List<(int OldEnd, int NewEnd)>();
        }

        public MergeKind Kind { get; }

        /// <summary>Number of leading characters of the previous transcript that are unchanged in the new one.</summary>
        public int StableLength { get; }

        public int NewLength { get; }

        public bool Changed => Kind != MergeKind.Unchanged;

        /// <summary>
        /// Maps a position in the previous transcript (e.g. sentIndex) to the new one. Words that were matched keep
        /// their place; a position inside rewritten words moves back to the end of the last matched word, so a
        /// rewritten word is sent again rather than lost.
        /// </summary>
        public int MapIndex(int oldIndex)
        {
            if (oldIndex <= StableLength)
            {
                return Math.Min(oldIndex, NewLength);
            }

            int mapped = StableLength;
            int matchedOldEnd = -1;
            foreach ((int oldEnd, int newEnd) in _pairs)
            {
                if (oldEnd > oldIndex)
                {
                    break;
                }

                mapped = newEnd;
                matchedOldEnd = oldEnd;
            }

            // Keep punctuation that followed the word ("video." stays before the cut, not after it).
            if (matchedOldEnd >= 0 && _oldText != null && _newText != null && HasPunctuation(_oldText, matchedOldEnd, oldIndex))
            {
                while (mapped < _newText.Length && IsPunctuation(_newText[mapped]))
                {
                    mapped++;
                }
            }

            return Math.Min(mapped, NewLength);
        }

        internal string? OldTextForMapping
        {
            set => _oldText = value;
        }

        internal string? NewTextForMapping
        {
            set => _newText = value;
        }

        private static bool IsPunctuation(char c) => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);

        private static bool HasPunctuation(string text, int from, int to)
        {
            for (int i = from; i < to && i < text.Length; i++)
            {
                if (IsPunctuation(text[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Builds one growing session transcript from successive Live Captions snapshots.
    ///
    /// Live Captions shows a rolling window: old lines scroll off the top, the last words keep being corrected
    /// (several at once during long speech, e.g. "I I I I" → "II", "C sharp" → "C#?"), and punctuation/capitals
    /// arrive later. Each window is located in the tail of the transcript by its first words (retrying from its
    /// 2nd…5th word), compared word by word ignoring case and punctuation, and aligned with small rewrites
    /// tolerated. Text after the match is replaced by the window, so corrections apply.
    ///
    /// Safety rules: losing text is never acceptable, a few duplicated words are. A window that only matches by a
    /// few common words is appended instead of replacing; a window already contained in the transcript changes
    /// nothing; an overlap that cannot be aligned to the end keeps the transcript and appends only the new end.
    /// Not thread-safe: call from one thread (the poller) or under a lock.
    /// </summary>
    public sealed class TranscriptMerger
    {
        /// <summary>Key length in words (≈ 40 chars).</summary>
        public const int KeyWords = 8;

        /// <summary>Key start positions tried: the window's 1st … 5th word.</summary>
        public const int MaxKeyShifts = 5;

        /// <summary>Minimum key length when the key does not start at the window's first word.</summary>
        public const int MinShiftedKeyWords = 3;

        /// <summary>Only this much of the transcript end is searched (at least twice the window length).</summary>
        public const int SearchTailChars = 3000;

        /// <summary>How many words may be skipped on either side to re-synchronize after a rewritten word.</summary>
        public const int ResyncWords = 12;

        /// <summary>Fewer matched words than this is too weak to justify dropping more than one transcript word.</summary>
        public const int WeakMatchWords = 6;

        /// <summary>Upper bound on transcript words a single merge may treat as rewritten.</summary>
        public const int MaxRewrittenWords = 40;

        /// <summary>Trailing transcript words a window may drop without replacing them (word merges like "C sharp" → "C#").</summary>
        public const int MaxMergedAwayWords = 3;

        /// <summary>Matched words needed to treat an unalignable window as overlapping (append only its end).</summary>
        public const int OverlapMatchWords = 8;

        private string _full = "";
        private string _lastWindow = "";

        /// <summary>The whole session transcript so far.</summary>
        public string Full => _full;

        public void Reset()
        {
            _full = "";
            _lastWindow = "";
        }

        /// <summary>Merges the current Live Captions window into <see cref="Full"/>.</summary>
        public MergeResult Merge(string? window)
        {
            string w = (window ?? "").Trim();
            int oldLength = _full.Length;

            if (w.Length == 0 || w == _lastWindow)
            {
                return Unchanged();
            }

            _lastWindow = w;
            List<Word> wWords = Tokenize(w, 0);
            if (wWords.Count == 0)
            {
                return Unchanged();
            }

            if (oldLength == 0)
            {
                _full = w;
                return new MergeResult(MergeKind.Appended, 0, _full.Length);
            }

            int tailFrom = Math.Max(0, oldLength - Math.Max(SearchTailChars, 2 * w.Length));
            List<Word> fWords = Tokenize(_full, tailFrom);

            Alignment? best = null;     // best acceptable candidate
            Alignment? strongest = null; // most matched words, acceptable or not
            int maxShift = Math.Min(MaxKeyShifts, wWords.Count);
            for (int s = 0; s < maxShift; s++)
            {
                // At most half of the remaining window, so the key stays clear of the rewritten tail.
                int k = Math.Min(KeyWords, Math.Max(1, (wWords.Count - s + 1) / 2));
                if (s > 0 && k < MinShiftedKeyWords)
                {
                    break;
                }

                for (int q = 0; q < fWords.Count; q++)
                {
                    // Near the transcript end the key may be cut short: the window extends past it.
                    if (!KeyMatches(fWords, q, wWords, s, Math.Min(k, fWords.Count - q)))
                    {
                        continue;
                    }

                    Alignment a = Align(fWords, q, wWords, s);
                    if (strongest == null || a.Matched > strongest.Matched)
                    {
                        strongest = a;
                    }

                    if (a.IsAcceptable && (best == null || a.IsBetterThan(best)))
                    {
                        best = a;
                    }
                }
            }

            if (best != null)
            {
                return best.IsStale ? Unchanged() : ApplyReplace(best, fWords, wWords, w);
            }

            if (strongest != null && strongest.Matched >= OverlapMatchWords)
            {
                return ApplyAppend(w, strongest.WEnd < wWords.Count ? wWords[strongest.WEnd].Start : w.Length, MergeKind.OverlapAppended);
            }

            return ApplyAppend(w, 0, MergeKind.Appended);

            MergeResult Unchanged() => new MergeResult(MergeKind.Unchanged, oldLength, oldLength);
        }

        private MergeResult ApplyReplace(Alignment a, List<Word> fWords, List<Word> wWords, string w)
        {
            int p = fWords[a.Q].Start;
            int wFrom = wWords[a.S].Start;
            string prefix = _full.Substring(0, p);
            if (prefix.Length > 0 && !char.IsWhiteSpace(prefix[prefix.Length - 1]))
            {
                prefix += " ";
            }

            string newFull = prefix + w.Substring(wFrom);
            var pairs = new List<(int OldEnd, int NewEnd)>(a.Pairs.Count);
            foreach ((int i, int j) in a.Pairs)
            {
                pairs.Add((fWords[i].End, prefix.Length + (wWords[j].End - wFrom)));
            }

            if (newFull == _full)
            {
                return new MergeResult(MergeKind.Unchanged, _full.Length, _full.Length);
            }

            string oldFull = _full;
            _full = newFull;
            return new MergeResult(MergeKind.Replaced, p, _full.Length, pairs) { OldTextForMapping = oldFull, NewTextForMapping = newFull };
        }

        private MergeResult ApplyAppend(string w, int wFrom, MergeKind kind)
        {
            int oldLength = _full.Length;
            string add = w.Substring(wFrom).Trim();
            if (add.Length == 0)
            {
                return new MergeResult(MergeKind.Unchanged, oldLength, oldLength);
            }

            _full = _full.TrimEnd() + " " + add;
            return new MergeResult(kind, Math.Min(oldLength, _full.Length), _full.Length);
        }

        private static bool KeyMatches(List<Word> f, int q, List<Word> w, int s, int k)
        {
            for (int n = 0; n < k; n++)
            {
                if (f[q + n].Norm != w[s + n].Norm)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Walks transcript words from q and window words from s, re-synchronizing over small rewrites.</summary>
        private static Alignment Align(List<Word> f, int q, List<Word> w, int s)
        {
            var pairs = new List<(int I, int J)>();
            int i = q;
            int j = s;
            while (i < f.Count && j < w.Count)
            {
                if (f[i].Norm == w[j].Norm)
                {
                    pairs.Add((i, j));
                    i++;
                    j++;
                    continue;
                }

                if (!TryResync(f, i, w, j, out int di, out int dj))
                {
                    break;
                }

                i += di;
                j += dj;
            }

            return new Alignment(q, s, pairs, i, j, f.Count, w.Count);
        }

        /// <summary>Smallest total skip (di + dj) after which both sides agree on two words (or one at an end).</summary>
        private static bool TryResync(List<Word> f, int i, List<Word> w, int j, out int di, out int dj)
        {
            for (int total = 1; total <= 2 * ResyncWords; total++)
            {
                for (int a = Math.Max(0, total - ResyncWords); a <= Math.Min(total, ResyncWords); a++)
                {
                    int b = total - a;
                    int fi = i + a;
                    int wj = j + b;
                    if (fi >= f.Count || wj >= w.Count || f[fi].Norm != w[wj].Norm)
                    {
                        continue;
                    }

                    bool atEnd = fi + 1 >= f.Count || wj + 1 >= w.Count;
                    if (atEnd || f[fi + 1].Norm == w[wj + 1].Norm)
                    {
                        di = a;
                        dj = b;
                        return true;
                    }
                }
            }

            di = 0;
            dj = 0;
            return false;
        }

        /// <summary>
        /// Words = runs of letters/digits, lower-cased; apostrophes inside a word are dropped ("don't" = "dont");
        /// everything else separates words. If <paramref name="from"/> cuts a word, that partial word is skipped.
        /// </summary>
        internal static List<Word> Tokenize(string s, int from)
        {
            var words = new List<Word>();
            var sb = new StringBuilder();
            int start = -1;
            int end = -1;
            bool skipFirst = from > 0 && char.IsLetterOrDigit(s[from - 1]);

            for (int idx = from; idx <= s.Length; idx++)
            {
                char ch = idx < s.Length ? s[idx] : ' ';
                if (char.IsLetterOrDigit(ch))
                {
                    if (start < 0)
                    {
                        start = idx;
                    }

                    sb.Append(char.ToLowerInvariant(ch));
                    end = idx + 1;
                    continue;
                }

                if ((ch == '\'' || ch == '’') && start >= 0 && idx + 1 < s.Length && char.IsLetterOrDigit(s[idx + 1]))
                {
                    continue;
                }

                if (start >= 0)
                {
                    if (skipFirst)
                    {
                        skipFirst = false;
                    }
                    else
                    {
                        words.Add(new Word(start, end, sb.ToString()));
                    }

                    sb.Clear();
                    start = -1;
                }
                else
                {
                    skipFirst = false;
                }
            }

            return words;
        }

        internal readonly struct Word
        {
            public Word(int start, int end, string norm)
            {
                Start = start;
                End = end;
                Norm = norm;
            }

            public int Start { get; }

            public int End { get; }

            public string Norm { get; }

            public override string ToString() => Norm;
        }

        private sealed class Alignment
        {
            public Alignment(int q, int s, List<(int I, int J)> pairs, int fEnd, int wEnd, int fCount, int wCount)
            {
                Q = q;
                S = s;
                Pairs = pairs;
                WEnd = wEnd;
                Matched = pairs.Count;
                UnmatchedTranscriptWords = fCount - fEnd;

                // At least 3 matched words, unless the window or the rest of the transcript is shorter than that.
                int required = Math.Min(MinShiftedKeyWords, Math.Min(wCount - s, fCount - q));

                // How many trailing transcript words may be treated as rewritten (dropped and replaced).
                int allowed = Matched < WeakMatchWords ? 1 : Math.Min(MaxRewrittenWords, Math.Max(4, Matched / 2));
                bool windowConsumed = wEnd >= wCount;
                if (windowConsumed)
                {
                    // Nothing in the window replaces them: only a few merged words ("C sharp" → "C#") may go.
                    allowed = Math.Min(allowed, MaxMergedAwayWords);
                }

                // The window ended inside the transcript with more left over: it is behind it, change nothing.
                IsStale = windowConsumed && UnmatchedTranscriptWords > allowed;
                IsAcceptable = Matched >= required && (IsStale || UnmatchedTranscriptWords <= allowed);
            }

            public int Q { get; }

            public int S { get; }

            public List<(int I, int J)> Pairs { get; }

            public int WEnd { get; }

            public int Matched { get; }

            public int UnmatchedTranscriptWords { get; }

            public bool IsStale { get; }

            public bool IsAcceptable { get; }

            public bool IsBetterThan(Alignment other)
            {
                if (Matched != other.Matched)
                {
                    return Matched > other.Matched;
                }

                if (UnmatchedTranscriptWords != other.UnmatchedTranscriptWords)
                {
                    return UnmatchedTranscriptWords < other.UnmatchedTranscriptWords;
                }

                return Q > other.Q;
            }
        }
    }
}
