using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveCaptionsTranslator.engine;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : Window
    {
        private readonly CaptionEngine _engine;
        private bool _nearBottom = true;
        private bool _scrollQueued;

        public MainWindow()
        {
            InitializeComponent();

            _engine = App.Captions;
            LinesItems.ItemsSource = _engine.Lines;

            ContentScroller.ScrollChanged += (_, _) => _nearBottom = IsNearBottom(ContentScroller);

            _engine.LinesChanged += OnLinesChanged;
            _engine.StatusChanged += status => Dispatcher.InvokeAsync(() => StatusText.Text = status);
            _engine.Translation.StatusChanged += status => Dispatcher.InvokeAsync(() => LlmStatusText.Text = status);
            _engine.Translation.LineTranslationUpdated += OnTranslationUpdated;

            Topmost = AppSettings.Current.Topmost;
            UpdateTopmostButton();
            LlmStatusText.Text = _engine.Translation.LlmStatus;
        }

        private void LiveCaptionsButton_Click(object sender, RoutedEventArgs e)
        {
            _engine.ShowLiveCaptionsWindow();
        }

        private void OnLinesChanged()
        {
            if (_nearBottom)
                ScrollToEnd();
        }

        private void OnTranslationUpdated(CaptionLine line)
        {
            if (_nearBottom)
                ScrollToEnd();
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            bool paused = !_engine.IsPaused;
            _engine.SetPaused(paused);
            PauseButton.Content = paused ? "▶ Resume" : "⏸ Pause";
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            AppSettings.Current.Topmost = Topmost;
            AppSettings.Current.Save();
            UpdateTopmostButton();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _engine.Clear();
            ScrollToEnd();
        }

        private void UpdateTopmostButton()
        {
            TopmostButton.Opacity = Topmost ? 1.0 : 0.45;
        }

        private static bool IsNearBottom(ScrollViewer scrollViewer)
            => scrollViewer.ExtentHeight <= scrollViewer.ViewportHeight + scrollViewer.VerticalOffset + 4;

        // Scroll after the layout has been recalculated, and no more than once per batch of updates;
        // otherwise the stream of tokens chews through the layout repeatedly.
        private void ScrollToEnd()
        {
            if (_scrollQueued)
                return;

            _scrollQueued = true;
            Dispatcher.InvokeAsync(() =>
            {
                _scrollQueued = false;
                ContentScroller.ScrollToVerticalOffset(double.MaxValue);
            }, DispatcherPriority.Background);
        }
    }
}
