using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CaptionRelay.Captions
{
    /// <summary>
    /// Appends the session transcript to sessions/yyyy-MM-dd_HHmm.txt (one file per app run, created on the
    /// first write). Text and markers are queued and written by <see cref="Flush"/>; on an I/O error the queue is
    /// kept and retried on the next flush. Not thread-safe (used under the capture lock).
    /// </summary>
    public sealed class SessionWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly Action<string> _write;
        private readonly Func<DateTime> _now;
        private readonly StringBuilder _queue = new StringBuilder();
        private bool _headerQueued;
        private bool _paragraphOpen;
        private string? _lastError;

        /// <summary>Writes to a file in <paramref name="directory"/> named after the current time.</summary>
        public SessionWriter(string directory, Func<DateTime>? now = null)
            : this((Action<string>?)null, now)
        {
            DateTime start = _now();
            FilePath = Path.Combine(directory, start.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + ".txt");
            string path = FilePath;
            _write = text =>
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(path, text, Utf8NoBom);
            };
        }

        /// <summary>For tests: custom output.</summary>
        internal SessionWriter(Action<string>? write, Func<DateTime>? now)
        {
            _write = write ?? (_ => { });
            _now = now ?? (() => DateTime.Now);
        }

        /// <summary>Raised when a flush fails, once per distinct error message.</summary>
        public event Action<string>? WriteFailed;

        public string? FilePath { get; }

        /// <summary>Queues transcript text; a new paragraph starts with a [hh:mm:ss] time stamp.</summary>
        public void AppendText(string text, bool newParagraph)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return;
            }

            QueueHeader();
            if (newParagraph || !_paragraphOpen)
            {
                _queue.Append("\r\n[").Append(Time()).Append("] ");
            }
            else if (char.IsLetterOrDigit(text[0]))
            {
                _queue.Append(' '); // "word" + ". Next" stays "word. Next"
            }

            _queue.Append(text);
            _paragraphOpen = true;
        }

        /// <summary>Queues a marker line such as "[SENT 12:34:56]".</summary>
        public void AppendMarker(string kind, string? note = null, DateTime? at = null)
        {
            QueueHeader();
            _queue.Append("\r\n[").Append(kind).Append(' ').Append(Time(at)).Append(']');
            if (!string.IsNullOrEmpty(note))
            {
                _queue.Append(' ').Append(note);
            }

            _paragraphOpen = false;
        }

        /// <summary>Writes the queue. Returns false (and keeps the queue) if writing failed.</summary>
        public bool Flush()
        {
            if (_queue.Length == 0)
            {
                return true;
            }

            try
            {
                _write(_queue.ToString());
                _queue.Clear();
                _lastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                string error = $"Session file not written: {ex.Message}";
                if (error != _lastError)
                {
                    WriteFailed?.Invoke(error);
                }

                _lastError = error;
                return false;
            }
        }

        /// <summary>Ends the file with a line break (app closing).</summary>
        public void Close()
        {
            if (_headerQueued)
            {
                _queue.Append("\r\n");
            }

            Flush();
        }

        /// <summary>Deletes *.txt files in <paramref name="directory"/> older than <paramref name="days"/> (0 = keep all).</summary>
        public static int DeleteOld(string directory, int days, DateTime? now = null)
        {
            if (days <= 0 || !Directory.Exists(directory))
            {
                return 0;
            }

            DateTime cutoff = (now ?? DateTime.Now).AddDays(-days);
            int deleted = 0;
            foreach (string file in Directory.GetFiles(directory, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Skip locked files.
                }
            }

            return deleted;
        }

        private void QueueHeader()
        {
            if (_headerQueued)
            {
                return;
            }

            _queue.Append("===== CaptionRelay session ").Append(_now().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" =====");
            _headerQueued = true;
        }

        private string Time(DateTime? at = null) => (at ?? _now()).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
