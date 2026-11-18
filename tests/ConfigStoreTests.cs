using System.IO;
using CaptionRelay.Config;
using Xunit;

namespace CaptionRelay.Tests
{
    public class ConfigStoreTests
    {
        private const string TestToken = "test-token-0123456789-abcdefghij";

        [Fact]
        public void Load_NoFile_CreatesDefaultsWithNewToken()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");

            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal(ConfigLoadOutcome.Created, result.Outcome);
            Assert.True(File.Exists(path));
            Assert.Equal(43, result.Config.Token.Length);
            Assert.Equal(AppConfig.DefaultPort, result.Config.Port);
            Assert.Equal("NumMinus", result.Config.SendKey);
            Assert.Equal("F9", result.Config.SkipKey);
            Assert.Equal("NumPlus", result.Config.TapKey);
            Assert.Equal(400, result.Config.TapHoldSendMs);
            Assert.Equal(15, result.Config.TapWords);
            Assert.Equal(400, result.Config.TapPasteDelayMs);
            Assert.True(result.Config.TapPreview);
            Assert.Equal("", result.Config.Prefix);
            Assert.Equal(3, result.Config.MinSendChars);
            Assert.Equal(120, result.Config.PollMs);
            Assert.True(result.Config.StartLiveCaptions);
            Assert.True(result.Config.IndicatorVisible);
            Assert.Null(result.Config.IndicatorX);
            Assert.Equal(30, result.Config.SessionRetentionDays);
            Assert.False(result.Config.HideLiveCaptions);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void Load_Twice_KeepsTokenAndDoesNotRewrite()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");

            string first = ConfigStore.Load(path).Config.Token;
            ConfigLoadResult second = ConfigStore.Load(path);

            Assert.Equal(ConfigLoadOutcome.Loaded, second.Outcome);
            Assert.Equal(first, second.Config.Token);
        }

        [Fact]
        public void Load_WrittenFileUsesCamelCaseKeys()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            ConfigStore.Load(path);

            string json = File.ReadAllText(path);
            foreach (string key in new[] { "port", "token", "sendKey", "skipKey", "tapKey", "tapWords", "tapPasteDelayMs", "tapHoldSendMs", "tapPreview", "tapPreviewOpacity", "prefix", "minSendChars", "pollMs", "startLiveCaptions", "closeLiveCaptionsOnExit", "indicatorX", "indicatorY", "indicatorVisible", "sessionRetentionDays", "hideLiveCaptions", "allowedExtensionIds", "startWithWindows", "extensionSetUp" })
            {
                Assert.Contains("\"" + key + "\"", json);
            }
        }

        [Fact]
        public void Load_MissingFields_FilledWithDefaultsAndSaved_TokenKept()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            File.WriteAllText(path, "{ \"token\": \"" + TestToken + "\", \"port\": 50000 }");

            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal(ConfigLoadOutcome.Repaired, result.Outcome);
            Assert.Equal(TestToken, result.Config.Token);
            Assert.Equal(50000, result.Config.Port);
            Assert.Equal(120, result.Config.PollMs);
            Assert.Contains("\"pollMs\"", File.ReadAllText(path));

            Assert.Equal(ConfigLoadOutcome.Loaded, ConfigStore.Load(path).Outcome);
        }

        [Fact]
        public void Load_InvalidJson_ThrowsAndLeavesFileUntouched()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            const string broken = "{ \"token\": \"abc\", port: }";
            File.WriteAllText(path, broken);

            Assert.Throws<ConfigException>(() => ConfigStore.Load(path));
            Assert.Equal(broken, File.ReadAllText(path));
        }

        [Fact]
        public void Load_WrongValueType_Throws()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            File.WriteAllText(path, "{ \"port\": \"not a number\" }");

            Assert.Throws<ConfigException>(() => ConfigStore.Load(path));
        }

        [Fact]
        public void Load_NonObjectRoot_Throws()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            File.WriteAllText(path, "[1, 2, 3]");

            Assert.Throws<ConfigException>(() => ConfigStore.Load(path));
        }

        [Fact]
        public void Load_OutOfRangeValues_ResetToDefaultsWithWarnings()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            var config = new AppConfig { Token = TestToken, Port = 80, PollMs = 5, MinSendChars = 0, SendKey = " " };
            ConfigStore.Save(path, config);

            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal(ConfigLoadOutcome.Repaired, result.Outcome);
            Assert.Equal(AppConfig.DefaultPort, result.Config.Port);
            Assert.Equal(AppConfig.DefaultPollMs, result.Config.PollMs);
            Assert.Equal(AppConfig.DefaultMinSendChars, result.Config.MinSendChars);
            Assert.Equal(AppConfig.DefaultSendKey, result.Config.SendKey);
            Assert.Equal(TestToken, result.Config.Token);
            Assert.Equal(4, result.Warnings.Count);
        }

        [Fact]
        public void Load_EmptyOrShortToken_GeneratesNewToken()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            ConfigStore.Save(path, new AppConfig { Token = "short" });

            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal(ConfigLoadOutcome.Repaired, result.Outcome);
            Assert.Equal(43, result.Config.Token.Length);
            Assert.Equal(result.Config.Token, ConfigStore.Load(path).Config.Token);
        }

        [Fact]
        public void Load_AllowsCommentsTrailingCommasAndAnyKeyCase()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            File.WriteAllText(path, "{\n  // my settings\n  \"Token\": \"" + TestToken + "\",\n  \"PREFIX\": \"Q: \",\n}");

            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal(TestToken, result.Config.Token);
            Assert.Equal("Q: ", result.Config.Prefix);
        }

        [Fact]
        public void Load_NullPrefix_BecomesEmpty()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            ConfigStore.Save(path, new AppConfig { Token = TestToken });
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"prefix\": \"\"", "\"prefix\": null"));

            ConfigLoadResult result = ConfigStore.Load(path);

            Assert.Equal("", result.Config.Prefix);
        }

        [Fact]
        public void Save_LeavesNoTempFile()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            ConfigStore.Save(path, new AppConfig { Token = TestToken });
            ConfigStore.Save(path, new AppConfig { Token = TestToken, Port = 50001 });

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(50001, ConfigStore.Load(path).Config.Port);
        }
    }
}
