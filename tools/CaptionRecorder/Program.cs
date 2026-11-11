using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CaptionRelay.Captions;

namespace CaptionRelay.Tools
{
    /// <summary>
    /// Polls Live Captions like the app does and writes every changed snapshot as one JSON line:
    /// {"t": ms since start, "text": "..."}.
    ///
    /// Privacy: text that was on screen before the recording belongs to the user. Nothing is written until the
    /// start marker ("... starts now") appears; then only the text after the marker is kept, until the marker
    /// scrolls off (older text has scrolled off before it).
    ///
    /// Usage: CaptionRecorder --out file.jsonl [--marker regex] [--pollMs 120] [--maxSeconds 480] [--doneFile path] [--afterDoneMs 4000]
    /// Stops when doneFile exists (+afterDoneMs), or after maxSeconds.
    /// </summary>
    internal static class Program
    {
        // The LAST occurrence counts (RightToLeft): an older recording may still be on screen with the same marker.
        private static Regex _startMarker = new Regex(@"start\w*\s+now\W*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft);

        private static int Main(string[] args)
        {
            string? marker = Arg(args, "--marker");
            if (marker != null)
            {
                _startMarker = new Regex(marker, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft);
            }

            string? outPath = Arg(args, "--out");
            if (outPath == null)
            {
                Console.Error.WriteLine("Usage: CaptionRecorder --out file.jsonl [--pollMs 120] [--maxSeconds 480] [--doneFile path] [--afterDoneMs 4000]");
                return 2;
            }

            int pollMs = int.Parse(Arg(args, "--pollMs") ?? "120");
            int maxSeconds = int.Parse(Arg(args, "--maxSeconds") ?? "480");
            int afterDoneMs = int.Parse(Arg(args, "--afterDoneMs") ?? "4000");
            string? doneFile = Arg(args, "--doneFile");

            if (!CaptionsReader.EnsureRunning())
            {
                Console.Error.WriteLine("Could not start Live Captions.");
                return 3;
            }

            var reader = new CaptionsReader();
            var clock = Stopwatch.StartNew();
            long doneAt = -1;
            bool markerSeen = false;
            string last = "\0";
            int polls = 0;
            int written = 0;
            int nulls = 0;
            long slowest = 0;

            using var writer = new StreamWriter(outPath, append: false, new UTF8Encoding(false));
            while (clock.Elapsed.TotalSeconds < maxSeconds)
            {
                long t0 = clock.ElapsedMilliseconds;
                string? raw = reader.Read();
                slowest = Math.Max(slowest, clock.ElapsedMilliseconds - t0);
                polls++;

                if (raw == null)
                {
                    nulls++;
                }
                else
                {
                    string? text = Sanitize(raw, ref markerSeen);
                    if (text != null && text != last)
                    {
                        last = text;
                        writer.WriteLine(JsonSerializer.Serialize(new { t = clock.ElapsedMilliseconds, text }));
                        written++;
                    }
                }

                if (doneFile != null && doneAt < 0 && File.Exists(doneFile))
                {
                    doneAt = clock.ElapsedMilliseconds;
                }

                if (doneAt >= 0 && clock.ElapsedMilliseconds - doneAt >= afterDoneMs)
                {
                    break;
                }

                int sleep = pollMs - (int)(clock.ElapsedMilliseconds - t0);
                if (sleep > 0)
                {
                    Thread.Sleep(sleep);
                }
            }

            Console.WriteLine($"polls={polls} written={written} nullReads={nulls} slowestReadMs={slowest} seconds={clock.Elapsed.TotalSeconds:F1} markerSeen={markerSeen}");
            return markerSeen ? 0 : 1;
        }

        private static string? Sanitize(string text, ref bool markerSeen)
        {
            Match m = _startMarker.Match(text);
            if (m.Success)
            {
                markerSeen = true;
                return text.Substring(m.Index + m.Length).Trim();
            }

            return markerSeen ? text : null;
        }

        private static string? Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
