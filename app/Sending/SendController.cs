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
    /// delivery fails), skip = move sentIndex to the end. A second send within <c>guardMs</c> is ignored.
    /// Call from the UI thread. Never logs transcript text, only lengths.
    /// </summary>
    public sealed class SendController
    {
        public const int DefaultGuardMs = 300;

        /// <summary>The extension gives up after 3 min; a little longer here in case its result never arrives.</summary>
        public static readonly TimeSpan DefaultQueueTimeout = TimeSpan.FromSeconds(200);

        private readonly Dictionary<string, SendTicket> _queued = new Dictionary<string, SendTicket>();
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

        /// <summary>Sends acked "queued" that still wait for the chat to finish answering.</summary>
        public int QueuedCount => _queued.Count;

        public void OnSendPressed() => _ = SendAsync();

        public void OnSkipPressed()
        {
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
                TakeResult taken = _capture.Take(_minSendChars);
                if (taken.Status == TakeStatus.CaptionsUnavailable)
                {
                    Log.Info("Hotkey: send, but Live Captions is not available (text kept).");
                    Report(FeedbackKind.Error, "Live Captions not available");
                    return;
                }

                if (taken.Status == TakeStatus.NothingNew)
                {
                    Log.Info("Hotkey: send, nothing new.");
                    Report(FeedbackKind.Info, "Nothing new");
                    return;
                }

                SendTicket ticket = taken.Ticket!;
                if (!_sender.IsConnected)
                {
                    _capture.Restore(ticket);
                    Log.Info($"Hotkey: send, {taken.Text.Length} chars pending, but Gemini is not connected (text kept).");
                    Report(FeedbackKind.Error, _providerName() + " not connected");
                    return;
                }

                Log.Info($"Hotkey: send, {taken.Text.Length} chars.");
                SendOutcome outcome = await _sender.SendAsync(_prefix + taken.Text).ConfigureAwait(true);
                string via = outcome.Provider != null ? $" via {outcome.Provider}" : "";
                switch (outcome.Status)
                {
                    case SendStatus.Sent:
                        _capture.Confirm(ticket);
                        Log.Info($"Send result: sent{via}{(string.IsNullOrEmpty(outcome.Detail) ? "" : " (" + outcome.Detail + ")")}.");
                        Report(FeedbackKind.Success, "Sent ✓");
                        break;
                    case SendStatus.Queued:
                        Log.Info($"Send result: queued{via}{(string.IsNullOrEmpty(outcome.Detail) ? "" : " (" + outcome.Detail + ")")}.");
                        if (outcome.Id != null)
                        {
                            // Final result comes later (DeliveryResult); until then the text is neither confirmed nor lost.
                            _queued[outcome.Id] = ticket;
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
            catch (Exception ex)
            {
                Log.Error("Send failed", ex);
                Report(FeedbackKind.Error, "Error: " + ex.Message);
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
            if (outcome.Status == SendStatus.Sent)
            {
                _capture.Confirm(ticket, "(queued)");
                Log.Info($"Queued send delivered{via}{(string.IsNullOrEmpty(outcome.Detail) ? "" : " (" + outcome.Detail + ")")}.");
                Report(FeedbackKind.Success, "Sent ✓");
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
