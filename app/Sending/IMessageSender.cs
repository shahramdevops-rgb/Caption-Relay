using System;
using System.Threading;
using System.Threading.Tasks;

namespace CaptionRelay.Sending
{
    public enum SendStatus
    {
        /// <summary>Delivered to the chat (ack "sent"): submitted, or only pasted into its input (see <see cref="SendOutcome.Submitted"/>).</summary>
        Sent,

        /// <summary>The chat is still answering; it will be submitted automatically (ack "queued").</summary>
        Queued,

        /// <summary>The extension reported a failure (ack "error").</summary>
        Error,

        /// <summary>No ack arrived in time.</summary>
        NoResponse,

        /// <summary>No extension is connected.</summary>
        NotConnected,
    }

    public sealed class SendOutcome
    {
        public SendOutcome(SendStatus status, string? detail = null, string? provider = null, string? id = null, bool submitted = true, bool alreadyInInput = false, bool focusChat = false, System.Drawing.Rectangle? chatBounds = null)
        {
            Status = status;
            Detail = detail;
            Provider = provider;
            Id = id;
            Submitted = submitted;
            AlreadyInInput = alreadyInInput;
            FocusChat = focusChat;
            ChatBounds = chatBounds;
        }

        public SendStatus Status { get; }

        /// <summary>Id of the send message; a later <see cref="IMessageSender.DeliveryResult"/> for a queued send uses it.</summary>
        public string? Id { get; }

        public string? Detail { get; }

        /// <summary>Which provider handled it (e.g. "gemini"), from the ack.</summary>
        public string? Provider { get; }

        /// <summary>
        /// False when the extension only pasted the text into the chat's input without submitting it (the user sends
        /// it). The text is delivered either way. Missing in the ack = true.
        /// </summary>
        public bool Submitted { get; }

        /// <summary>Paste only: nothing was added because the chat's input already ended with this text.</summary>
        public bool AlreadyInInput { get; }

        /// <summary>
        /// The extension focused the chat after the paste (its tab, window and input) and asks the app to make sure the
        /// window is in front (Windows may not let the browser do it).
        /// </summary>
        public bool FocusChat { get; }

        /// <summary>That browser window's bounds in screen DIPs, when the extension knows them.</summary>
        public System.Drawing.Rectangle? ChatBounds { get; }
    }

    /// <summary>Delivers text to the browser extension (implemented by the WebSocket server in Phase 5).</summary>
    public interface IMessageSender
    {
        /// <summary>
        /// Final result of a send that was acked "queued" (submitted after the chat finished answering, or given up).
        /// Raised on any thread with the send's id.
        /// </summary>
        event Action<string, SendOutcome>? DeliveryResult;

        bool IsConnected { get; }

        /// <param name="submit">Ask the extension to send the text too (merged with what the chat's input holds), even in its paste-only mode.</param>
        Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default, bool submit = false);
    }

    /// <summary>Used until the WebSocket server exists: never connected.</summary>
    public sealed class NotConnectedSender : IMessageSender
    {
        public event Action<string, SendOutcome>? DeliveryResult
        {
            add { }
            remove { }
        }

        public bool IsConnected => false;

        public Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default, bool submit = false) =>
            Task.FromResult(new SendOutcome(SendStatus.NotConnected));
    }
}
