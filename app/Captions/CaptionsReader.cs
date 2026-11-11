using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace CaptionRelay.Captions
{
    /// <summary>
    /// Reads the caption text of Windows 11 Live Captions via UI Automation (from LiveCaptionsGemini, tested there).
    /// Thread-safe: called from the background poller and from the UI thread.
    /// </summary>
    public sealed class CaptionsReader : ICaptionSource
    {
        public const string ProcessName = "LiveCaptions";
        private const string TextBlockId = "CaptionsTextBlock";
        private const string Placeholder = "Ready to show live captions";
        private const long RefindIntervalMs = 1000;

        private readonly object _lock = new object();
        private AutomationElement? _element;

        // The window exists but CaptionsTextBlock doesn't yet: Live Captions only creates it after the first
        // speech (before that it shows ReadyToCaptionTextBlock). That is "connected, no text", not "not found".
        private bool _windowSeen;
        private long _lastFindTick = long.MinValue / 2;

        /// <summary>Starts LiveCaptions.exe if it isn't running. Returns false if it could not be started.</summary>
        public static bool EnsureRunning()
        {
            if (IsProcessRunning())
            {
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo("LiveCaptions.exe") { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Normalized caption text ("" when only the placeholder is shown), or null when the captions window
        /// isn't available.
        /// </summary>
        public string? Read()
        {
            lock (_lock)
            {
                // Second attempt covers a stale cached element: drop it, re-find (throttled), read again.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (_element == null && !TryFind())
                    {
                        return _windowSeen ? "" : null;
                    }

                    try
                    {
                        return Normalize(_element!.Current.Name);
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException || ex is COMException || ex is InvalidOperationException)
                    {
                        _element = null;
                        _windowSeen = false;
                    }
                }

                return null;
            }
        }

        /// <summary>Collapses all whitespace runs to single spaces; the placeholder becomes "".</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "";
            }

            var sb = new StringBuilder(raw.Length);
            bool pendingSpace = false;
            foreach (char ch in raw)
            {
                if (char.IsWhiteSpace(ch))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(ch);
            }

            string text = sb.ToString();
            return text.StartsWith(Placeholder, StringComparison.OrdinalIgnoreCase) ? "" : text;
        }

        // Caller holds _lock.
        private bool TryFind()
        {
            long now = Environment.TickCount64;
            if (now - _lastFindTick < RefindIntervalMs)
            {
                return false;
            }

            _lastFindTick = now;
            _windowSeen = false;

            foreach (Process process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    try
                    {
                        var byPid = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
                        var byId = new PropertyCondition(AutomationElement.AutomationIdProperty, TextBlockId);
                        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, byPid))
                        {
                            _windowSeen = true;
                            AutomationElement? element = window.FindFirst(TreeScope.Descendants, byId);
                            if (element != null)
                            {
                                _element = element;
                                return true;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is ElementNotAvailableException || ex is COMException || ex is InvalidOperationException)
                    {
                        // Window is closing or not ready yet; try the next process / next attempt.
                    }
                }
            }

            return false;
        }

        private static bool IsProcessRunning()
        {
            Process[] processes = Process.GetProcessesByName(ProcessName);
            foreach (Process p in processes)
            {
                p.Dispose();
            }

            return processes.Length > 0;
        }
    }
}
