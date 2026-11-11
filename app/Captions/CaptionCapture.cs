using System;
using System.Collections.Generic;
using System.Threading;
using CaptionRelay.Infrastructure;

namespace CaptionRelay.Captions
{
    public enum TakeStatus
    {
        Ok,
        NothingNew,
        CaptionsUnavailable,
    }

    /// <summary>One send in flight: confirm it when the extension acks, restore it when delivery fails.</summary>
    public sealed class SendTicket
    {
        internal SendTicket(string text, int cut, int previous, DateTime at)
        {
            Text = text;
            Cut = cut;
            Previous = previous;
            At = at;
        }

        public string Text { get; }

        public DateTime At { get; }

        internal int Cut { get; set; }

        internal int Previous { get; set; }
    }

    public sealed class TakeResult
    {
        internal TakeResult(TakeStatus status, string text = "", SendTicket? ticket = null)
        {
            Status = status;
            Text = text;
            Ticket = ticket;
        }

        public TakeStatus Status { get; }

        public string Text { get; }

        public SendTicket? Ticket { get; }
    }

    public sealed class CaptureStatus
    {
        internal CaptureStatus(bool available, int pendingWords, string pendingTail)
        {
            CaptionsAvailable = available;
            PendingWords = pendingWords;
            PendingTail = pendingTail;
        }

        public bool CaptionsAvailable { get; }

        public int PendingWords { get; }

        /// <summary>The last ~300 chars of the pending text (for the indicator's expand panel).</summary>
        public string PendingTail { get; }
    }

    public sealed class CaptureOptions
    {
        public int PollMs { get; set; } = 120;

        /// <summary>While speech goes on, the last words may still be rewritten: they are not written to the file yet.</summary>
        public int HoldBackWords { get; set; } = 40;

        /// <summary>After this long without changes everything is written.</summary>
        public TimeSpan IdleCommit { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan WriteInterval { get; set; } = TimeSpan.FromSeconds(2);

        public bool RestartLiveCaptions { get; set; } = true;

        public TimeSpan RestartInterval { get; set; } = TimeSpan.FromSeconds(5);

        public int PendingTailChars { get; set; } = 300;
    }

    /// <summary>
    /// Continuous capture: polls Live Captions on a background thread, merges every snapshot into one session
    /// transcript (<see cref="TranscriptMerger"/>), keeps <c>sentIndex</c> (pending = transcript after it) and writes
    /// the transcript with [SENT]/[SKIPPED] markers to the session file. Text on screen when capture starts belongs
    /// to an earlier conversation: it is written to the file but marked skipped, never sent.
    /// All positions (sentIndex, file progress, markers, tickets) are mapped through every merge, so rewrites by
    /// Live Captions never make text disappear. Thread-safe.
    /// </summary>
    public sealed class CaptionCapture : IDisposable
    {
        private static readonly char[] LeadingJunk = { '.', ',', ';', ':', '!', '?', '-', '…' };

        private readonly ICaptionSource _source;
        private readonly SessionWriter? _writer;
        private readonly CaptureOptions _options;
        private readonly Func<DateTime> _now;
        private readonly Func<bool>? _ensureLiveCaptions;

        private readonly object _lock = new object();
        private readonly TranscriptMerger _merger = new TranscriptMerger();
        private readonly List<Marker> _markers = new List<Marker>();
        private readonly List<SendTicket> _open = new List<SendTicket>();
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);

        private Thread? _thread;
        private bool _started;
        private bool _available;
        private int _sentIndex;
        private int _committedIndex;
        private int _forceCommit;
        private bool _newParagraph = true;
        private DateTime _lastChange;
        private DateTime _lastWrite;
        private DateTime _lastRestart = DateTime.MinValue;
        private bool _restartFailedLogged;
        private bool _disposed;

        public CaptionCapture(ICaptionSource source, SessionWriter? writer, CaptureOptions? options = null, Func<DateTime>? now = null, Func<bool>? ensureLiveCaptions = null)
        {
            _source = source;
            _writer = writer;
            _options = options ?? new CaptureOptions();
            _now = now ?? (() => DateTime.Now);
            _ensureLiveCaptions = ensureLiveCaptions;
            _lastChange = _now();
            _lastWrite = _now();
            if (_writer != null)
            {
                _writer.WriteFailed += error => Log.Warn(error);
            }
        }

        /// <summary>Raised on the poller thread when availability or pending text changes. Keep handlers short.</summary>
        public event Action? StatusChanged;

        /// <summary>The whole session transcript (diagnostics and tests).</summary>
        public string Transcript
        {
            get
            {
                lock (_lock)
                {
                    return _merger.Full;
                }
            }
        }

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "CaptionRelay capture" };
            _thread.Start();
        }

        public CaptureStatus GetStatus()
        {
            lock (_lock)
            {
                string pending = Clean(_merger.Full.Substring(_sentIndex));
                int words = TranscriptMerger.Tokenize(pending, 0).Count;
                string tail = pending.Length <= _options.PendingTailChars ? pending : "…" + pending.Substring(pending.Length - _options.PendingTailChars);
                return new CaptureStatus(_available, words, tail);
            }
        }

        /// <summary>
        /// Send: returns everything after sentIndex and moves sentIndex to the end. The text is kept (sentIndex not
        /// moved) when captions are unavailable or it is shorter than <paramref name="minChars"/>.
        /// </summary>
        public TakeResult Take(int minChars)
        {
            TakeResult result;
            lock (_lock)
            {
                if (!_started || !_available)
                {
                    return new TakeResult(TakeStatus.CaptionsUnavailable);
                }

                string full = _merger.Full;
                string pending = Clean(full.Substring(_sentIndex));
                if (pending.Length < Math.Max(1, minChars))
                {
                    return new TakeResult(TakeStatus.NothingNew);
                }

                var ticket = new SendTicket(pending, full.Length, _sentIndex, _now());
                _open.Add(ticket);
                _markers.Add(new Marker(full.Length, "SENT", ticket.At, confirmed: false, ticket));
                _sentIndex = full.Length;
                _forceCommit = full.Length;
                result = new TakeResult(TakeStatus.Ok, pending, ticket);
            }

            StatusChanged?.Invoke();
            return result;
        }

        /// <summary>The extension accepted the text: its [SENT] marker becomes final.</summary>
        public void Confirm(SendTicket ticket, string? note = null)
        {
            lock (_lock)
            {
                _open.Remove(ticket);
                foreach (Marker m in _markers)
                {
                    if (m.Ticket == ticket)
                    {
                        m.Confirmed = true;
                        m.Note = note;
                    }
                }
            }
        }

        /// <summary>Delivery failed: sentIndex goes back to before this send so the text is sent again next time.</summary>
        public void Restore(SendTicket ticket)
        {
            lock (_lock)
            {
                if (!_open.Remove(ticket))
                {
                    return;
                }

                _sentIndex = Math.Min(_sentIndex, ticket.Previous);
                _markers.RemoveAll(m => m.Ticket == ticket);
            }

            StatusChanged?.Invoke();
        }

        /// <summary>Skip: moves sentIndex to the end without sending. False if capture has not started yet.</summary>
        public bool Skip()
        {
            lock (_lock)
            {
                if (!_started)
                {
                    return false;
                }

                int end = _merger.Full.Length;
                _sentIndex = end;
                _forceCommit = end;
                _markers.Add(new Marker(end, "SKIPPED", _now(), confirmed: true));
            }

            StatusChanged?.Invoke();
            return true;
        }

        /// <summary>One poll: read Live Captions and merge. Called by the background thread (or directly by tests).</summary>
        internal void PollOnce()
        {
            string? text;
            try
            {
                text = _source.Read();
            }
            catch (Exception ex)
            {
                Log.Warn("Reading Live Captions failed", ex);
                text = null;
            }

            bool notify = false;
            bool restart = false;
            DateTime now = _now();
            lock (_lock)
            {
                if (text == null)
                {
                    if (_available)
                    {
                        _available = false;
                        notify = true;
                        Log.Warn("Live Captions is not available (closed or not found).");
                    }

                    if (_options.RestartLiveCaptions && _ensureLiveCaptions != null && now - _lastRestart >= _options.RestartInterval)
                    {
                        _lastRestart = now;
                        restart = true;
                    }
                }
                else
                {
                    if (!_available)
                    {
                        _available = true;
                        notify = true;
                        Log.Info("Live Captions connected.");
                    }

                    if (!_started)
                    {
                        _started = true;
                        _lastChange = now;
                        if (text.Length > 0)
                        {
                            _merger.Merge(text);
                            int end = _merger.Full.Length;
                            _sentIndex = end;
                            _forceCommit = end;
                            _markers.Add(new Marker(end, "SKIPPED", now, confirmed: true) { Note = "(text on screen at startup)" });
                            Log.Info($"Text on screen at startup ignored ({end} chars).");
                        }
                    }
                    else
                    {
                        MergeResult r = _merger.Merge(text);
                        if (r.Changed)
                        {
                            MapAll(r);
                            _lastChange = now;
                            notify = true;
                            if (r.Kind != MergeKind.Replaced)
                            {
                                Log.Info($"Merge {r.Kind}: window {text.Length} chars, transcript {r.NewLength} chars.");
                            }
                        }
                    }
                }
            }

            if (restart)
            {
                bool ok = _ensureLiveCaptions!();
                if (!ok && !_restartFailedLogged)
                {
                    Log.Warn("Could not start Live Captions.");
                }

                _restartFailedLogged = !ok;
            }

            if (notify)
            {
                StatusChanged?.Invoke();
            }
        }

        /// <summary>Writes stable text and confirmed markers to the session file. <paramref name="final"/> writes everything.</summary>
        internal void Tick(bool final = false)
        {
            lock (_lock)
            {
                DateTime now = _now();
                _lastWrite = now;
                if (_writer == null)
                {
                    return;
                }

                string full = _merger.Full;
                bool idle = now - _lastChange >= _options.IdleCommit;
                int target = idle || final ? full.Length : HoldBackStart(full);
                target = Math.Max(target, Math.Min(_forceCommit, full.Length));

                // An unconfirmed send blocks the file at its cut until it is confirmed or restored.
                foreach (Marker m in _markers)
                {
                    if (!m.Confirmed && !final)
                    {
                        target = Math.Min(target, m.Index);
                    }
                }

                target = Math.Max(target, _committedIndex);
                _markers.Sort((a, b) => a.Index.CompareTo(b.Index));

                int pos = _committedIndex;
                for (int i = 0; i < _markers.Count; i++)
                {
                    Marker m = _markers[i];
                    if (m.Index > target || (!m.Confirmed && !final))
                    {
                        continue;
                    }

                    int upTo = Math.Max(pos, Math.Min(m.Index, full.Length));
                    WriteText(full.Substring(pos, upTo - pos));
                    pos = upTo;
                    if (m.Confirmed)
                    {
                        _writer.AppendMarker(m.Kind, m.Note, m.At);
                        _newParagraph = true;
                    }

                    _markers.RemoveAt(i);
                    i--;
                }

                WriteText(full.Substring(pos, target - pos));
                _committedIndex = target;
                if (idle)
                {
                    _newParagraph = true; // a pause ends the paragraph
                }

                _writer.Flush();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Set();
            _thread?.Join(2000);
            try
            {
                Tick(final: true);
                lock (_lock)
                {
                    _writer?.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Final session write failed", ex);
            }

            _stop.Dispose();
        }

        private void Run()
        {
            while (!_stop.IsSet)
            {
                long started = Environment.TickCount64;
                try
                {
                    PollOnce();
                    if (_now() - _lastWrite >= _options.WriteInterval)
                    {
                        Tick();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Capture loop error", ex);
                }

                int wait = _options.PollMs - (int)(Environment.TickCount64 - started);
                _stop.Wait(Math.Max(1, wait));
            }
        }

        private void WriteText(string text)
        {
            if (text.Trim().Length == 0)
            {
                return;
            }

            _writer!.AppendText(_newParagraph ? Clean(text) : text, _newParagraph);
            _newParagraph = false;
        }

        private void MapAll(MergeResult r)
        {
            _sentIndex = r.MapIndex(_sentIndex);
            _committedIndex = r.MapIndex(_committedIndex);
            _forceCommit = r.MapIndex(_forceCommit);
            foreach (Marker m in _markers)
            {
                m.Index = r.MapIndex(m.Index);
            }

            foreach (SendTicket t in _open)
            {
                t.Cut = r.MapIndex(t.Cut);
                t.Previous = r.MapIndex(t.Previous);
            }
        }

        private int HoldBackStart(string full)
        {
            int from = Math.Max(0, full.Length - 3000);
            List<TranscriptMerger.Word> words = TranscriptMerger.Tokenize(full, from);
            if (words.Count <= _options.HoldBackWords)
            {
                return from == 0 ? 0 : words.Count > 0 ? words[0].Start : full.Length;
            }

            return words[words.Count - _options.HoldBackWords].Start;
        }

        /// <summary>Trims and drops punctuation that belongs to the previous sentence (". Then" → "Then").</summary>
        internal static string Clean(string text) => text.Trim().TrimStart(LeadingJunk).TrimStart();

        private sealed class Marker
        {
            public Marker(int index, string kind, DateTime at, bool confirmed, SendTicket? ticket = null)
            {
                Index = index;
                Kind = kind;
                At = at;
                Confirmed = confirmed;
                Ticket = ticket;
            }

            public int Index { get; set; }

            public string Kind { get; }

            public DateTime At { get; }

            public bool Confirmed { get; set; }

            public string? Note { get; set; }

            public SendTicket? Ticket { get; }
        }
    }
}
