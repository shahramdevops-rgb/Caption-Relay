using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CaptionRelay.Captions;
using CaptionRelay.Infrastructure;

namespace CaptionRelay.Sending
{
    public enum FeedbackKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>
    /// What the hotkeys do: send = take the pending text and deliver it (text is kept when nothing is connected or
    /// delivery fails), skip = move sentIndex to the end, tap = select the latest <see cref="TapWords"/> words per tap
    /// and deliver the selection <see cref="TapPasteDelayMs"/> after the last tap. A second send within
    /// <c>guardMs</c> is ignored. Call from the UI thread. Never logs transcript text, only lengths.
    /// </summary>
    public sealed class SendController
    {
        public const int DefaultGuardMs = 300;

        /// <summary>Session-file note for text that was only pasted into the chat's input, not submitted.</summary>
        public const string PastedNote = "(pasted, not submitted)";

        /// <summary>The extension gives up after 3 min; a little longer here in case its result never arrives.</summary>
        public static readonly TimeSpan DefaultQueueTimeout = TimeSpan.FromSeconds(200);

        private readonly Dictionary<string, SendTicket> _queued = new Dictionary<string, SendTicket>();
        private readonly HashSet<string> _keepQueued = new HashSet<string>();
        private readonly Action<Action> _dispatch;
        private readonly TimeSpan _queueTimeout;
        private readonly CaptionCapture _capture;
        private readonly IMessageSender _sender;
        private readonly int _minSendChars;
        private readonly string _prefix;
        private readonly Func<long> _tickMs;
        private readonly int _guardMs;
        private readonly Func<string> _providerName;
        private long _lastSend = long.MinValue / 2;
        private int _taps;
        private int _tapGeneration;
        private bool _tapKeyDown;
        private bool _sendOnRelease;

        /// <param name="dispatch">Runs an action on the UI thread (delivery results arrive on a network thread).</param>
        public SendController(CaptionCapture capture, IMessageSender sender, int minSendChars, string prefix, Func<long>? tickMs = null, int guardMs = DefaultGuardMs, Func<string>? providerName = null, Action<Action>? dispatch = null, TimeSpan? queueTimeout = null)
        {
            _dispatch = dispatch ?? (a => a());
            _queueTimeout = queueTimeout ?? DefaultQueueTimeout;
            sender.DeliveryResult += (id, outcome) => _dispatch(() => OnDeliveryResult(id, outcome));
            _providerName = providerName ?? (() => "Gemini");
            _capture = capture;
            _sender = sender;
            _minSendChars = minSendChars;
            _prefix = prefix ?? "";
            _tickMs = tickMs ?? (() => Environment.TickCount64);
            _guardMs = guardMs;
        }

        /// <summary>Message for the indicator (Phase 6): e.g. "Sent ✓", "Queued", "Gemini not connected".</summary>
        public event Action<FeedbackKind, string>? Feedback;

        /// <summary>Tap selection started, grew, was delivered or was cancelled (see <see cref="Taps"/>).</summary>
        public event Action? TapChanged;

        /// <summary>Sends acked "queued" that still wait for the chat to finish answering.</summary>
        public int QueuedCount => _queued.Count;

        /// <summary>Words selected per tap.</summary>
        public int TapWords { get; set; } = 15;

        /// <summary>The selection is delivered this long after the last tap.</summary>
        public int TapPasteDelayMs { get; set; } = 400;

        /// <summary>Holding the tap key at least this long pastes the selection AND sends it when the key is released.</summary>
        public int TapHoldSendMs { get; set; } = 400;

        /// <summary>The tap key is held long enough: releasing it pastes and sends (for the preview and indicator).</summary>
        public bool SendOnRelease => _sendOnRelease;

        /// <summary>Waits the given milliseconds (replaced in tests).</summary>
        public Func<int, Task> Delay { get; set; } = Task.Delay;

        /// <summary>Taps so far in the current selection; 0 = no tap selection in progress.</summary>
        public int Taps => _taps;

        /// <summary>How many of the latest words the current tap selection takes.</summary>
        public int TapWordLimit => _taps * TapWords;

        public void OnSendPressed()
        {
            CancelTaps("send key");
            _ = SendAsync();
        }

        /// <summary>
        /// Tap key down: select <see cref="TapWords"/> more words. Nothing is delivered while the key is held; see
        /// <see cref="OnTapReleased"/>.
        /// </summary>
        public void OnTapPressed()
        {
            _taps++;
            _tapKeyDown = true;
            _sendOnRelease = false;
            int generation = ++_tapGeneration;
            Log.Info($"Hotkey: tap {_taps} (last {TapWordLimit} words).");
            Report(FeedbackKind.Info, $"Tap \u00d7{_taps} \u00b7 {TapWordLimit} words");
            RaiseTapChanged();
            _ = WatchHoldAsync(generation);
        }

        /// <summary>
        /// Tap key up after <paramref name="heldMs"/>: a long press pastes the selection and sends it right away; a
        /// short tap pastes it <see cref="TapPasteDelayMs"/> later unless another tap comes first.
        /// </summary>
        public void OnTapReleased(int heldMs)
        {
            _tapKeyDown = false;
            if (_taps == 0)
            {
                return; // cancelled while held
            }

            if (_sendOnRelease || heldMs >= TapHoldSendMs)
            {
                Log.Info($"Hotkey: tap key held {heldMs} ms: paste and send.");
                _ = CommitTapsAsync(submit: true);
                return;
            }

            _ = CommitAfterDelayAsync(_tapGeneration);
        }

        /// <summary>
        /// Delivers the current tap selection now. <paramref name="submit"/>: also send it (tap key held), merged with
        /// what the chat's input already holds; like a paste, nothing is used up.
        /// </summary>
        public async Task CommitTapsAsync(bool submit = false)
        {
            if (_taps == 0)
            {
                return;
            }

            int words = TapWordLimit;
            _taps = 0;
            _tapGeneration++;
            _sendOnRelease = false;
            RaiseTapChanged();
            try
            {
                await DeliverAsync(_capture.TakeLast(words, _minSendChars), submit ? $"tap + send (last {words} words)" : $"tap (last {words} words)", submit).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Tap send failed", ex);
                Report(FeedbackKind.Error, "Error: " + ex.Message);
            }
        }

        public void OnSkipPressed()
        {
            CancelTaps("skip key");
            if (_capture.Skip())
            {
                Log.Info("Hotkey: skip.");
                Report(FeedbackKind.Info, "Skipped");
            }
            else
            {
                Log.Info("Hotkey: skip, but Live Captions has not been read yet.");
                Report(FeedbackKind.Error, "Live Captions not available");
            }
        }

        public async Task SendAsync()
        {
            try
            {
                long now = _tickMs();
                if (now - _lastSend < _guardMs)
                {
                    Log.Info($"Hotkey: send ignored (second press within {_guardMs} ms).");
                    return;
                }

                _lastSend = now;
                await DeliverAsync(_capture.Take(_minSendChars), "send").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Send failed", ex);
                Report(FeedbackKind.Error, "Error: " + ex.Message);
            }
        }

        private async Task CommitAfterDelayAsync(int generation)
        {
            try
            {
                await Delay(TapPasteDelayMs).ConfigureAwait(true);
                if (generation == _tapGeneration && _taps > 0)
                {
                    await CommitTapsAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Tap timer failed", ex);
            }
        }

        /// <summary>After <see cref="TapHoldSendMs"/> with the tap key still down: arm "send on release" and say so.</summary>
        private async Task WatchHoldAsync(int generation)
        {
            try
            {
                await Delay(TapHoldSendMs).ConfigureAwait(true);
                if (generation == _tapGeneration && _taps > 0 && _tapKeyDown)
                {
                    _sendOnRelease = true;
                    Report(FeedbackKind.Info, "Release: paste + send");
                    RaiseTapChanged();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Tap hold timer failed", ex);
            }
        }

        private void CancelTaps(string reason)
        {
            if (_taps == 0)
            {
                return;
            }

            Log.Info($"Tap selection cancelled ({reason}).");
            _taps = 0;
            _tapGeneration++;
            _sendOnRelease = false;
            RaiseTapChanged();
        }

        /// <summary>
        /// Delivers taken text and handles the outcome: confirm, wait for a queued result, or keep the text.
        /// <paramref name="submit"/>: ask the extension to send it too (it is still not used up, like a paste).
        /// </summary>
        private async Task DeliverAsync(TakeResult taken, string what, bool submit = false)
        {
            if (taken.Status == TakeStatus.CaptionsUnavailable)
            {
                Log.Info($"Hotkey: {what}, but Live Captions is not available (text kept).");
                Report(FeedbackKind.Error, "Live Captions not available");
                return;
            }

            if (taken.Status == TakeStatus.NothingNew)
            {
                Log.Info($"Hotkey: {what}, nothing new.");
                Report(FeedbackKind.Info, "Nothing new");
                return;
            }

            SendTicket ticket = taken.Ticket!;
            if (!_sender.IsConnected)
            {
                _capture.Restore(ticket);
                Log.Info($"Hotkey: {what}, {taken.Text.Length} chars, but the extension is not connected (text kept).");
                Report(FeedbackKind.Error, _providerName() + " not connected");
                return;
            }

            Log.Info($"Hotkey: {what}, {taken.Text.Length} chars.");
            SendOutcome outcome = await _sender.SendAsync(_prefix + taken.Text, default, submit).ConfigureAwait(true);
            string via = outcome.Provider != null ? $" via {outcome.Provider}" : "";
            switch (outcome.Status)
            {
                case SendStatus.Sent:
                    Delivered(ticket, outcome, null, keep: submit);
                    break;
                case SendStatus.Queued:
                    Log.Info($"Send result: queued{via}{(string.IsNullOrEmpty(outcome.Detail) ? "" : " (" + outcome.Detail + ")")}.");
                    if (outcome.Id != null)
                    {
                        // Final result comes later (DeliveryResult); until then the text is neither confirmed nor lost.
                        _queued[outcome.Id] = ticket;
                        if (submit)
                        {
                            _keepQueued.Add(outcome.Id);
                        }

                        _ = ExpireAsync(outcome.Id);
                    }
                    else
                    {
                        _capture.Confirm(ticket, "(queued)");
                    }

                    Report(FeedbackKind.Info, "Queued");
                    break;
                case SendStatus.NoResponse:
                    _capture.Restore(ticket);
                    Log.Warn("Send result: no response from the extension (text kept).");
                    Report(FeedbackKind.Error, "No response");
                    break;
                case SendStatus.NotConnected:
                    _capture.Restore(ticket);
                    Log.Warn("Send result: extension disconnected (text kept).");
                    Report(FeedbackKind.Error, _providerName() + " not connected");
                    break;
                default:
                    _capture.Restore(ticket);
                    Log.Warn($"Send result: error{via}: {outcome.Detail} (text kept).");
                    Report(FeedbackKind.Error, string.IsNullOrEmpty(outcome.Detail) ? "Error" : "Error: " + outcome.Detail);
                    break;
            }
        }

        /// <summary>
        /// Submitted: the text is used up (the next press starts after it), except for a held tap key
        /// (<paramref name="keep"/>), which works like a paste plus Enter. Only pasted: nothing is used up, so the
        /// next press pastes the same words again (the extension merges them with what the input already holds).
        /// </summary>
        private void Delivered(SendTicket ticket, SendOutcome outcome, string? submittedNote, bool keep = false)
        {
            string via = outcome.Provider != null ? $" via {outcome.Provider}" : "";
            string detail = string.IsNullOrEmpty(outcome.Detail) ? "" : " (" + outcome.Detail + ")";
            if (outcome.Submitted && keep)
            {
                _capture.ConfirmPasted(ticket, "(pasted and sent)");
                Log.Info($"Send result: pasted and sent{via}{detail}.");
                Report(FeedbackKind.Success, "Sent ✓");
            }
            else if (outcome.Submitted)
            {
                _capture.Confirm(ticket, submittedNote);
                Log.Info($"Send result: sent{via}{detail}.");
                Report(FeedbackKind.Success, "Sent ✓");
            }
            else if (outcome.AlreadyInInput)
            {
                _capture.ConfirmPasted(ticket, "(already in the input)");
                Log.Info($"Send result: nothing added, already in the input{via}.");
                Report(FeedbackKind.Info, "Already in the input");
            }
            else
            {
                _capture.ConfirmPasted(ticket, PastedNote);
                Log.Info($"Send result: pasted{via}{detail}.");
                Report(FeedbackKind.Success, "Pasted ✓");
            }
        }

        private void RaiseTapChanged()
        {
            try
            {
                TapChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("TapChanged handler failed", ex);
            }
        }

        private void OnDeliveryResult(string id, SendOutcome outcome)
        {
            if (!_queued.Remove(id, out SendTicket? ticket))
            {
                Log.Info("Delivery result for a send that is no longer waiting; ignored.");
                return;
            }

            string via = outcome.Provider != null ? $" via {outcome.Provider}" : "";
            bool keep = _keepQueued.Remove(id);
            if (outcome.Status == SendStatus.Sent)
            {
                Delivered(ticket, outcome, "(queued)", keep);
            }
            else
            {
                _capture.Restore(ticket);
                Log.Warn($"Queued send failed{via}: {outcome.Detail} (text kept).");
                Report(FeedbackKind.Error, "Not sent: " + (outcome.Detail ?? "error"));
            }
        }

        private async Task ExpireAsync(string id)
        {
            await Task.Delay(_queueTimeout).ConfigureAwait(true);
            _keepQueued.Remove(id);
            if (_queued.Remove(id, out SendTicket? ticket))
            {
                _capture.Restore(ticket);
                Log.Warn($"Queued send got no result within {_queueTimeout.TotalSeconds:0} s (text kept).");
                Report(FeedbackKind.Error, "Queued text was not sent");
            }
        }

        private void Report(FeedbackKind kind, string message)
        {
            try
            {
                Feedback?.Invoke(kind, message);
            }
            catch (Exception ex)
            {
                Log.Error("Feedback handler failed", ex);
            }
        }
    }
}
