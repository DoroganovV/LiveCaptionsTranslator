using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.engine
{
    public class CaptionEngine
    {
        private readonly LiveCaptionsService _live = new();
        private readonly TranslationEngine _translation;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _cts;
        private Thread? _thread;
        private string _lastFullText = "";
        private string _clearedPrefix = "";

        private sealed record Snapshot(string[] Finalized, string Active);

        private Snapshot? _latest;
        private int _dispatchQueued;

        // Suppresses translation cancellation when lines are reordered within the list
        // (line references are preserved, so there is no need to cancel translations).
        private bool _suspendLineCancels = false;

        public ObservableCollection<CaptionLine> Lines { get; } = new();
        public TranslationEngine Translation => _translation;

        public event Action? LinesChanged;
        public event Action<string>? StatusChanged;

        public CaptionEngine(TranslationEngine translation, Dispatcher dispatcher)
        {
            _translation = translation;
            _dispatcher = dispatcher;
            translation.LinesSource = Lines;
            Lines.CollectionChanged += OnLinesCollectionChanged;
        }

        // A dedicated thread instead of the pool: polling UI Automation is a blocking
        // cross-process call, and tying up a pooled thread with it would stall the rest of the app.
        public void Start()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _thread = new Thread(() => PollLoop(token))
            {
                IsBackground = true,
                Name = "LiveCaptionsPoll"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _cts?.Cancel();
            _translation.CancelAll();
            _live.Stop();
        }

        public void Clear()
        {
            _translation.CancelAll();
            Lines.Clear();
            _clearedPrefix = _lastFullText;
            LinesChanged?.Invoke();
        }

        public void ShowLiveCaptionsWindow()
        {
            try
            {
                _live.Restore();
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to show the LiveCaptions window: {ex.Message}");
            }
        }

        public bool IsPaused => _translation.Paused;

        public void SetPaused(bool paused)
        {
            _translation.SetPaused(paused);
            StatusChanged?.Invoke(paused ? "Translation: paused" : "Translation: active");
        }

        private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems == null || _suspendLineCancels)
                return;

            foreach (CaptionLine line in e.OldItems)
                _translation.Cancel(line);
        }

        private void PollLoop(CancellationToken token)
        {
            var lastRestart = DateTime.MinValue;
            bool hidden = false;
            bool waitingShown = false;

            try
            {
                _live.EnsureStarted();
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to start LiveCaptions: {ex.Message}");
            }

            while (!token.IsCancellationRequested)
            {
                int interval = Math.Max(20, AppSettings.Current.PollIntervalMs);

                string? full;
                try
                {
                    full = _live.GetTranscript();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to read the transcript: {ex.Message}");
                    full = null;
                }

                if (full == null)
                {
                    // The service is dead — restart it, but no more often than once every 5 seconds,
                    // to avoid a storm of kill/launch cycles.
                    if (DateTime.Now - lastRestart < TimeSpan.FromSeconds(5))
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    lastRestart = DateTime.Now;

                    Log.Warning("LiveCaptions is unavailable, restarting");
                    StatusChanged?.Invoke("LiveCaptions: restarting...");
                    try
                    {
                        _live.Reset();
                        _live.EnsureStarted();
                        _lastFullText = "";
                        _clearedPrefix = "";
                        hidden = false;
                        waitingShown = false;
                        StatusChanged?.Invoke("LiveCaptions: connected");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Restart failed: {ex.Message}");
                        StatusChanged?.Invoke("LiveCaptions: unavailable");
                    }

                    Thread.Sleep(2000);
                    continue;
                }

                if (full.Length == 0)
                {
                    // LiveCaptions is alive, but there are no captions yet
                    // (e.g., the language has not been selected / the "Start" button has not been pressed).
                    if (!waitingShown)
                    {
                        waitingShown = true;
                        StatusChanged?.Invoke("LiveCaptions: waiting for captions...");
                    }
                    Thread.Sleep(interval);
                    continue;
                }

                waitingShown = false;

                // The first real captions have arrived — the LiveCaptions window can be hidden now.
                if (!hidden)
                {
                    hidden = true;
                    _live.Hide();
                }

                string processed = TextUtil.Preprocess(full);

                if (processed.Length > _clearedPrefix.Length &&
                    processed.StartsWith(_clearedPrefix, StringComparison.Ordinal))
                    processed = processed[_clearedPrefix.Length..].TrimStart();
                else if (processed != _clearedPrefix)
                    _clearedPrefix = "";

                if (string.IsNullOrEmpty(processed) || processed == _lastFullText)
                {
                    Thread.Sleep(interval);
                    continue;
                }

                _lastFullText = processed;

                var (finalized, active) = TranscriptParser.Split(processed);
                Publish(new Snapshot(finalized, active));

                Thread.Sleep(interval);
            }
        }

        // We hand the dispatcher not every change but only the latest state: while
        // the previous Reconcile is still running, new ones are not queued.
        private void Publish(Snapshot snapshot)
        {
            Volatile.Write(ref _latest, snapshot);

            if (Interlocked.Exchange(ref _dispatchQueued, 1) != 0)
                return;

            _dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _dispatchQueued, 0);
                var current = Volatile.Read(ref _latest);
                if (current == null)
                    return;

                Reconcile(current.Finalized, current.Active);
                EnforceVisibleLimit();
                RetryMissing();
            });
        }

        private void Reconcile(string[] finalized, string active)
        {
            bool activeIsFinal = active.Length > 0 && TextUtil.IsEos(active[^1]);

            var old = Lines.ToList();
            var used = new bool[old.Count];
            var result = new List<CaptionLine>();

            // Translations of lines that may drop out of the window
            // (in case the same text reappears later).
            var donors = new Dictionary<string, string>();
            foreach (var l in old)
            {
                if (!l.HasTranslation)
                    continue;

                string key = TextUtil.NormalizeForMatch(l.Original);
                if (!donors.ContainsKey(key))
                    donors[key] = l.Translation;
            }

            bool changed = false;

            // Finalized lines — look for an existing line with the same text
            // to preserve the reference (the translation and engine state are not lost).
            for (int i = 0; i < finalized.Length; i++)
            {
                string text = finalized[i];
                int idx = FindMatch(old, used, text);

                if (idx >= 0)
                {
                    used[idx] = true;
                    var line = old[idx];

                    bool textChanged = !string.Equals(line.Original, text, StringComparison.Ordinal);
                    bool becameFinal = !line.IsFinal;

                    if (textChanged)
                        line.Original = text;
                    if (becameFinal)
                    {
                        line.IsFinal = true;
                        line.Error = "";
                    }

                    // Key point: a line that has become finalized must receive a translation —
                    // the length gate was silently filtering out short phrases before this.
                    if (textChanged || becameFinal)
                    {
                        changed = true;
                        _translation.OnLineChanged(line);
                    }

                    result.Add(line);
                }
                else
                {
                    var line = new CaptionLine { Original = text, IsFinal = true };
                    if (donors.TryGetValue(TextUtil.NormalizeForMatch(text), out var t))
                        line.Translation = t;
                    result.Add(line);
                    if (!line.HasTranslation)
                        _translation.OnLineChanged(line);
                    changed = true;
                }
            }

            // The active line — only it gets corrected.
            // Look for the previous active line (the text should "roughly" match),
            // otherwise a line with the same text, otherwise create a new one.
            if (active.Length > 0)
            {
                int idx = -1;
                for (int j = 0; j < old.Count; j++)
                {
                    if (!used[j] && !old[j].IsFinal && LooksLikeCorrection(old[j].Original, active))
                    {
                        idx = j;
                        break;
                    }
                }

                if (idx < 0)
                    idx = FindMatch(old, used, active);

                if (idx >= 0)
                {
                    used[idx] = true;
                    var line = old[idx];
                    bool textChanged = !string.Equals(line.Original, active, StringComparison.Ordinal);
                    bool becameFinal = activeIsFinal && !line.IsFinal;

                    // Set the flag before sending the request: otherwise the length gate
                    // would reject a short phrase that has already become finalized.
                    if (textChanged)
                    {
                        line.Original = active;
                        line.Error = "";
                    }
                    if (becameFinal)
                        line.IsFinal = true;

                    if (textChanged || becameFinal)
                    {
                        _translation.OnLineChanged(line);
                        changed = true;
                    }

                    result.Add(line);
                }
                else
                {
                    var line = new CaptionLine { Original = active, IsFinal = activeIsFinal };
                    if (donors.TryGetValue(TextUtil.NormalizeForMatch(active), out var t))
                        line.Translation = t;
                    result.Add(line);
                    if (!line.HasTranslation)
                        _translation.OnLineChanged(line);
                    changed = true;
                }
            }

            // Remove lines that dropped out of the window (their translations are cancelled).
            for (int j = old.Count - 1; j >= 0; j--)
            {
                if (!used[j])
                {
                    Lines.Remove(old[j]);
                    changed = true;
                }
            }

            // Put the surviving lines in the right order and append the new ones.
            // Reordering must not cancel translations — suppress cancellation for now.
            _suspendLineCancels = true;
            try
            {
                for (int i = 0; i < result.Count; i++)
                {
                    var line = result[i];
                    int pos = Lines.IndexOf(line);
                    if (pos == i)
                        continue;
                    if (pos >= 0)
                        Lines.RemoveAt(pos);
                    if (i < Lines.Count)
                        Lines.Insert(i, line);
                    else
                        Lines.Add(line);
                }
            }
            finally
            {
                _suspendLineCancels = false;
            }

            if (changed)
                LinesChanged?.Invoke();
        }

        // First an exact match, then a match ignoring trailing punctuation:
        // "abc" and "abc." are the same utterance, and the translation must not move to a new line.
        private static int FindMatch(List<CaptionLine> old, bool[] used, string text)
        {
            for (int j = 0; j < old.Count; j++)
            {
                if (!used[j] && string.Equals(old[j].Original, text, StringComparison.Ordinal))
                    return j;
            }

            string key = TextUtil.NormalizeForMatch(text);
            if (key.Length == 0)
                return -1;

            for (int j = 0; j < old.Count; j++)
            {
                if (!used[j] &&
                    string.Equals(TextUtil.NormalizeForMatch(old[j].Original), key, StringComparison.Ordinal))
                    return j;
            }

            return -1;
        }

        // The active line gets "refined" as speech recognition progresses:
        // usually the new text extends the previous one or is a light correction.
        // If there are few characters in common — it is already a new phrase, not a correction.
        private static bool LooksLikeCorrection(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
                return false;

            string longer = a.Length >= b.Length ? a : b;
            string shorter = a.Length >= b.Length ? b : a;

            if (longer.StartsWith(shorter, StringComparison.Ordinal) ||
                longer.EndsWith(shorter, StringComparison.Ordinal))
                return true;

            // Character-by-character match (order does not matter) — for small recognition corrections.
            var remaining = new Dictionary<char, int>();
            foreach (char c in shorter)
                remaining[c] = remaining.GetValueOrDefault(c) + 1;

            int common = 0;
            foreach (char c in longer)
            {
                if (remaining.TryGetValue(c, out int n) && n > 0)
                {
                    common++;
                    if (n == 1)
                        remaining.Remove(c);
                    else
                        remaining[c] = n - 1;

                    if (common * 10 >= shorter.Length * 7)
                        return true;
                }
            }

            return false;
        }

        // Safety net: a finalized line without a translation and without an error re-requests it.
        // The engine itself filters out lines whose request is already in flight or already completed,
        // so this does not create extra traffic.
        private void RetryMissing()
        {
            foreach (var line in Lines)
            {
                if (line.IsFinal && line.IsPending)
                    _translation.OnLineChanged(line);
            }
        }

        private void EnforceVisibleLimit()
        {
            int limit = Math.Max(1, AppSettings.Current.VisibleLines);
            while (Lines.Count > limit)
                Lines.RemoveAt(0);
        }
    }
}
