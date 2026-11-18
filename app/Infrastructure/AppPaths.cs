using System;
using System.IO;

namespace CaptionRelay.Infrastructure
{
    /// <summary>
    /// Where the app keeps its files. Portable mode: if config.json sits next to the exe, everything stays there
    /// (as before). Otherwise everything lives in %LOCALAPPDATA%\CaptionRelay, so the single exe can be run from
    /// anywhere (Desktop, Downloads) without scattering files next to it, and the unpacked extension folder that
    /// Chrome points at does not move when the exe is moved.
    /// </summary>
    public static class AppPaths
    {
        public const string AppFolderName = "CaptionRelay";

        /// <summary>Folder of the running exe (also for a single-file publish).</summary>
        public static string ExeDir => AppContext.BaseDirectory;

        public static string DataDir { get; } = ResolveDataDir(
            ExeDir,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        public static string ConfigFile => Path.Combine(DataDir, "config.json");

        public static string LogsDir => Path.Combine(DataDir, "logs");

        public static string SessionsDir => Path.Combine(DataDir, "sessions");

        /// <summary>The browser extension, unpacked from the exe; Chrome loads it from here ("Load unpacked").</summary>
        public static string ExtensionDir => Path.Combine(DataDir, "extension");

        /// <summary>Native messaging host manifest (lets the extension read port + token from the app).</summary>
        public static string NativeHostManifest => Path.Combine(DataDir, "native-host.json");

        /// <summary>Exe folder when it already holds a config.json (portable), else %LOCALAPPDATA%\CaptionRelay.</summary>
        public static string ResolveDataDir(string exeDir, string localAppData)
        {
            if (File.Exists(Path.Combine(exeDir, "config.json")) || string.IsNullOrEmpty(localAppData))
            {
                return exeDir;
            }

            return Path.Combine(localAppData, AppFolderName);
        }
    }
}
