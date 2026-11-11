using System;
using System.IO;
using System.Linq;
using CaptionRelay.Infrastructure;
using Xunit;

namespace CaptionRelay.Tests
{
    public class FileLoggerTests
    {
        private static readonly DateTime Day = new DateTime(2026, 11, 11, 10, 0, 0);

        [Fact]
        public void Write_AppendsTimestampSeverityAndMessage()
        {
            using var dir = new TempDir();
            var logger = new FileLogger(dir.Path, now: () => Day);

            logger.Write(LogSeverity.Info, "hello");
            logger.Write(LogSeverity.Error, "boom", new InvalidOperationException("bad state"));

            string text = File.ReadAllText(dir.File("captionrelay-20261111.log"));
            Assert.Contains("2026-11-11 10:00:00.000 [INFO ] hello", text);
            Assert.Contains("[ERROR] boom", text);
            Assert.Contains("InvalidOperationException: bad state", text);
        }

        [Fact]
        public void Write_RollsToNumberedFileWhenMaxBytesReached()
        {
            using var dir = new TempDir();
            var logger = new FileLogger(dir.Path, maxBytes: 300, now: () => Day);

            for (int i = 0; i < 20; i++)
            {
                logger.Write(LogSeverity.Info, "message number " + i.ToString("00") + " " + new string('x', 30));
            }

            string[] files = Directory.GetFiles(dir.Path, "captionrelay-20261111*.log");
            Assert.True(files.Length > 1, "expected more than one file");
            Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 300));
            Assert.True(File.Exists(dir.File("captionrelay-20261111.1.log")));

            string all = string.Concat(files.OrderBy(f => f).Select(File.ReadAllText));
            for (int i = 0; i < 20; i++)
            {
                Assert.Contains("message number " + i.ToString("00"), all);
            }
        }

        [Fact]
        public void Write_NewDayStartsNewFile()
        {
            using var dir = new TempDir();
            DateTime now = Day;
            var logger = new FileLogger(dir.Path, now: () => now);

            logger.Write(LogSeverity.Info, "day one");
            now = Day.AddDays(1);
            logger.Write(LogSeverity.Info, "day two");

            Assert.Contains("day one", File.ReadAllText(dir.File("captionrelay-20261111.log")));
            Assert.Contains("day two", File.ReadAllText(dir.File("captionrelay-20261112.log")));
        }

        [Fact]
        public void Constructor_DeletesOnlyOldLogFiles()
        {
            using var dir = new TempDir();
            string old = dir.File("captionrelay-20261101.log");
            string recent = dir.File("captionrelay-20261110.log");
            string other = dir.File("notes.txt");
            foreach (string f in new[] { old, recent, other })
            {
                File.WriteAllText(f, "x");
            }

            File.SetLastWriteTime(old, Day.AddDays(-10));
            File.SetLastWriteTime(recent, Day.AddDays(-1));
            File.SetLastWriteTime(other, Day.AddDays(-30));

            _ = new FileLogger(dir.Path, retentionDays: 7, now: () => Day);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
        }

        [Fact]
        public void Write_ToUnwritableDirectory_DoesNotThrow()
        {
            var logger = new FileLogger("Z:\\::invalid::\\logs");
            logger.Write(LogSeverity.Info, "ignored");
        }
    }
}
