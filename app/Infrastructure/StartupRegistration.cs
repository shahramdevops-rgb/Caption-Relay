using System;
using Microsoft.Win32;

namespace CaptionRelay.Infrastructure
{
    /// <summary>
    /// "Start with Windows": a value in HKCU\...\CurrentVersion\Run (per user, no admin rights). Only written when the
    /// user turns the option on or off in the menu.
    /// </summary>
    public sealed class StartupRegistration
    {
        public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "CaptionRelay";

        private readonly string _keyPath;

        /// <param name="keyPath">Registry path under HKCU (tests use their own key).</param>
        public StartupRegistration(string keyPath = DefaultKeyPath)
        {
            _keyPath = keyPath;
        }

        /// <summary>The command stored for <paramref name="exePath"/> (quoted).</summary>
        public static string Command(string exePath) => "\"" + exePath + "\"";

        public bool IsRegistered(string exePath)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command(exePath), StringComparison.OrdinalIgnoreCase);
        }

        public void Set(bool enabled, string exePath)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command(exePath));
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
