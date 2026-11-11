using System;
using System.IO;

namespace CaptionRelay.Infrastructure
{
    /// <summary>All runtime files live next to the exe.</summary>
    public static class AppPaths
    {
        public static string BaseDir => AppContext.BaseDirectory;

        public static string ConfigFile => Path.Combine(BaseDir, "config.json");

        public static string LogsDir => Path.Combine(BaseDir, "logs");

        public static string SessionsDir => Path.Combine(BaseDir, "sessions");
    }
}
