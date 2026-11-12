using System;
using System.Threading;
using System.Threading.Tasks;

namespace CaptionRelay.Sending
{
    public enum SendStatus
    {
        /// <summary>Submitted in the chat (ack "sent").</summary>
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
        public SendOutcome(SendStatus status, string? detail = null, string? provider = null, string? id = null)
        {
            Status = status;
            Detail = detail;
            Provider = provider;
            Id = id;
        }

        public SendStatus Status { get; }

        /// <summary>Id of the send message; a later <see cref="IMessageSender.DeliveryResult"/> for a queued send uses it.</summary>
        public string? Id { get; }

        public string? Detail { get; }

        /// <summary>Which provider handled it (e.g. "gemini"), from the ack.</summary>
        public string? Provider { get; }
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

        Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default);
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

        public Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendOutcome(SendStatus.NotConnected));
    }
}
