using System;
using CaptionRelay.Infrastructure;
using Microsoft.Win32;
using Xunit;

namespace CaptionRelay.Tests
{
    /// <summary>Uses its own HKCU test key, never the real Run key.</summary>
    public class StartupRegistrationTests
    {
        [Fact]
        public void SetOnAndOff_WritesAndRemovesQuotedExePath()
        {
            string keyPath = @"Software\CaptionRelayTests\" + Guid.NewGuid().ToString("N");
            var reg = new StartupRegistration(keyPath);
            const string exe = @"C:\Program Files\CaptionRelay\CaptionRelay.exe";
            try
            {
                Assert.False(reg.IsRegistered(exe));
                reg.Set(true, exe);
                Assert.True(reg.IsRegistered(exe));
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    Assert.Equal("\"" + exe + "\"", key!.GetValue(StartupRegistration.ValueName));
                }

                Assert.False(reg.IsRegistered(@"D:\elsewhere\CaptionRelay.exe"));
                reg.Set(false, exe);
                Assert.False(reg.IsRegistered(exe));
                reg.Set(false, exe); // removing twice is fine
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\CaptionRelayTests", throwOnMissingSubKey: false);
            }
        }
    }
}
