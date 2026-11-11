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
        public SendOutcome(SendStatus status, string? detail = null, string? provider = null)
        {
            Status = status;
            Detail = detail;
            Provider = provider;
        }

        public SendStatus Status { get; }

        public string? Detail { get; }

        /// <summary>Which provider handled it (e.g. "gemini"), from the ack.</summary>
        public string? Provider { get; }
    }

    /// <summary>Delivers text to the browser extension (implemented by the WebSocket server in Phase 5).</summary>
    public interface IMessageSender
    {
        bool IsConnected { get; }

        Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default);
    }

    /// <summary>Used until the WebSocket server exists: never connected.</summary>
    public sealed class NotConnectedSender : IMessageSender
    {
        public bool IsConnected => false;

        public Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendOutcome(SendStatus.NotConnected));
    }
}
