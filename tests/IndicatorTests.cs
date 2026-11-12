using System;
using System.Drawing;
using System.Text;
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
            Assert.Equal("1 word pending", IndicatorText.Words(1));
            Assert.Equal("142 words pending", IndicatorText.Words(142));
        }

        [Fact]
        public void StatusLine_ShowsFlashInsteadOfWords()
        {
            Assert.Equal("Captions on | Gemini connected | 3 words pending", IndicatorText.StatusLine(true, true, "Gemini", 3, null));
            Assert.Equal("Captions off | Gemini not connected | Sent ✓", IndicatorText.StatusLine(false, false, "Gemini", 3, "Sent ✓"));
        }

        [Fact]
        public void StatusLine_ShowsQueuedUntilDelivered()
        {
            Assert.Equal("Captions on | Gemini connected | queued · 2 words pending", IndicatorText.StatusLine(true, true, "Gemini", 2, null, 1));
        }

        [Fact]
        public void Tooltip_NeverOver127Chars()
        {
            string t = IndicatorText.Tooltip(true, false, "Gemini", 12345, new string('x', 200));
            Assert.True(t.Length <= 127);
        }

        [Fact]
        public void Place_DefaultBottomRight()
        {
            Point p = IndicatorText.Place(null, new Size(300, 26), new[] { Primary }, Primary, 16);
            Assert.Equal(new Point(1920 - 300 - 16, 1040 - 26 - 16), p);
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
            Assert.Equal(new Point(1604, 998), p);
        }

        [Fact]
        public void Place_SecondMonitorAccepted()
        {
            var second = new Rectangle(1920, 0, 1920, 1040);
            Assert.Equal(new Point(2500, 200), IndicatorText.Place(new Point(2500, 200), new Size(300, 26), new[] { Primary, second }, Primary, 16));
        }

        [Fact]
        public void Wave_IsValidPcm()
        {
            byte[] wav = Sounds.Wave((880, 70), (1320, 90));
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            int samples = 22050 * 70 / 1000 + 22050 * 90 / 1000;
            Assert.Equal(44 + samples * 2, wav.Length);
        }

        [Fact]
        public void Form_ExpandsUpward_FlashShowsInTitle()
        {
            RunSta(() =>
            {
                using var form = new IndicatorForm(new Point(400, 600));
                form.SetStatus(true, false, "Gemini", 5, "five words of pending text");
                Assert.Equal("Captions on | Gemini not connected | 5 words pending", form.Text);

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
