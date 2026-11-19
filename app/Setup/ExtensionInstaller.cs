using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace CaptionRelay.Setup
{
    public enum Browser
    {
        Chrome,
        Edge,
    }

    /// <summary>
    /// Everything the single exe does so the browser extension works with as few clicks as Chrome allows:
    /// unpacks the extension (embedded in the exe) to a fixed folder, registers the native messaging host (so the
    /// extension gets port + token by itself), and opens the browser's extensions page. Chrome does not let programs
    /// install extensions that are not on the Chrome Web Store, so "Load unpacked" stays a one-time user step.
    /// </summary>
    public static class ExtensionInstaller
    {
        /// <summary>Fixed by the "key" in extension/manifest.json.</summary>
        public const string ExtensionId = "hpmjjnjmlhbolpmeafdkmdldlfmoaaee";

        internal const string ResourcePrefix = "extension/";

        private static readonly string[] BrowserHostKeys =
        {
            @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHost.HostName,
            @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHost.HostName,
        };

        /// <summary>
        /// Writes the embedded extension files into <paramref name="targetDir"/>; only files whose content differs
        /// are written (an unchanged extension is left alone). Returns the number of files written.
        /// </summary>
        public static int Extract(string targetDir)
        {
            Assembly assembly = typeof(ExtensionInstaller).Assembly;
            int written = 0;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                string normalized = name.Replace('\\', '/');
                if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = normalized.Substring(ResourcePrefix.Length);
                string path = Path.GetFullPath(Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[] content;
                using (Stream stream = assembly.GetManifestResourceStream(name)!)
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    content = ms.ToArray();
                }

                if (File.Exists(path) && SameBytes(File.ReadAllBytes(path), content))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
                written++;
            }

            return written;
        }

        /// <summary>Number of extension files embedded in the exe (0 means the build did not include them).</summary>
        public static int EmbeddedFileCount()
        {
            int n = 0;
            foreach (string name in typeof(ExtensionInstaller).Assembly.GetManifestResourceNames())
            {
                if (name.Replace('\\', '/').StartsWith(ResourcePrefix, StringComparison.Ordinal))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Writes the native messaging host manifest pointing at <paramref name="exePath"/>.</summary>
        public static void WriteHostManifest(string manifestPath, string exePath)
        {
            var manifest = new Dictionary<string, object>
            {
                ["name"] = NativeHost.HostName,
                ["description"] = "CaptionRelay: gives the CaptionRelay extension the app's port and token",
                ["path"] = exePath,
                ["type"] = "stdio",
                ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Registers the host manifest for Chrome and Edge under <paramref name="root"/> (HKCU; no admin).</summary>
        public static void RegisterHost(string manifestPath, RegistryKey root)
        {
            foreach (string keyPath in BrowserHostKeys)
            {
                using RegistryKey key = root.CreateSubKey(keyPath, writable: true);
                key.SetValue("", manifestPath);
            }
        }

        /// <summary>Path of chrome.exe / msedge.exe, or null if that browser is not installed.</summary>
        public static string? FindBrowser(Browser browser)
        {
            string exe = browser == Browser.Chrome ? "chrome.exe" : "msedge.exe";
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using RegistryKey? key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                    if (key?.GetValue("") is string path && File.Exists(path.Trim('"')))
                    {
                        return path.Trim('"');
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
                {
                    // Try the next place.
                }
            }

            string sub = browser == Browser.Chrome ? @"Google\Chrome\Application\chrome.exe" : @"Microsoft\Edge\Application\msedge.exe";
            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData,
            })
            {
                string candidate = Path.Combine(Environment.GetFolderPath(folder), sub);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>Opens chrome://extensions (or edge://extensions) in that browser. Returns false if it could not.</summary>
        public static bool OpenExtensionsPage(Browser browser)
        {
            string? exe = FindBrowser(browser);
            if (exe == null)
            {
                return false;
            }

            string url = browser == Browser.Chrome ? "chrome://extensions/" : "edge://extensions/";
            try
            {
                Process.Start(new ProcessStartInfo(exe, url) { UseShellExecute = false })?.Dispose();
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return false;
            }
        }

        private static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    }
}
