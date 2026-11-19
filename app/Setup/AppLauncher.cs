using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CaptionRelay.Infrastructure;

namespace CaptionRelay.Setup
{
    /// <summary>
    /// Starts the CaptionRelay app for the extension popup's "turn on" (via the native messaging host). Chrome may run
    /// native hosts inside a job object that ends their child processes when the host exits, so the app is started
    /// outside that job (CREATE_BREAKAWAY_FROM_JOB); if the job forbids that, Explorer starts it instead (Explorer's
    /// own process is not in Chrome's job).
    /// </summary>
    public static class AppLauncher
    {
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const int ERROR_ACCESS_DENIED = 5;

        public static bool IsAppRunning(string mutexName = SingleInstance.DefaultName)
        {
            if (Mutex.TryOpenExisting(mutexName, out Mutex? mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }

        /// <summary>Returns "already running", "started" or "started via explorer". Throws if it could not start.</summary>
        public static string Launch(string exePath)
        {
            if (IsAppRunning())
            {
                return "already running";
            }

            string commandLine = $"\"{exePath}\" {NativeHost.LaunchedByExtensionArg}";
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero, Path.GetDirectoryName(exePath), ref si, out PROCESS_INFORMATION pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return "started";
            }

            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_ACCESS_DENIED)
            {
                throw new Win32Exception(error, "Could not start CaptionRelay");
            }

            // The job does not allow breakaway: let the shell start it.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exePath + "\"") { UseShellExecute = false })?.Dispose();
            return "started via explorer";
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }
}
