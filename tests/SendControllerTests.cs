using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CaptionRelay.Captions;
using CaptionRelay.Sending;
using Xunit;

namespace CaptionRelay.Tests
{
    public class SendControllerTests
    {
        private sealed class FakeSource : ICaptionSource
        {
            public string? Text { get; set; } = "";

            public string? Read() => Text;
        }

        private sealed class FakeSender : IMessageSender
        {
            public event Action<string, SendOutcome>? DeliveryResult;

            public bool IsConnected { get; set; } = true;

            public void RaiseResult(string id, SendOutcome outcome) => DeliveryResult?.Invoke(id, outcome);

            public SendOutcome Next { get; set; } = new SendOutcome(SendStatus.Sent, provider: "gemini");

            public List<string> Sent { get; } = new List<string>();

            public Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default)
            {
                Sent.Add(text);
                return Task.FromResult(Next);
            }
        }

        private sealed class Rig
        {
            public Rig(string prefix = "", TimeSpan? queueTimeout = null)
            {
                Capture = new CaptionCapture(Source, null);
                Capture.PollOnce(); // empty screen at startup
                Controller = new SendController(Capture, Sender, 3, prefix, () => Tick, queueTimeout: queueTimeout);
                Controller.Feedback += (kind, message) => Feedback.Add($"{kind}:{message}");
            }

            public FakeSource Source { get; } = new FakeSource();

            public FakeSender Sender { get; } = new FakeSender();

            public CaptionCapture Capture { get; }

            public SendController Controller { get; }

            public List<string> Feedback { get; } = new List<string>();

            public long Tick { get; set; } = 10_000;

            public void Screen(string text)
            {
                Source.Text = text;
                Capture.PollOnce();
            }

            public Task Send(long advanceMs = 1000)
            {
                Tick += advanceMs;
                return Controller.SendAsync();
            }
        }

        [Fact]
        public async Task Send_DeliversPendingWithPrefix_AndConfirms()
        {
            var rig = new Rig("Q: ");
            rig.Screen("What is a delegate in C sharp");
            await rig.Send();
            Assert.Equal(new[] { "Q: What is a delegate in C sharp" }, rig.Sender.Sent);
            Assert.Equal("Success:Sent ✓", rig.Feedback[^1]);

            await rig.Send();
            Assert.Equal("Info:Nothing new", rig.Feedback[^1]);
            Assert.Single(rig.Sender.Sent);
        }

        [Fact]
        public async Task NotConnected_TextKept_SentLaterWithNewWords()
        {
            var rig = new Rig();
            rig.Sender.IsConnected = false;
            rig.Screen("What is a delegate");
            await rig.Send();
            Assert.Equal("Error:Gemini not connected", rig.Feedback[^1]);
            Assert.Empty(rig.Sender.Sent);

            rig.Sender.IsConnected = true;
            rig.Screen("What is a delegate and an event");
            await rig.Send();
            Assert.Equal(new[] { "What is a delegate and an event" }, rig.Sender.Sent);
        }

        [Fact]
        public async Task SecondSendWithin300ms_IsIgnored()
        {
            var rig = new Rig();
            rig.Screen("first question here");
            await rig.Send();
            rig.Screen("first question here and a second one");
            await rig.Send(advanceMs: 150);
            Assert.Single(rig.Sender.Sent);
            await rig.Send(advanceMs: 200);
            Assert.Equal(2, rig.Sender.Sent.Count);
        }

        [Theory]
        [InlineData(SendStatus.NoResponse, "Error:No response")]
        [InlineData(SendStatus.Error, "Error:Error: send button not found")]
        [InlineData(SendStatus.NotConnected, "Error:Gemini not connected")]
        public async Task FailedDelivery_TextKeptForNextSend(SendStatus status, string feedback)
        {
            var rig = new Rig();
            rig.Sender.Next = new SendOutcome(status, status == SendStatus.Error ? "send button not found" : null);
            rig.Screen("tell me about your project");
            await rig.Send();
            Assert.Equal(feedback, rig.Feedback[^1]);

            rig.Sender.Next = new SendOutcome(SendStatus.Sent);
            await rig.Send();
            Assert.Equal("tell me about your project", rig.Sender.Sent[^1]);
        }

        [Fact]
        public async Task Queued_WithoutId_IsConfirmed_NotResent()
        {
            var rig = new Rig();
            rig.Sender.Next = new SendOutcome(SendStatus.Queued);
            rig.Screen("tell me about your project");
            await rig.Send();
            Assert.Equal("Info:Queued", rig.Feedback[^1]);
            await rig.Send();
            Assert.Equal("Info:Nothing new", rig.Feedback[^1]);
        }

        [Fact]
        public async Task Queued_ThenResultSent_Confirmed()
        {
            var rig = new Rig();
            rig.Sender.Next = new SendOutcome(SendStatus.Queued, id: "q1");
            rig.Screen("tell me about your project");
            await rig.Send();
            Assert.Equal("Info:Queued", rig.Feedback[^1]);
            Assert.Equal(1, rig.Controller.QueuedCount);

            rig.Sender.RaiseResult("q1", new SendOutcome(SendStatus.Sent, "execCommand", "gemini", "q1"));
            Assert.Equal(0, rig.Controller.QueuedCount);
            Assert.Equal("Success:Sent ✓", rig.Feedback[^1]);
            await rig.Send();
            Assert.Equal("Info:Nothing new", rig.Feedback[^1]);
        }

        [Fact]
        public async Task Queued_ThenResultError_TextBackAsPending()
        {
            var rig = new Rig();
            rig.Sender.Next = new SendOutcome(SendStatus.Queued, id: "q2");
            rig.Screen("tell me about your project");
            await rig.Send();
            rig.Sender.RaiseResult("q2", new SendOutcome(SendStatus.Error, "still answering after 3 min", "gemini", "q2"));
            Assert.Equal("Error:Not sent: still answering after 3 min", rig.Feedback[^1]);

            rig.Sender.Next = new SendOutcome(SendStatus.Sent);
            rig.Screen("tell me about your project and the team");
            await rig.Send();
            Assert.Equal("tell me about your project and the team", rig.Sender.Sent[^1]);
        }

        [Fact]
        public async Task Queued_NoResultInTime_TextBackAsPending()
        {
            var rig = new Rig(queueTimeout: TimeSpan.FromMilliseconds(100));
            rig.Sender.Next = new SendOutcome(SendStatus.Queued, id: "q3");
            rig.Screen("tell me about your project");
            await rig.Send();
            await Task.Delay(400);
            Assert.Equal(0, rig.Controller.QueuedCount);
            Assert.Equal("Error:Queued text was not sent", rig.Feedback[^1]);

            rig.Sender.Next = new SendOutcome(SendStatus.Sent);
            await rig.Send();
            Assert.Equal("tell me about your project", rig.Sender.Sent[^1]);
        }

        [Fact]
        public async Task ResultForUnknownId_Ignored()
        {
            var rig = new Rig();
            rig.Screen("some words here");
            rig.Sender.RaiseResult("nope", new SendOutcome(SendStatus.Sent));
            Assert.Empty(rig.Feedback);
            await rig.Send();
            Assert.Equal("Success:Sent ✓", rig.Feedback[^1]);
        }

        [Fact]
        public async Task CaptionsUnavailable_Reported()
        {
            var rig = new Rig();
            rig.Screen("some words here");
            rig.Source.Text = null;
            rig.Capture.PollOnce();
            await rig.Send();
            Assert.Equal("Error:Live Captions not available", rig.Feedback[^1]);
        }

        [Fact]
        public void Skip_Reported()
        {
            var rig = new Rig();
            rig.Screen("small talk");
            rig.Controller.OnSkipPressed();
            Assert.Equal("Info:Skipped", rig.Feedback[^1]);
        }
    }
}
