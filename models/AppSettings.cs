using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class AppSettings
    {
        public static readonly string DefaultPrompt =
            "You are a professional simultaneous interpreter with specialized knowledge in all fields. " +
            "Provide a fluent and precise oral translation of the sentence enclosed in 🔤 to {0}. " +
            "The sentence may be incomplete - translate it as is, without adding or omitting anything. " +
            "Return ONLY the translated sentence on a single line. " +
            "No explanations, no comments, no quotes. Remove all 🔤 from your output.";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Current { get; set; } = new();

        public string ApiUrl { get; set; } = "https://api.openai.com/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "";
        public string Prompt { get; set; } = DefaultPrompt;
        public string TargetLanguage { get; set; } = "ru-RU";
        public int ContextCount { get; set; } = 1;
        public int VisibleLines { get; set; } = 10;
        public bool Streaming { get; set; } = true;
        public double Temperature { get; set; } = 0.3;
        public int MinSentenceLength { get; set; } = 10;
        public int DebounceMs { get; set; } = 1000;
        public bool Topmost { get; set; } = true;

        public int PollIntervalMs { get; set; } = 100;
        public int StreamFlushMs { get; set; } = 120;
        public int RequestTimeoutSeconds { get; set; } = 120;
        public int RetryCount { get; set; } = 2;
        public int MaxConcurrentRequests { get; set; } = 2;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public LogLevel LogLevel { get; set; } = LogLevel.Warning;

        public static string SettingsPath =>
            Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        private static string LegacySettingsPath =>
            Path.Combine(AppContext.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();

                if (File.Exists(LegacySettingsPath))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(LegacySettingsPath), JsonOptions) ?? new AppSettings();
                    settings.Save();
                    try
                    {
                        File.Delete(LegacySettingsPath);
                    }
                    catch
                    {
                    }
                    return settings;
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(SettingsPath))
                        File.Move(SettingsPath, SettingsPath + ".bak");
                }
                catch
                {
                }
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
            }
        }

        public AppSettings Clone() => (AppSettings)MemberwiseClone();
    }
}
