using System;
using System.Threading;

namespace CaptionRelay.Infrastructure
{
    /// <summary>
    /// Named-mutex guard so only one CaptionRelay runs per user session
    /// (two keyboard hooks would send every message twice).
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        public const string DefaultName = @"Local\CaptionRelay.SingleInstance";

        private readonly Mutex _mutex;
        private bool _disposed;

        public SingleInstance(string name = DefaultName)
        {
            // If a previous instance was killed, its handle is gone and the mutex is created fresh.
            _mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
            IsFirst = createdNew;
        }

        public bool IsFirst { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (IsFirst)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Released from a different thread; disposing still frees the handle.
                }
            }

            _mutex.Dispose();
        }
    }
}
