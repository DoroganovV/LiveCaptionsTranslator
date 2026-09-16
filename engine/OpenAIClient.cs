using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.engine
{
    public class ChatMessage
    {
        public string role { get; set; } = "";
        public string content { get; set; } = "";
    }

    public sealed class ApiException : Exception
    {
        public int StatusCode { get; }
        public string Body { get; }

        public ApiException(int code, string body) : base($"HTTP {code}")
        {
            StatusCode = code;
            Body = body;
        }
    }

    public static class OpenAIClient
    {
        // The timeout is applied per request (via CancellationToken), so that
        // loading the model in LM Studio has enough time to complete.
        private static readonly HttpClient Http = new()
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        private readonly record struct Variant(bool Streaming, bool MinimalBody);

        private static readonly object ModeGate = new();
        private static string _modeKey = "";
        private static int _modeIndex;

        public static async Task TranslateAsync(
            AppSettings settings,
            List<ChatMessage> context,
            string text,
            Action<string> onChunk,
            CancellationToken token)
        {
            string url = TextUtil.NormalizeUrl(settings.ApiUrl);

            var messages = new List<ChatMessage>
            {
                new() { role = "system", content = BuildSystemPrompt(settings) }
            };
            messages.AddRange(context);
            messages.Add(new ChatMessage { role = "user", content = $"🔤 {text} 🔤" });

            var variants = new List<Variant>();
            if (settings.Streaming)
                variants.Add(new Variant(true, false));
            variants.Add(new Variant(false, false));
            variants.Add(new Variant(false, true));

            // Start from the variant the endpoint accepted last time; otherwise every line
            // wastes two requests that are guaranteed to fail.
            string key = $"{url}|{settings.Model}|{settings.Streaming}";
            int start;
            lock (ModeGate)
                start = string.Equals(_modeKey, key, StringComparison.Ordinal)
                    ? Math.Min(_modeIndex, variants.Count - 1)
                    : 0;

            for (int i = start; i < variants.Count; i++)
            {
                try
                {
                    await Send(settings, url, messages, variants[i], onChunk, token);
                    RememberMode(key, i, variants[i]);
                    return;
                }
                catch (ApiException ex) when ((ex.StatusCode == 400 || ex.StatusCode == 422) &&
                                              i < variants.Count - 1)
                {
                    Log.Warning($"The API rejected the request ({ex.Message}); trying a simpler variant");
                }
            }
        }

        private static void RememberMode(string key, int index, Variant variant)
        {
            lock (ModeGate)
            {
                if (string.Equals(_modeKey, key, StringComparison.Ordinal) && _modeIndex == index)
                    return;

                _modeKey = key;
                _modeIndex = index;
                Log.Info($"Request mode: streaming={variant.Streaming}, minimal={variant.MinimalBody}");
            }
        }

        private static async Task Send(
            AppSettings settings,
            string url,
            List<ChatMessage> messages,
            Variant variant,
            Action<string> onChunk,
            CancellationToken token)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = settings.Model,
                ["messages"] = messages
            };
            if (!variant.MinimalBody)
            {
                body["temperature"] = settings.Temperature;
                body["max_tokens"] = 256;
            }
            if (variant.Streaming)
                body["stream"] = true;

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(token);
                throw new ApiException((int)response.StatusCode, errorBody);
            }

            if (variant.Streaming)
            {
                await ReadStream(response, onChunk, token);
                return;
            }

            string bodyText = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(bodyText);
            string content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
            if (content.Length > 0)
                onChunk(content);
        }

        private static async Task ReadStream(
            HttpResponseMessage response, Action<string> onChunk, CancellationToken token)
        {
            Stream stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);

            while (true)
            {
                string? raw = await reader.ReadLineAsync(token);
                if (raw == null)
                    break;

                raw = raw.TrimStart();
                if (!raw.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = raw[5..].Trim();
                if (data == "[DONE]")
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array ||
                        choices.GetArrayLength() == 0)
                        continue;

                    var choice = choices[0];
                    string? delta = null;

                    if (choice.TryGetProperty("delta", out var deltaObj) &&
                        deltaObj.ValueKind == JsonValueKind.Object &&
                        deltaObj.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                        delta = content.GetString();

                    if (delta == null && choice.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Object &&
                        message.TryGetProperty("content", out var content2) &&
                        content2.ValueKind == JsonValueKind.String)
                        delta = content2.GetString();

                    if (!string.IsNullOrEmpty(delta))
                        onChunk(delta);
                }
                catch (JsonException)
                {
                }
            }
        }

        private static string BuildSystemPrompt(AppSettings settings)
        {
            string language = LanguageNames.Names.TryGetValue(settings.TargetLanguage, out var name)
                ? name
                : settings.TargetLanguage;

            string prompt = string.IsNullOrWhiteSpace(settings.Prompt)
                ? AppSettings.DefaultPrompt
                : settings.Prompt;

            if (prompt.Contains("{0}"))
            {
                try
                {
                    prompt = string.Format(prompt, language);
                }
                catch
                {
                    prompt = prompt.Replace("{0}", language);
                }
            }
            else
            {
                prompt = prompt.TrimEnd() +
                         string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "\nTarget language: {0}.", language);
            }

            return prompt;
        }

        public static class LanguageNames
        {
            public static readonly Dictionary<string, string> Names = new()
            {
                { "ru-RU", "Russian" },
                { "ru", "Russian" },
                { "en-US", "English" },
                { "en-GB", "English" },
                { "en", "English" },
                { "zh-CN", "Chinese (Simplified)" },
                { "zh-TW", "Chinese (Traditional)" },
                { "de-DE", "German" },
                { "fr-FR", "French" },
                { "es-ES", "Spanish" },
                { "ja-JP", "Japanese" },
                { "ko-KR", "Korean" },
                { "it-IT", "Italian" },
                { "pt-BR", "Portuguese (Brazil)" },
                { "tr-TR", "Turkish" },
                { "pl-PL", "Polish" },
                { "uk-UA", "Ukrainian" },
                { "ar-SA", "Arabic" },
            };
        }
    }
}
