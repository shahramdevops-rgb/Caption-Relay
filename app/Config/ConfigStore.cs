using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace CaptionRelay.Config
{
    /// <summary>config.json could not be read or parsed. The file is left untouched.</summary>
    public sealed class ConfigException : Exception
    {
        public ConfigException(string message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }

    public enum ConfigLoadOutcome
    {
        /// <summary>No file existed; defaults and a new token were written.</summary>
        Created,

        /// <summary>The file was complete and valid; nothing was written.</summary>
        Loaded,

        /// <summary>Missing fields or invalid values were fixed and the file was rewritten.</summary>
        Repaired,
    }

    public sealed class ConfigLoadResult
    {
        public ConfigLoadResult(AppConfig config, ConfigLoadOutcome outcome, IReadOnlyList<string> warnings)
        {
            Config = config;
            Outcome = outcome;
            Warnings = warnings;
        }

        public AppConfig Config { get; }

        public ConfigLoadOutcome Outcome { get; }

        public IReadOnlyList<string> Warnings { get; }
    }

    public static class ConfigStore
    {
        public const int MinTokenLength = 16;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static readonly string[] ExpectedKeys = typeof(AppConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToArray();

        /// <summary>
        /// Loads config.json, creating it with defaults and a new token if it does not exist.
        /// Missing fields get defaults and invalid values are reset; the file is then rewritten
        /// (an existing valid token is always kept). Unreadable or invalid JSON throws
        /// <see cref="ConfigException"/> and the file is NOT overwritten.
        /// </summary>
        public static ConfigLoadResult Load(string path)
        {
            var warnings = new List<string>();

            if (!File.Exists(path))
            {
                var created = new AppConfig { Token = TokenGenerator.NewToken() };
                Save(path, created);
                return new ConfigLoadResult(created, ConfigLoadOutcome.Created, warnings);
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ConfigException($"Cannot read {path}: {ex.Message}", ex);
            }

            AppConfig? config;
            bool hasMissingKeys;
            try
            {
                var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                using JsonDocument doc = JsonDocument.Parse(json, docOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigException("config.json must contain a JSON object { ... }.");
                }

                hasMissingKeys = HasMissingKeys(doc.RootElement);
                config = doc.RootElement.Deserialize<AppConfig>(JsonOptions);
            }
            catch (JsonException ex)
            {
                string where = ex.LineNumber.HasValue ? $" (line {ex.LineNumber.Value + 1})" : "";
                throw new ConfigException($"config.json is not valid JSON{where}: {ex.Message}", ex);
            }

            if (config == null)
            {
                throw new ConfigException("config.json is empty (null).");
            }

            bool fixedValues = Validate(config, warnings);
            if (hasMissingKeys)
            {
                warnings.Add("Missing fields were filled with defaults.");
            }

            if (fixedValues || hasMissingKeys)
            {
                Save(path, config);
                return new ConfigLoadResult(config, ConfigLoadOutcome.Repaired, warnings);
            }

            return new ConfigLoadResult(config, ConfigLoadOutcome.Loaded, warnings);
        }

        /// <summary>Writes the config atomically (temp file + replace).</summary>
        public static void Save(string path, AppConfig config)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }

        private static bool HasMissingKeys(JsonElement root)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                present.Add(property.Name);
            }

            return ExpectedKeys.Any(k => !present.Contains(k));
        }

        /// <summary>Resets invalid values to defaults. Returns true if anything changed.</summary>
        private static bool Validate(AppConfig c, List<string> warnings)
        {
            bool changed = false;

            if (string.IsNullOrWhiteSpace(c.Token) || c.Token.Trim().Length < MinTokenLength)
            {
                c.Token = TokenGenerator.NewToken();
                warnings.Add($"token was empty or shorter than {MinTokenLength} chars; a new token was generated (paste it into the extension again).");
                changed = true;
            }

            if (c.Port < 1024 || c.Port > 65535)
            {
                warnings.Add($"port {c.Port} is out of range 1024-65535; reset to {AppConfig.DefaultPort}.");
                c.Port = AppConfig.DefaultPort;
                changed = true;
            }

            if (c.PollMs < 50 || c.PollMs > 1000)
            {
                warnings.Add($"pollMs {c.PollMs} is out of range 50-1000; reset to {AppConfig.DefaultPollMs}.");
                c.PollMs = AppConfig.DefaultPollMs;
                changed = true;
            }

            if (c.MinSendChars < 1 || c.MinSendChars > 1000)
            {
                warnings.Add($"minSendChars {c.MinSendChars} is out of range 1-1000; reset to {AppConfig.DefaultMinSendChars}.");
                c.MinSendChars = AppConfig.DefaultMinSendChars;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(c.SendKey))
            {
                c.SendKey = AppConfig.DefaultSendKey;
                warnings.Add($"sendKey was empty; reset to {AppConfig.DefaultSendKey} (use \"None\" to disable).");
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(c.SkipKey))
            {
                c.SkipKey = AppConfig.DefaultSkipKey;
                warnings.Add($"skipKey was empty; reset to {AppConfig.DefaultSkipKey} (use \"None\" to disable).");
                changed = true;
            }

            if (c.Prefix == null)
            {
                c.Prefix = "";
                changed = true;
            }

            return changed;
        }
    }
}
