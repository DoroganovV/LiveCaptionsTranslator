using System.Diagnostics;
using System.Windows.Automation;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.engine
{
    public class LiveCaptionsService
    {
        private AutomationElement? _window;
        private AutomationElement? _captionsTextBlock;

        public event Action<string>? StatusChanged;

        public bool IsAttached => _window != null;

        public void EnsureStarted()
        {
            // Do not kill an already running LiveCaptions: the user may have enabled
            // captions themselves, and a restart would reset the language and the current transcript.
            if (TryAttachRunning())
                return;

            Process? process = null;
            try
            {
                process = Process.Start(ProcessName);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    "Failed to start LiveCaptions (Windows 11, Live Captions). " + ex.Message, ex);
            }

            // No need to hide the window yet: before the first captions appear
            // the user may need to select a language and press "Start".
            for (int attempt = 0; attempt < 300; attempt++)
            {
                Thread.Sleep(100);
                var window = FindWindowByPId(process.Id);
                if (window != null)
                {
                    _window = window;
                    _captionsTextBlock = null;
                    NotifyStatus("LiveCaptions: connected");
                    return;
                }
            }

            throw new Exception(
                "Failed to find the LiveCaptions window. Make sure Windows 11 with Live Captions is installed.");
        }

        private bool TryAttachRunning()
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                try
                {
                    var window = FindWindowByPId(process.Id);
                    if (window == null)
                        continue;

                    _window = window;
                    _captionsTextBlock = null;
                    Log.Info($"Attached to the running LiveCaptions (pid {process.Id})");
                    NotifyStatus("LiveCaptions: connected");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to attach to LiveCaptions: {ex.Message}");
                }
            }

            return false;
        }

        // null  — the service is dead (the window disappeared / the process crashed), a restart is needed.
        // ""    — the service is alive, but there are no captions yet (e.g., the "Start" button has not been pressed).
        // text  — the current transcript.
        public string? GetTranscript()
        {
            if (_window == null)
                return null;

            try
            {
                // Check that the window is still alive.
                _ = _window.Current.ProcessId;
            }
            catch
            {
                _window = null;
                _captionsTextBlock = null;
                return null;
            }

            try
            {
                if (_captionsTextBlock == null)
                    _captionsTextBlock = FindElementByAutomationId(_window, "CaptionsTextBlock");

                return _captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch
            {
                // The window is alive, but the element is temporarily unavailable — wait for it.
                _captionsTextBlock = null;
                return string.Empty;
            }
        }

        public void Hide()
        {
            if (_window == null)
                return;

            try
            {
                nint hWnd = (nint)_window.Current.NativeWindowHandle;
                int exStyle = NativeApi.GetWindowLong(hWnd, NativeApi.GWL_EXSTYLE);
                NativeApi.ShowWindow(hWnd, NativeApi.SW_MINIMIZE);
                NativeApi.SetWindowLong(hWnd, NativeApi.GWL_EXSTYLE, exStyle | NativeApi.WS_EX_TOOLWINDOW);
            }
            catch
            {
            }
        }

        public void Restore()
        {
            if (_window == null)
                return;

            try
            {
                nint hWnd = (nint)_window.Current.NativeWindowHandle;
                int exStyle = NativeApi.GetWindowLong(hWnd, NativeApi.GWL_EXSTYLE);
                NativeApi.SetWindowLong(hWnd, NativeApi.GWL_EXSTYLE, exStyle & ~NativeApi.WS_EX_TOOLWINDOW);
                NativeApi.ShowWindow(hWnd, NativeApi.SW_RESTORE);
                NativeApi.SetForegroundWindow(hWnd);
            }
            catch
            {
            }
        }

        public void Reset()
        {
            _window = null;
            _captionsTextBlock = null;
            KillAllProcesses();
        }

        public void Stop()
        {
            try
            {
                if (_window != null)
                {
                    Restore();
                    KillProcessOf(_window);
                }
            }
            catch
            {
            }

            _window = null;
            _captionsTextBlock = null;
        }

        public const string ProcessName = "LiveCaptions";

        private void KillProcessOf(AutomationElement window)
        {
            try
            {
                nint hWnd = (nint)window.Current.NativeWindowHandle;
                NativeApi.GetWindowThreadProcessId(hWnd, out int processId);
                var process = Process.GetProcessById(processId);
                process.Kill();
                process.WaitForExit(1000);
            }
            catch
            {
            }
        }

        private static void KillAllProcesses()
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
                catch
                {
                }
            }
        }

        private static AutomationElement? FindWindowByPId(int processId)
        {
            try
            {
                var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
                AutomationElement? fallback = null;

                foreach (AutomationElement window in windows)
                {
                    if (string.Equals(window.Current.ClassName, "LiveCaptionsDesktopWindow",
                            StringComparison.Ordinal))
                        return window;
                    fallback ??= window;
                }
                return fallback;
            }
            catch
            {
                return null;
            }
        }

        private static AutomationElement? FindElementByAutomationId(AutomationElement window, string automationId)
        {
            try
            {
                var condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch
            {
                return null;
            }
        }

        private void NotifyStatus(string status)
        {
            StatusChanged?.Invoke(status);
        }
    }
}

