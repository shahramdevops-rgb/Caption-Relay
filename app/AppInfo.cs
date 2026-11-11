using System.Reflection;

namespace CaptionRelay
{
    /// <summary>Static facts about the app (name, version) shared by UI, logs and the WebSocket hello.</summary>
    public static class AppInfo
    {
        public const string Name = "CaptionRelay";

        public static string Version { get; } =
            typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
