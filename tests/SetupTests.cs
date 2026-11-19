using System;
using System.IO;
using System.Text.Json;
using CaptionRelay.Config;
using CaptionRelay.Infrastructure;
using CaptionRelay.Setup;
using Microsoft.Win32;
using Xunit;

namespace CaptionRelay.Tests
{
    public class SetupTests
    {
        private const string Origin = "chrome-extension://" + ExtensionInstaller.ExtensionId + "/";

        [Fact]
        public void NativeHost_DetectsChromeLaunch()
        {
            Assert.True(NativeHost.IsNativeHostLaunch(new[] { Origin, "--parent-window=0" }));
            Assert.False(NativeHost.IsNativeHostLaunch(new string[0]));
            Assert.False(NativeHost.IsNativeHostLaunch(new[] { "--something" }));
            Assert.Equal(ExtensionInstaller.ExtensionId, NativeHost.ExtensionIdFromOrigin(Origin));
        }

        [Fact]
        public void NativeHost_AnswersWithPortAndToken()
        {
            var config = new AppConfig { Port = 50123, Token = "the-token-0123456789" };
            JsonElement reply = RunHost(config);
            Assert.Equal("config", reply.GetProperty("type").GetString());
            Assert.Equal(50123, reply.GetProperty("port").GetInt32());
            Assert.Equal("the-token-0123456789", reply.GetProperty("token").GetString());
        }

        [Fact]
        public void NativeHost_RefusesExtensionNotInAllowList()
        {
            var config = new AppConfig { Token = "the-token-0123456789", AllowedExtensionIds = new[] { "someotherid" } };
            JsonElement reply = RunHost(config);
            Assert.Equal("error", reply.GetProperty("type").GetString());
            Assert.False(reply.TryGetProperty("token", out _));
        }

        [Fact]
        public void NativeHost_ReportsConfigErrorsWithoutCrashing()
        {
            JsonElement reply = RunHost(() => throw new ConfigException("broken"));
            Assert.Equal("error", reply.GetProperty("type").GetString());
        }

        [Fact]
        public void NativeHost_NoInput_ExitsWithoutReply()
        {
            var output = new MemoryStream();
            int code = NativeHost.Run(new MemoryStream(), output, Origin, () => new AppConfig());
            Assert.Equal(1, code);
            Assert.Equal(0, output.Length);
        }

        [Fact]
        public void NativeHost_Launch_CallsTheLauncher()
        {
            int launches = 0;
            JsonElement reply = RunHost(() => new AppConfig { Token = "the-token-0123456789" }, "{\"type\":\"launch\"}", () =>
            {
                launches++;
                return "started";
            });
            Assert.Equal(1, launches);
            Assert.Equal("launched", reply.GetProperty("type").GetString());
            Assert.Equal("started", reply.GetProperty("status").GetString());
            Assert.False(reply.TryGetProperty("token", out _));
        }

        [Fact]
        public void NativeHost_Launch_RefusedForExtensionNotInAllowList()
        {
            int launches = 0;
            JsonElement reply = RunHost(() => new AppConfig { AllowedExtensionIds = new[] { "someotherid" } }, "{\"type\":\"launch\"}", () =>
            {
                launches++;
                return "started";
            });
            Assert.Equal(0, launches);
            Assert.Equal("error", reply.GetProperty("type").GetString());
        }

        [Fact]
        public void AppLauncher_DetectsRunningAppByMutex()
        {
            string name = @"Local\CaptionRelayTests." + Guid.NewGuid().ToString("N");
            Assert.False(AppLauncher.IsAppRunning(name));
            using var instance = new SingleInstance(name);
            Assert.True(AppLauncher.IsAppRunning(name));
        }

        [Fact]
        public void Extract_WritesTheExtensionAndIsIdempotent()
        {
            using var dir = new TempDir();
            int first = ExtensionInstaller.Extract(dir.Path);
            Assert.True(first > 10, $"only {first} files extracted");
            Assert.Equal(first, ExtensionInstaller.EmbeddedFileCount());

            string manifest = File.ReadAllText(dir.File("manifest.json"));
            Assert.Contains("\"manifest_version\": 3", manifest);
            Assert.Contains("nativeMessaging", manifest);
            Assert.True(File.Exists(Path.Combine(dir.Path, "providers", "gemini", "selectors.js")));
            Assert.True(File.Exists(Path.Combine(dir.Path, "icons", "icon128.png")));

            Assert.Equal(0, ExtensionInstaller.Extract(dir.Path));

            File.WriteAllText(dir.File("background.js"), "changed");
            Assert.Equal(1, ExtensionInstaller.Extract(dir.Path));
            Assert.NotEqual("changed", File.ReadAllText(dir.File("background.js")));
        }

        [Fact]
        public void HostManifest_PointsAtExeAndAllowsOnlyOurExtension()
        {
            using var dir = new TempDir();
            string path = dir.File("native-host.json");
            ExtensionInstaller.WriteHostManifest(path, @"C:\Some Folder\CaptionRelay.exe");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            Assert.Equal(NativeHost.HostName, root.GetProperty("name").GetString());
            Assert.Equal(@"C:\Some Folder\CaptionRelay.exe", root.GetProperty("path").GetString());
            Assert.Equal("stdio", root.GetProperty("type").GetString());
            Assert.Equal(new[] { Origin }, JsonSerializer.Deserialize<string[]>(root.GetProperty("allowed_origins").GetRawText()));
        }

        [Fact]
        public void RegisterHost_WritesChromeAndEdgeKeys()
        {
            string testRoot = @"Software\CaptionRelayTests\" + Guid.NewGuid().ToString("N");
            try
            {
                using (RegistryKey root = Registry.CurrentUser.CreateSubKey(testRoot))
                {
                    ExtensionInstaller.RegisterHost(@"C:\x\native-host.json", root);
                }

                foreach (string browser in new[] { @"Google\Chrome", @"Microsoft\Edge" })
                {
                    using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{testRoot}\Software\{browser}\NativeMessagingHosts\{NativeHost.HostName}");
                    Assert.NotNull(key);
                    Assert.Equal(@"C:\x\native-host.json", key!.GetValue(""));
                }
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
            }
        }

        [Fact]
        public void DataDir_IsPortableOnlyWhenConfigIsNextToTheExe()
        {
            using var exeDir = new TempDir();
            string local = @"C:\Users\x\AppData\Local";
            Assert.Equal(Path.Combine(local, "CaptionRelay"), AppPaths.ResolveDataDir(exeDir.Path, local));

            File.WriteAllText(exeDir.File("config.json"), "{}");
            Assert.Equal(exeDir.Path, AppPaths.ResolveDataDir(exeDir.Path, local));
        }

        private static JsonElement RunHost(AppConfig config) => RunHost(() => config);

        private static JsonElement RunHost(Func<AppConfig> load, string request = "{\"type\":\"getConfig\"}", Func<string>? launch = null)
        {
            var input = new MemoryStream();
            NativeHost.WriteMessage(input, request);
            input.Position = 0;
            var output = new MemoryStream();

            Assert.Equal(0, NativeHost.Run(input, output, Origin, load, launch));

            output.Position = 0;
            string? json = NativeHost.ReadMessage(output);
            Assert.NotNull(json);
            return JsonDocument.Parse(json!).RootElement.Clone();
        }
    }
}
