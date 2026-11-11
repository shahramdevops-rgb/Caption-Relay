using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CaptionRelay.Infrastructure
{
    public enum LogSeverity
    {
        Info,
        Warn,
        Error,
    }

    /// <summary>
    /// Plain-text rolling file logger: one file per day (captionrelay-yyyyMMdd.log), continued in
    /// captionrelay-yyyyMMdd.1.log, .2.log ... when a file reaches <c>maxBytes</c>. Files older than
    /// <c>retentionDays</c> are deleted on startup. Thread-safe; never throws.
    /// </summary>
    public sealed class FileLogger
    {
        public const string FilePrefix = "captionrelay-";
        public const long DefaultMaxBytes = 5L * 1024 * 1024;
        public const int DefaultRetentionDays = 7;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly object _lock = new object();
        private readonly string _dir;
        private readonly long _maxBytes;
        private readonly int _retentionDays;
        private readonly Func<DateTime> _now;

        private string? _currentPath;
        private DateTime _currentDate;
        private long _currentSize;

        public FileLogger(string directory, long maxBytes = DefaultMaxBytes, int retentionDays = DefaultRetentionDays, Func<DateTime>? now = null)
        {
            _dir = directory;
            _maxBytes = maxBytes;
            _retentionDays = retentionDays;
            _now = now ?? (() => DateTime.Now);

            try
            {
                Directory.CreateDirectory(_dir);
                DeleteOldFiles();
            }
            catch
            {
                // Logging must never stop the app.
            }
        }

        public string LogDirectory => _dir;

        public void Write(LogSeverity severity, string message, Exception? exception = null)
        {
            try
            {
                DateTime now = _now();
                var sb = new StringBuilder();
                sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                sb.Append(" [").Append(SeverityText(severity)).Append("] ").Append(message);
                if (exception != null)
                {
                    sb.AppendLine().Append(exception);
                }

                sb.AppendLine();
                string text = sb.ToString();
                int bytes = Utf8NoBom.GetByteCount(text);

                lock (_lock)
                {
                    string path = GetWritablePath(now, bytes);
                    File.AppendAllText(path, text, Utf8NoBom);
                    _currentSize += bytes;
                }
            }
            catch
            {
                // Logging must never stop the app.
            }
        }

        public static string FileName(DateTime date, int index)
        {
            string day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return index == 0 ? $"{FilePrefix}{day}.log" : $"{FilePrefix}{day}.{index}.log";
        }

        private string GetWritablePath(DateTime now, int bytes)
        {
            if (_currentPath == null || now.Date != _currentDate)
            {
                _currentDate = now.Date;
                _currentPath = null;
            }

            if (_currentPath != null && _currentSize + bytes <= _maxBytes)
            {
                return _currentPath;
            }

            // First file of the day that is empty or still has room.
            for (int i = 0; ; i++)
            {
                string path = Path.Combine(_dir, FileName(_currentDate, i));
                long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size == 0 || size + bytes <= _maxBytes)
                {
                    _currentPath = path;
                    _currentSize = size;
                    return path;
                }
            }
        }

        private void DeleteOldFiles()
        {
            DateTime cutoff = _now().AddDays(-_retentionDays);
            foreach (string file in Directory.GetFiles(_dir, FilePrefix + "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // A locked or vanished file is not worth failing over.
                }
            }
        }

        private static string SeverityText(LogSeverity severity) => severity switch
        {
            LogSeverity.Warn => "WARN ",
            LogSeverity.Error => "ERROR",
            _ => "INFO ",
        };
    }
}
