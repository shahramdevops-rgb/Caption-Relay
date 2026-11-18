using System;
using System.Collections.Generic;
using System.Linq;
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

            public List<bool> Submits { get; } = new List<bool>();

            public Task<SendOutcome> SendAsync(string text, CancellationToken cancellationToken = default, bool submit = false)
            {
                Sent.Add(text);
                Submits.Add(submit);
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

        private static string Words(int from, int to) => string.Join(" ", System.Linq.Enumerable.Range(from, to - from + 1).Select(i => "w" + i));

        [Fact]
        public async Task Taps_SelectMoreWordsEach_DeliveredWhenTheTapsStop()
        {
            var rig = new Rig();
            var timer = new List<TaskCompletionSource>();
            rig.Controller.TapWords = 50;
            rig.Controller.Delay = _ =>
            {
                var t = new TaskCompletionSource();
                timer.Add(t);
                return t.Task;
            };
            int changes = 0;
            rig.Controller.TapChanged += () => changes++;
            rig.Screen(Words(1, 200));

            // Each press starts a hold timer (timer[even]); each short release starts the paste timer (timer[odd]).
            for (int i = 0; i < 3; i++)
            {
                rig.Controller.OnTapPressed();
                rig.Controller.OnTapReleased(60);
            }

            Assert.Equal(3, rig.Controller.Taps);
            Assert.Equal(150, rig.Controller.TapWordLimit);
            Assert.Empty(rig.Sender.Sent);
            Assert.Equal("Info:Tap \u00d73 \u00b7 150 words", rig.Feedback[^1]);

            timer[1].SetResult(); // an older paste timer: a newer tap restarted the wait
            timer[4].SetResult(); // a hold timer after the key was released: nothing
            await Task.Yield();
            Assert.Empty(rig.Sender.Sent);
            Assert.False(rig.Controller.SendOnRelease);

            timer[5].SetResult();
            await WaitFor(() => rig.Sender.Sent.Count == 1);
            Assert.Equal(Words(51, 200), rig.Sender.Sent[0]);
            Assert.False(rig.Sender.Submits[0]);
            Assert.Equal(0, rig.Controller.Taps);
            Assert.Equal(4, changes); // 3 taps + delivered

            // 5 new words, one tap: only the new words, nothing repeated.
            rig.Screen(Words(1, 205));
            rig.Controller.OnTapPressed();
            await rig.Controller.CommitTapsAsync();
            Assert.Equal(Words(201, 205), rig.Sender.Sent[1]);
        }

        [Fact]
        public async Task TapKeyHeld_PastesAndSends_OnRelease_NothingUsedUp()
        {
            var rig = new Rig();
            var timer = new List<TaskCompletionSource>();
            rig.Controller.TapWords = 15;
            rig.Controller.Delay = _ =>
            {
                var t = new TaskCompletionSource();
                timer.Add(t);
                return t.Task;
            };
            rig.Screen(Words(1, 40));

            rig.Controller.OnTapPressed();
            rig.Controller.OnTapReleased(60);
            rig.Controller.OnTapPressed(); // second press, held
            timer[2].SetResult(); // hold timer: still down → armed
            await WaitFor(() => rig.Controller.SendOnRelease);
            Assert.Equal("Info:Release: paste + send", rig.Feedback[^1]);
            Assert.Empty(rig.Sender.Sent); // nothing while the key is held

            rig.Controller.OnTapReleased(650);
            await WaitFor(() => rig.Sender.Sent.Count == 1);
            Assert.Equal(Words(11, 40), rig.Sender.Sent[0]); // 2 presses = 30 words
            Assert.True(rig.Sender.Submits[0]);
            Assert.Equal("Success:Sent \u2713", rig.Feedback[^1]);
            Assert.False(rig.Controller.SendOnRelease);

            // Like a paste, nothing is used up: the next tap takes the same words again.
            rig.Controller.OnTapPressed();
            await rig.Controller.CommitTapsAsync();
            Assert.Equal(Words(26, 40), rig.Sender.Sent[1]);
            Assert.False(rig.Sender.Submits[1]);
        }

        [Fact]
        public async Task TapKeyHeldLong_ReleaseSendsEvenIfTheHoldTimerWasLate()
        {
            var rig = new Rig();
            rig.Controller.Delay = _ => new TaskCompletionSource().Task; // timers never fire
            rig.Screen(Words(1, 10));
            rig.Controller.OnTapPressed();
            rig.Controller.OnTapReleased(rig.Controller.TapHoldSendMs + 1);
            await WaitFor(() => rig.Sender.Sent.Count == 1);
            Assert.True(rig.Sender.Submits[0]);
        }

        [Fact]
        public async Task SendOrSkipKey_CancelsTheTapSelection()
        {
            var rig = new Rig();
            rig.Controller.Delay = _ => new TaskCompletionSource().Task; // never fires
            rig.Screen(Words(1, 80));
            rig.Controller.OnTapPressed();
            rig.Controller.OnSkipPressed();
            Assert.Equal(0, rig.Controller.Taps);
            await rig.Controller.CommitTapsAsync();
            Assert.Empty(rig.Sender.Sent);

            rig.Screen(Words(1, 90));
            rig.Controller.OnTapPressed();
            rig.Controller.OnSendPressed(); // the send key: everything since the skip, as always
            await WaitFor(() => rig.Sender.Sent.Count == 1);
            Assert.Equal(Words(81, 90), rig.Sender.Sent[0]);
            Assert.Equal(0, rig.Controller.Taps);
        }

        private static async Task WaitFor(Func<bool> cond)
        {
            for (int i = 0; i < 200 && !cond(); i++)
            {
                await Task.Delay(10);
            }

            Assert.True(cond());
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
        public async Task PastedOnly_ConfirmedAndShownAsPasted()
        {
            var rig = new Rig();
            rig.Sender.Next = new SendOutcome(SendStatus.Sent, "pasted with execCommand, not submitted", "gemini", submitted: false);
            rig.Screen("what is dependency injection");
            await rig.Send();
            Assert.Equal("Success:Pasted ✓", rig.Feedback[^1]);

            // Pasted only: nothing is used up, pressing again pastes the same words again (plus new ones).
            rig.Screen("what is dependency injection and why");
            await rig.Send();
            Assert.Equal(new[] { "what is dependency injection", "what is dependency injection and why" }, rig.Sender.Sent);

            // The extension found it all in the input already.
            rig.Sender.Next = new SendOutcome(SendStatus.Sent, "already in the input", "gemini", submitted: false, alreadyInInput: true);
            await rig.Send();
            Assert.Equal("Info:Already in the input", rig.Feedback[^1]);
        }

        [Fact]
        public async Task Pasted_WithFocus_AsksToBringTheChatForward_SentDoesNot()
        {
            var rig = new Rig();
            var requests = new List<System.Drawing.Rectangle?>();
            rig.Controller.ChatFocusRequested += b => requests.Add(b);
            rig.Screen("tell me about yourself");

            rig.Sender.Next = new SendOutcome(SendStatus.Sent, null, "gemini", submitted: false, focusChat: true, chatBounds: new System.Drawing.Rectangle(0, 0, 1280, 1400));
            await rig.Send();
            Assert.Equal(new System.Drawing.Rectangle?[] { new System.Drawing.Rectangle(0, 0, 1280, 1400) }, requests);

            rig.Sender.Next = new SendOutcome(SendStatus.Sent, null, "gemini", submitted: false); // focus turned off
            await rig.Send();
            rig.Sender.Next = new SendOutcome(SendStatus.Sent, null, "gemini"); // submitted
            rig.Screen("tell me about yourself please");
            await rig.Send();
            Assert.Single(requests);
        }

        [Fact]
        public async Task Queued_ThenResultPastedOnly_ShownAsPasted()
        {
            var rig = new Rig();
            rig.Sender.Next = new SendOutcome(SendStatus.Queued, id: "p1");
            rig.Screen("opened a new tab for this");
            await rig.Send();
            rig.Sender.RaiseResult("p1", new SendOutcome(SendStatus.Sent, null, "gemini", "p1", submitted: false));
            Assert.Equal(0, rig.Controller.QueuedCount);
            Assert.Equal("Success:Pasted ✓", rig.Feedback[^1]);
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
