using System.IO;
using System.Text;

namespace LiveCaptionsTranslator.utils
{
    public enum LogLevel
    {
        Off = 0,
        Warning = 1,
        Info = 2,
        Debug = 3
    }

    public static class Log
    {
        private static readonly object Gate = new();
        private static LogLevel _level = LogLevel.Off;
        private static string _path = "";

        public static void Configure(LogLevel level)
        {
            lock (Gate)
            {
                _level = level;
                _path = "";
                if (level == LogLevel.Off)
                    return;

                try
                {
                    string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, $"translator-{DateTime.Now:yyyyMMdd}.log");
                }
                catch
                {
                }
            }
        }

        public static void Warning(string message) => Write(LogLevel.Warning, message);

        public static void Info(string message) => Write(LogLevel.Info, message);

        public static void Debug(string message) => Write(LogLevel.Debug, message);

        private static void Write(LogLevel level, string message)
        {
            if (level > _level)
                return;

            lock (Gate)
            {
                if (level > _level || _path.Length == 0)
                    return;

                try
                {
                    File.AppendAllText(
                        _path,
                        $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch
                {
                }
            }
        }
    }
}
