using System;
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

        private readonly CaptionCapture _capture;
        private readonly IMessageSender _sender;
        private readonly int _minSendChars;
        private readonly string _prefix;
        private readonly Func<long> _tickMs;
        private readonly int _guardMs;
        private readonly Func<string> _providerName;
        private long _lastSend = long.MinValue / 2;

        public SendController(CaptionCapture capture, IMessageSender sender, int minSendChars, string prefix, Func<long>? tickMs = null, int guardMs = DefaultGuardMs, Func<string>? providerName = null)
        {
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
                        Log.Info($"Send result: sent{via}.");
                        Report(FeedbackKind.Success, "Sent ✓");
                        break;
                    case SendStatus.Queued:
                        _capture.Confirm(ticket, "(queued)");
                        Log.Info($"Send result: queued{via}.");
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
