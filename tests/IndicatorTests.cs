using System;
using System.Drawing;
using System.Threading;
using CaptionRelay.Sending;
using CaptionRelay.UI;
using Xunit;

namespace CaptionRelay.Tests
{
    public class IndicatorTests
    {
        private static readonly Rectangle Primary = new Rectangle(0, 0, 1920, 1040);

        [Theory]
        [InlineData("gemini", null, "Gemini")]
        [InlineData("chatgpt", null, "ChatGPT")]
        [InlineData("CLAUDE", null, "Claude")]
        [InlineData("openai", null, "OpenAI")]
        [InlineData("mistral", null, "Mistral")]
        [InlineData(null, "chatgpt", "ChatGPT")]
        [InlineData(null, null, "Gemini")]
        [InlineData("  ", null, "Gemini")]
        public void ProviderLabel(string? provider, string? last, string expected)
        {
            Assert.Equal(expected, IndicatorText.ProviderLabel(provider, last));
        }

        [Fact]
        public void Words_SingularAndPlural()
        {
            Assert.Equal("1 word", IndicatorText.Words(1));
            Assert.Equal("142 words", IndicatorText.Words(142));
        }

        [Fact]
        public void StatusLine_NoWordCount_FlashWhenThere()
        {
            Assert.Equal("Captions on | Gemini connected", IndicatorText.StatusLine(true, true, "Gemini", null));
            Assert.Equal("Captions off | Gemini not connected | Sent ✓", IndicatorText.StatusLine(false, false, "Gemini", "Sent ✓"));
        }

        [Fact]
        public void StatusLine_ShowsQueuedUntilDelivered()
        {
            Assert.Equal("Captions on | Gemini connected | queued", IndicatorText.StatusLine(true, true, "Gemini", null, 1));
        }

        [Fact]
        public void Tooltip_NeverOver127Chars()
        {
            string t = IndicatorText.Tooltip(true, false, "Gemini", new string('x', 200));
            Assert.True(t.Length <= 127);
        }

        [Fact]
        public void Place_DefaultBottom_300pxFromTheLeft()
        {
            Point p = IndicatorText.Place(null, new Size(300, 26), new[] { Primary }, Primary, 16);
            Assert.Equal(new Point(300, 1040 - 26 - 16), p);
        }

        [Fact]
        public void Place_SavedPositionKeptWhenOnScreen()
        {
            Assert.Equal(new Point(100, 200), IndicatorText.Place(new Point(100, 200), new Size(300, 26), new[] { Primary }, Primary, 16));
        }

        [Fact]
        public void Place_SavedPositionOnRemovedMonitor_FallsBackToDefault()
        {
            Point p = IndicatorText.Place(new Point(2500, 200), new Size(300, 26), new[] { Primary }, Primary, 16);
            Assert.Equal(new Point(300, 998), p);
        }

        [Fact]
        public void Place_SecondMonitorAccepted()
        {
            var second = new Rectangle(1920, 0, 1920, 1040);
            Assert.Equal(new Point(2500, 200), IndicatorText.Place(new Point(2500, 200), new Size(300, 26), new[] { Primary, second }, Primary, 16));
        }

        [Fact]
        public void Form_ExpandsUpward_FlashShowsInTitle()
        {
            RunSta(() =>
            {
                using var form = new IndicatorForm(new Point(400, 600));
                form.SetStatus(true, false, "Gemini", 5, "five words of pending text");
                Assert.Equal("Captions on | Gemini not connected", form.Text);

                Rectangle collapsed = form.Bounds;
                form.ToggleExpanded();
                Assert.True(form.Expanded);
                Assert.True(form.Height > collapsed.Height);
                Assert.Equal(collapsed.Bottom, form.Bounds.Bottom); // grows upward, pill stays put
                Assert.Equal(new Point(400, 600), form.PillLocation);
                form.ToggleExpanded();
                Assert.Equal(collapsed, form.Bounds);

                form.Flash(FeedbackKind.Error, "Gemini not connected");
                Assert.EndsWith("| Gemini not connected", form.Text);
            });
        }

        private static void RunSta(Action action)
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null)
            {
                throw new Exception("STA test failed", error);
            }
        }
    }
}
