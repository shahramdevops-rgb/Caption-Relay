using System;
using System.IO;
using System.Media;
using CaptionRelay.Infrastructure;
using CaptionRelay.Sending;

namespace CaptionRelay.UI
{
    /// <summary>
    /// Short generated tones (independent of the Windows sound scheme): a soft rising chime for "sent", one soft tone
    /// for "queued", a low tone for errors. "Nothing new" and "Skipped" are silent.
    /// </summary>
    public sealed class Sounds : IDisposable
    {
        private const int SampleRate = 22050;

        private readonly SoundPlayer _success = new SoundPlayer(new MemoryStream(Wave((880, 70), (1320, 90))));
        private readonly SoundPlayer _queued = new SoundPlayer(new MemoryStream(Wave((990, 80))));
        private readonly SoundPlayer _error = new SoundPlayer(new MemoryStream(Wave((330, 120), (247, 160))));

        public bool Enabled { get; set; } = true;

        public void Play(FeedbackKind kind, string message)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                switch (kind)
                {
                    case FeedbackKind.Success:
                        _success.Play();
                        break;
                    case FeedbackKind.Error:
                        _error.Play();
                        break;
                    case FeedbackKind.Info when message == "Queued":
                        _queued.Play();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Sound failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _success.Dispose();
            _queued.Dispose();
            _error.Dispose();
        }

        /// <summary>16-bit mono PCM WAV of sine tones (Hz, ms) with short fades, at moderate volume.</summary>
        internal static byte[] Wave(params (int Hz, int Ms)[] tones)
        {
            int samples = 0;
            foreach ((int _, int ms) in tones)
            {
                samples += SampleRate * ms / 1000;
            }

            using var ms2 = new MemoryStream();
            using var w = new BinaryWriter(ms2);
            int dataBytes = samples * 2;
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + dataBytes);
            w.Write("WAVEfmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(SampleRate);
            w.Write(SampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8.ToArray());
            w.Write(dataBytes);

            foreach ((int hz, int ms) in tones)
            {
                int n = SampleRate * ms / 1000;
                int fade = Math.Min(n / 4, SampleRate / 200); // 5 ms
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                    double v = Math.Sin(2 * Math.PI * hz * i / SampleRate) * 0.22 * env;
                    w.Write((short)(v * short.MaxValue));
                }
            }

            w.Flush();
            return ms2.ToArray();
        }
    }
}
