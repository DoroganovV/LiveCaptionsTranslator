using System.Windows;
using LiveCaptionsTranslator.engine;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        public static TranslationEngine Translation { get; private set; } = null!;
        public static CaptionEngine Captions { get; private set; } = null!;

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            models.AppSettings.Current = models.AppSettings.Load();
            utils.Log.Configure(models.AppSettings.Current.LogLevel);
            utils.Log.Info("Startup");

            Translation = new TranslationEngine(Dispatcher);
            Captions = new CaptionEngine(Translation, Dispatcher);

            // Warm up the model before starting the main translation stream.
            Translation.StartWarmUp();
            Captions.Start();
        }

        private void OnDispatcherUnhandledException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            utils.Log.Warning("Unhandled exception: " + e.Exception);
            MessageBox.Show(
                "Unhandled exception:\n" + e.Exception.Message,
                "LiveCaptions Translator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Captions?.Stop();
            base.OnExit(e);
        }
    }
}
