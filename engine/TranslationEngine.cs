using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.engine
{
    public class TranslationEngine
    {
        private readonly object _gate = new();
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<CaptionLine, CancellationTokenSource> _pending = new();
        private readonly Dictionary<CaptionLine, string> _sent = new();
        private readonly Dictionary<CaptionLine, string> _lastSent = new();
        private readonly HashSet<CaptionLine> _completed = new();

        // A queue, not preemption: previously the third line cancelled the first line's request,
        // which came back 150 ms later and cancelled someone else's — the model was hammering
        // cancelled requests while lines were left without translations.
        private readonly SemaphoreSlim _slots;

        public IReadOnlyList<CaptionLine>? LinesSource { get; set; }
        public bool Paused { get; private set; }

        public event Action<CaptionLine>? LineTranslationUpdated;
        public event Action<string>? StatusChanged;

        // Warm-up task: until it finishes, the main requests are not sent,
        // so that repeated cancellations do not kill model loading in LM Studio.
        private Task _warmup = Task.CompletedTask;

        public TranslationEngine(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _slots = new SemaphoreSlim(Math.Max(1, AppSettings.Current.MaxConcurrentRequests));
        }

        public void StartWarmUp()
        {
            var s = AppSettings.Current;
            if (string.IsNullOrWhiteSpace(s.ApiUrl))
            {
                SetStatus("LLM: not configured");
                return;
            }

            SetStatus("LLM: warming up model...");
            _warmup = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                    await OpenAIClient.TranslateAsync(
                        s, new List<ChatMessage>(), "ping", _ => { }, cts.Token);
                    SetStatus("LLM: ready");
                }
                catch (Exception ex)
                {
                    Log.Warning($"Model warm-up failed: {ex.Message}");
                    SetStatus("LLM: " + ex.Message);
                }
            });
        }

        public string LlmStatus { get; private set; } = "LLM: waiting";

        private void SetStatus(string status)
        {
            LlmStatus = status;
            StatusChanged?.Invoke(status);
        }

        public void OnLineChanged(CaptionLine line)
        {
            string text = line.Original;

            lock (_gate)
            {
                if (_pending.TryGetValue(line, out var current))
                {
                    // A live request for the same text (up to punctuation) is already in flight —
                    // do not send it again. A cancelled CTS does not count as live.
                    if (!current.IsCancellationRequested && SameRequest(Get(_sent, line), text))
                        return;

                    SafeCancel(current);
                    _pending.Remove(line);
                }

                if (_completed.Contains(line) && SameRequest(Get(_lastSent, line), text))
                    return;

                if (Paused || string.IsNullOrWhiteSpace(text))
                    return;

                if (!line.IsFinal && text.Length < AppSettings.Current.MinSentenceLength)
                    return;

                var cts = new CancellationTokenSource();
                _pending[line] = cts;
                _sent[line] = text;
                _completed.Remove(line);

                int delay = line.IsFinal ? 0 : AppSettings.Current.DebounceMs;

                // The token is intentionally not passed to Task.Run: if it were already cancelled,
                // the delegate would never execute and the line would remain in _pending forever
                // with an unfinished translation.
                _ = Task.Run(() => SendAsync(line, text, cts, delay));
            }
        }

        private async Task SendAsync(CaptionLine line, string text, CancellationTokenSource cts, int delay)
        {
            try
            {
                await SendCoreAsync(line, text, cts, delay);
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                    Log.Debug($"Translation interrupted: {ex.Message}");
                else
                {
                    Log.Warning($"Translation failed: {ex.Message}");
                    SetError(line, Describe(ex));
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_pending.TryGetValue(line, out var pending) && ReferenceEquals(pending, cts))
                        _pending.Remove(line);
                    _lastSent[line] = text;
                }

                OnUi(() => line.IsTranslating = false);
                cts.Dispose();
            }
        }

        private async Task SendCoreAsync(
            CaptionLine line, string text, CancellationTokenSource cts, int delay)
        {
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            // Wait for the model warm-up to finish (if it is still running),
            // so that cancelling a request does not interrupt model loading.
            try
            {
                await _warmup.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!line.IsFinal && text.Length < AppSettings.Current.MinSentenceLength)
                return;

            try
            {
                await _slots.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunWithRetriesAsync(line, text, cts);
            }
            finally
            {
                _slots.Release();
            }
        }

        private async Task RunWithRetriesAsync(CaptionLine line, string text, CancellationTokenSource cts)
        {
            if (cts.IsCancellationRequested)
                return;

            OnUi(() => line.IsTranslating = true);

            var context = BuildContext(line);
            int attempts = Math.Max(1, AppSettings.Current.RetryCount + 1);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await RunRequestAsync(line, text, context, cts);
                    return;
                }
                catch (OperationCanceledException)
                {
                    if (cts.IsCancellationRequested)
                    {
                        Log.Debug($"Translation cancelled: {text}");
                        return;
                    }

                    // The cancellation is not ours — the request timeout fired.
                    if (attempt >= attempts)
                    {
                        SetError(line, "Request timed out");
                        return;
                    }
                    Log.Warning($"Request timed out, attempt {attempt} of {attempts}");
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (attempt >= attempts)
                    {
                        SetError(line, Describe(ex));
                        return;
                    }
                    Log.Warning($"Request failed ({ex.Message}), attempt {attempt} of {attempts}");
                }

                try
                {
                    await Task.Delay(500 * attempt, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        // A single request attempt. The previous translation is not erased: it stays on screen
        // until the first meaningful chunk of the new one arrives, and is restored on failure.
        private async Task RunRequestAsync(
            CaptionLine line, string text, List<ChatMessage> context, CancellationTokenSource cts)
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            requestCts.CancelAfter(TimeSpan.FromSeconds(
                Math.Max(5, AppSettings.Current.RequestTimeoutSeconds)));

            string previous = line.Translation;
            var buffer = new StringBuilder();
            var sinceFlush = Stopwatch.StartNew();
            bool published = false;

            try
            {
                await OpenAIClient.TranslateAsync(
                    AppSettings.Current,
                    context,
                    text,
                    chunk =>
                    {
                        if (cts.IsCancellationRequested)
                            return;

                        buffer.Append(chunk);
                        if (sinceFlush.ElapsedMilliseconds < AppSettings.Current.StreamFlushMs)
                            return;

                        sinceFlush.Restart();
                        string partial = TextUtil.StripModelNoise(buffer.ToString());
                        if (partial.Length == 0)
                            return;

                        published = true;
                        OnUi(() =>
                        {
                            line.Error = "";
                            line.Translation = partial;
                            LineTranslationUpdated?.Invoke(line);
                        });
                    },
                    requestCts.Token);
            }
            catch
            {
                if (published)
                    OnUi(() => line.Translation = previous);
                throw;
            }

            string cleaned = TextUtil.StripModelNoise(buffer.ToString());

            // An empty result or one noticeably shorter than the existing translation
            // (the model truncated it / spat out garbage) — do not overwrite the previous translation.
            string final = cleaned.Length == 0 || (previous.Length > 0 && cleaned.Length * 3 < previous.Length)
                ? previous
                : cleaned;

            OnUi(() =>
            {
                line.Error = "";
                line.Translation = final;
                line.IsTranslating = false;
                LineTranslationUpdated?.Invoke(line);
            });

            lock (_gate)
            {
                _completed.Add(line);
                _lastSent[line] = text;
            }
        }

        private List<ChatMessage> BuildContext(CaptionLine line)
        {
            var result = new List<ChatMessage>();
            int count = Math.Clamp(AppSettings.Current.ContextCount, 0, 2);
            if (count <= 0 || LinesSource == null)
                return result;

            int index = -1;
            for (int i = 0; i < LinesSource.Count; i++)
            {
                if (ReferenceEquals(LinesSource[i], line))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                return result;

            var candidates = new List<CaptionLine>();
            for (int i = index - 1; i >= 0 && candidates.Count < count; i--)
            {
                var candidate = LinesSource[i];
                if (candidate.IsFinal && candidate.HasTranslation)
                    candidates.Add(candidate);
            }

            candidates.Reverse();
            foreach (var candidate in candidates)
            {
                result.Add(new ChatMessage { role = "user", content = $"🔤 {candidate.Original} 🔤" });
                result.Add(new ChatMessage { role = "assistant", content = candidate.Translation });
            }

            return result;
        }

        public void SetPaused(bool paused)
        {
            List<CaptionLine>? toRetrigger = null;

            lock (_gate)
            {
                Paused = paused;

                if (paused)
                {
                    foreach (var cts in _pending.Values)
                        SafeCancel(cts);
                    _pending.Clear();                }
                else if (LinesSource != null)
                {
                    toRetrigger = new List<CaptionLine>();
                    foreach (var line in LinesSource)
                    {
                        bool valid = _completed.Contains(line) &&
                                     SameRequest(Get(_lastSent, line), line.Original) &&
                                     line.HasTranslation;
                        if (!valid)
                            toRetrigger.Add(line);
                    }
                }
            }

            if (toRetrigger != null)
                foreach (var line in toRetrigger)
                    OnLineChanged(line);

            SetStatus(paused ? "LLM: paused" : "LLM: waiting");
        }

        public void Cancel(CaptionLine line)
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(line, out var cts))
                {
                    SafeCancel(cts);
                    _pending.Remove(line);
                }
                _sent.Remove(line);
                _lastSent.Remove(line);
                _completed.Remove(line);
            }
        }

        public void CancelAll()
        {
            lock (_gate)
            {
                foreach (var cts in _pending.Values)
                    SafeCancel(cts);
                _pending.Clear();



                _sent.Clear();
                _lastSent.Clear();
                _completed.Clear();
            }
        }

        private void SetError(CaptionLine line, string message)
        {
            OnUi(() =>
            {
                line.Error = message;
                line.IsTranslating = false;
                LineTranslationUpdated?.Invoke(line);
            });
        }

        // The owner of the CTS disposes it right after the request, while another thread may
        // cancel it via a reference captured a moment earlier.
        private static void SafeCancel(CancellationTokenSource cts)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static bool SameRequest(string? a, string b) =>
            a != null && string.Equals(
                TextUtil.NormalizeForMatch(a), TextUtil.NormalizeForMatch(b), StringComparison.Ordinal);

        private static string? Get(Dictionary<CaptionLine, string> map, CaptionLine line) =>
            map.TryGetValue(line, out var value) ? value : null;

        private static bool IsTransient(Exception ex) =>
            ex is HttpRequestException or IOException ||
            (ex is ApiException api && (api.StatusCode == 429 || api.StatusCode >= 500));

        private static string Describe(Exception ex) =>
            ex is ApiException api && api.Body.Length > 0
                ? $"{api.Message}: {api.Body[..Math.Min(200, api.Body.Length)]}"
                : ex.Message;

        private void OnUi(Action action)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return;

            try
            {
                _dispatcher.InvokeAsync(action);
            }
            catch
            {
            }
        }
    }
}
