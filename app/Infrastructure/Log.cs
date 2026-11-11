using System;

namespace CaptionRelay.Infrastructure
{
    /// <summary>
    /// App-wide log facade. Calls before <see cref="Init"/> are ignored.
    /// Never log caption/transcript text here (only lengths); the full text lives in sessions/.
    /// </summary>
    public static class Log
    {
        private static FileLogger? _logger;

        public static void Init(FileLogger logger) => _logger = logger;

        public static void Info(string message) => _logger?.Write(LogSeverity.Info, message);

        public static void Warn(string message, Exception? exception = null) => _logger?.Write(LogSeverity.Warn, message, exception);

        public static void Error(string message, Exception? exception = null) => _logger?.Write(LogSeverity.Error, message, exception);
    }
}
