using System;
using System.IO;
using System.Text;
using CaptionRelay.Captions;
using Xunit;

namespace CaptionRelay.Tests
{
    public class CaptionCaptureTests
    {
        private sealed class FakeSource : ICaptionSource
        {
            public string? Text { get; set; } = "";

            public string? Read() => Text;
        }

        private sealed class Rig
        {
            public Rig(string? firstScreen = "", bool failWrites = false)
            {
                Source.Text = firstScreen;
                FailWrites = failWrites;
                Writer = new SessionWriter(
                    text =>
                    {
                        if (FailWrites)
                        {
                            WriteAttempts++;
                            throw new IOException("locked");
                        }

                        File.Append(text);
                    },
                    () => Now);
                Capture = new CaptionCapture(Source, Writer, new CaptureOptions { HoldBackWords = 4 }, () => Now, () =>
                {
                    Restarts++;
                    return true;
                });
                Capture.PollOnce();
            }

            public FakeSource Source { get; } = new FakeSource();

            public SessionWriter Writer { get; }

            public CaptionCapture Capture { get; }

            public StringBuilder File { get; } = new StringBuilder();

            public DateTime Now { get; set; } = new DateTime(2026, 11, 11, 20, 0, 0);

            public int Restarts { get; private set; }

            public bool FailWrites { get; set; }

            public int WriteAttempts { get; private set; }

            public string Out => File.ToString().Replace("\r\n", "\n");

            public void Screen(string? text, double advanceSeconds = 0.12)
            {
                Now = Now.AddSeconds(advanceSeconds);
                Source.Text = text;
                Capture.PollOnce();
            }

            public void Idle(double seconds = 6)
            {
                Now = Now.AddSeconds(seconds);
                Capture.Tick();
            }
        }

        private static string Words(int from, int to)
        {
            var sb = new StringBuilder();
            for (int i = from; i <= to; i++)
            {
                sb.Append(sb.Length == 0 ? "" : " ").Append('w').Append(i);
            }

            return sb.ToString();
        }

        [Fact]
        public void TakeLast_TakesOnlyTheLastWords_OlderPendingIsSkipped()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 120));
            TakeResult r = rig.Capture.TakeLast(50, 3);
            Assert.Equal(TakeStatus.Ok, r.Status);
            Assert.Equal(Words(71, 120), r.Text);
            rig.Capture.Confirm(r.Ticket!, "(pasted, not submitted)");
            Assert.Equal(TakeStatus.NothingNew, rig.Capture.TakeLast(50, 3).Status);
            rig.Idle();
            Assert.Contains("[SKIPPED", rig.Out);
            Assert.Contains("(older than the last 50 words)", rig.Out);
            Assert.Contains("(pasted, not submitted)", rig.Out);
        }

        [Fact]
        public void TakeLast_MoreWordsThanPending_TakesAllPending()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 30));
            Assert.Equal(Words(1, 30), rig.Capture.TakeLast(150, 3).Text);
        }

        [Fact]
        public void Submitted_IsUsedUp_NextTapOnlyTheNewWords()
        {
            // "Press Enter for me": 3 taps (45 words) submitted; 5 more words, one tap (15) → only the 5 new words.
            var rig = new Rig();
            rig.Screen(Words(1, 200));
            TakeResult first = rig.Capture.TakeLast(45, 3);
            Assert.Equal(Words(156, 200), first.Text);
            rig.Capture.Confirm(first.Ticket!);

            rig.Screen(Words(1, 205));
            Assert.Equal(Words(201, 205), rig.Capture.TakeLast(15, 3).Text);
        }

        [Fact]
        public void Pasted_IsNotUsedUp_PressingAgainPastesTheSameWordsAgain()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 40));
            TakeResult tap = rig.Capture.TakeLast(15, 3);
            Assert.Equal(Words(26, 40), tap.Text);
            rig.Capture.ConfirmPasted(tap.Ticket!, "(pasted, not submitted)");
            TakeResult again = rig.Capture.TakeLast(15, 3);
            Assert.Equal(Words(26, 40), again.Text); // same words again
            rig.Capture.Restore(again.Ticket!);

            TakeResult all = rig.Capture.Take(3);
            Assert.Equal(Words(1, 40), all.Text);
            rig.Capture.ConfirmPasted(all.Ticket!, "(pasted, not submitted)");
            rig.Screen(Words(1, 42));
            Assert.Equal(Words(1, 42), rig.Capture.Take(3).Text); // everything since the start, plus the new words

            rig.Idle();
            Assert.Contains("(pasted, not submitted)", rig.Out);
            Assert.DoesNotContain("older than", rig.Out); // a pasted tap skips nothing
        }

        [Fact]
        public void Pasted_SkipInBetween_StaysSkipped()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 20));
            TakeResult r = rig.Capture.Take(3);
            rig.Screen(Words(1, 25));
            rig.Capture.Skip();
            rig.Capture.ConfirmPasted(r.Ticket!);
            Assert.Equal(TakeStatus.NothingNew, rig.Capture.Take(3).Status);
        }

        [Fact]
        public void TakeLast_Restore_PutsEverythingBack()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 80));
            TakeResult r = rig.Capture.TakeLast(50, 3);
            rig.Capture.Restore(r.Ticket!);
            Assert.Equal(Words(1, 80), rig.Capture.Take(3).Text);
        }

        [Fact]
        public void PreviewLast_ShowsSelectionAndContext_ChangesNothing()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 70));
            TapPreview p = rig.Capture.PreviewLast(50);
            Assert.True(p.CaptionsAvailable);
            Assert.Equal(Words(21, 70), p.Selected);
            Assert.Equal(50, p.SelectedWords);
            Assert.Equal(70, p.PendingWords);
            Assert.EndsWith("w20", p.Context);
            Assert.Equal(Words(1, 70), rig.Capture.Take(3).Text); // nothing was taken by the preview
        }

        [Fact]
        public void PreviewLast_WithTheLiveCaptionsView_ShowsThoseWords_SelectionAmongThem()
        {
            var rig = new Rig();
            rig.Screen(Words(1, 70));
            string onScreen = "W41, " + Words(42, 70) + "."; // the window shows the last 30 words (case/punctuation differ)

            TapPreview one = rig.Capture.PreviewLast(15, onScreen);
            Assert.True(one.MatchesLiveCaptions);
            Assert.Equal(Words(41, 55), one.Context); // the rest of what the window shows, dimmed
            Assert.Equal(Words(56, 70), one.Selected);
            Assert.Equal(0, one.AboveWindow);

            TapPreview three = rig.Capture.PreviewLast(45, onScreen);
            Assert.Equal("", three.Context);
            Assert.Equal(Words(26, 70), three.Selected);
            Assert.Equal(15, three.AboveWindow); // w26..w40 are above the window

            TapPreview unknown = rig.Capture.PreviewLast(15, "something else entirely shown here");
            Assert.False(unknown.MatchesLiveCaptions);
            Assert.EndsWith("w55", unknown.Context);
        }

        [Fact]
        public void FindVisibleStart_LatestOccurrence_TopLineMayHaveChanged()
        {
            string full = "so what is your name. so what is your name and where do you live now";
            Assert.Equal(full.LastIndexOf("so what", StringComparison.Ordinal), CaptionCapture.FindVisibleStart(full, "So what is your name and where"));
            Assert.Equal(full.IndexOf("where", StringComparison.Ordinal), CaptionCapture.FindVisibleStart(full, "Wear do you live now")); // first word rewritten
            Assert.Equal(-1, CaptionCapture.FindVisibleStart(full, "nothing like it"));
        }

        [Fact]
        public void TextOnScreenAtStartup_IsNotSent_ButWrittenAsSkipped()
        {
            var rig = new Rig("Old text from an earlier video.");
            Assert.Equal(TakeStatus.NothingNew, rig.Capture.Take(3).Status);

            rig.Screen("Old text from an earlier video. So tell me about your last project");
            TakeResult r = rig.Capture.Take(3);
            Assert.Equal("So tell me about your last project", r.Text);

            rig.Capture.Confirm(r.Ticket!);
            rig.Idle();
            Assert.Equal(
                "===== CaptionRelay session 2026-11-11 20:00 =====\n" +
                "[20:00:06] Old text from an earlier video.\n" + // paragraph time = when it was written
                "[SKIPPED 20:00:00] (text on screen at startup)\n" +
                "[20:00:06] So tell me about your last project\n" +
                "[SENT 20:00:00]",
                rig.Out);
        }

        [Fact]
        public void EmptyScreenAtStartup_FirstSpeechIsSent()
        {
            var rig = new Rig("");
            rig.Screen("Hello, can you hear me?");
            Assert.Equal("Hello, can you hear me?", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void SecondSend_OnlyNewWords_NothingNewSendsNothing()
        {
            var rig = new Rig("");
            rig.Screen("So tell me about your last project");
            Assert.Equal("So tell me about your last project", rig.Capture.Take(3).Text);
            Assert.Equal(TakeStatus.NothingNew, rig.Capture.Take(3).Status);

            rig.Screen("So tell me about your last project and the team.");
            Assert.Equal("and the team.", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void ShorterThanMinSendChars_IsKept()
        {
            var rig = new Rig("");
            rig.Screen("Ok");
            Assert.Equal(TakeStatus.NothingNew, rig.Capture.Take(3).Status);
            rig.Screen("Ok so what is a delegate");
            Assert.Equal("Ok so what is a delegate", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void Restore_SendsTheSameTextAgainWithNewWords()
        {
            var rig = new Rig("");
            rig.Screen("What is dependency injection");
            TakeResult first = rig.Capture.Take(3);
            rig.Screen("What is dependency injection in C sharp");
            rig.Capture.Restore(first.Ticket!);
            Assert.Equal("What is dependency injection in C sharp", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void RewrittenWordAfterSend_IsSentAgain()
        {
            var rig = new Rig("");
            rig.Screen("we drove all the way down to the ba");
            rig.Capture.Take(3);
            rig.Screen("we drove all the way down to the bank and parked");
            Assert.Equal("bank and parked", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void LeadingPunctuationOfPendingIsDropped()
        {
            var rig = new Rig("");
            rig.Screen("the quick brown fox jumps over the lazy dog");
            rig.Capture.Take(3);
            rig.Screen("the quick brown fox jumps over the lazy dog. Then it ran away.");
            Assert.Equal("Then it ran away.", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void Skip_MovesToEnd_AndIsMarked()
        {
            var rig = new Rig("");
            rig.Screen("small talk about the weather today");
            Assert.True(rig.Capture.Skip());
            Assert.Equal(TakeStatus.NothingNew, rig.Capture.Take(3).Status);
            rig.Idle();
            Assert.Contains("small talk about the weather today\n[SKIPPED 20:00:00]", rig.Out);
        }

        [Fact]
        public void CaptionsUnavailable_TextKept_RestartThrottled()
        {
            var rig = new Rig("");
            rig.Screen("first question about the project");
            rig.Screen(null);
            Assert.Equal(TakeStatus.CaptionsUnavailable, rig.Capture.Take(3).Status);
            Assert.False(rig.Capture.GetStatus().CaptionsAvailable);
            Assert.Equal(1, rig.Restarts);

            rig.Screen(null, 1);
            Assert.Equal(1, rig.Restarts);
            rig.Screen(null, 5);
            Assert.Equal(2, rig.Restarts);

            rig.Screen("first question about the project");
            Assert.Equal("first question about the project", rig.Capture.Take(3).Text);
        }

        [Fact]
        public void LiveCaptionsRestarted_NewTextAppended_OldKept()
        {
            var rig = new Rig("");
            rig.Screen("the first part of the conversation");
            rig.Capture.Take(3);
            rig.Screen(null);
            rig.Screen("");
            rig.Screen("a completely new part after the restart");
            Assert.Equal("a completely new part after the restart", rig.Capture.Take(3).Text);
            Assert.Equal("the first part of the conversation a completely new part after the restart", rig.Capture.Transcript);
        }

        [Fact]
        public void WhileSpeaking_LastWordsAreHeldBack_AfterPauseEverythingIsWritten()
        {
            var rig = new Rig("");
            rig.Screen("one two three four five six seven eight");
            rig.Capture.Tick();
            Assert.EndsWith("[20:00:00] one two three four", rig.Out);

            rig.Idle();
            Assert.EndsWith("one two three four five six seven eight", rig.Out);
            Assert.Equal(1, CountOf(rig.Out, "five"));
        }

        [Fact]
        public void UnconfirmedSend_BlocksFileAtCut_UntilConfirmed()
        {
            var rig = new Rig("");
            rig.Screen("question one is here");
            TakeResult r = rig.Capture.Take(3);
            rig.Screen("question one is here and question two follows");
            rig.Idle();
            Assert.DoesNotContain("question two", rig.Out);
            Assert.DoesNotContain("[SENT", rig.Out);

            rig.Capture.Confirm(r.Ticket!, "(queued)");
            rig.Idle();
            Assert.Contains("question one is here\n[SENT 20:00:00] (queued)\n", rig.Out);
            Assert.EndsWith("and question two follows", rig.Out);
        }

        [Fact]
        public void RestoredSend_LeavesNoMarker()
        {
            var rig = new Rig("");
            rig.Screen("question one is here");
            TakeResult r = rig.Capture.Take(3);
            rig.Capture.Restore(r.Ticket!);
            rig.Idle();
            Assert.DoesNotContain("[SENT", rig.Out);
            Assert.Contains("question one is here", rig.Out);
        }

        [Fact]
        public void LockedFile_RetriedLater_NothingLost()
        {
            var rig = new Rig("", failWrites: true);
            rig.Screen("one two three four five six");
            rig.Idle();
            rig.Idle();
            Assert.Equal(2, rig.WriteAttempts);
            rig.FailWrites = false;
            rig.Idle();
            Assert.EndsWith("one two three four five six", rig.Out);
        }

        [Fact]
        public void Status_ReportsPendingWordsAndTail()
        {
            var rig = new Rig("");
            rig.Screen("one two three four five");
            CaptureStatus s = rig.Capture.GetStatus();
            Assert.True(s.CaptionsAvailable);
            Assert.Equal(5, s.PendingWords);
            Assert.Equal("one two three four five", s.PendingTail);
        }

        [Fact]
        public void DeleteOld_RemovesOnlyOldSessionFiles()
        {
            using var dir = new TempDir();
            var now = new DateTime(2026, 11, 11);
            System.IO.File.WriteAllText(dir.File("old.txt"), "x");
            System.IO.File.WriteAllText(dir.File("new.txt"), "x");
            System.IO.File.SetLastWriteTime(dir.File("old.txt"), now.AddDays(-31));
            System.IO.File.SetLastWriteTime(dir.File("new.txt"), now.AddDays(-2));

            Assert.Equal(0, SessionWriter.DeleteOld(dir.Path, 0, now));
            Assert.Equal(1, SessionWriter.DeleteOld(dir.Path, 30, now));
            Assert.False(System.IO.File.Exists(dir.File("old.txt")));
            Assert.True(System.IO.File.Exists(dir.File("new.txt")));
        }

        [Fact]
        public void SessionWriter_RealFile_NamedAfterStartTime()
        {
            using var dir = new TempDir();
            var writer = new SessionWriter(dir.Path, () => new DateTime(2026, 11, 11, 20, 5, 0));
            writer.AppendText("hello there", newParagraph: true);
            writer.Close();
            Assert.Equal(dir.File("2026-11-11_2005.txt"), writer.FilePath);
            Assert.Contains("[20:05:00] hello there", System.IO.File.ReadAllText(writer.FilePath!));
        }

        private static int CountOf(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
            {
                n++;
            }

            return n;
        }
    }
}
