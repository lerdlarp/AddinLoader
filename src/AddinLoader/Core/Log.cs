using System;
using System.Diagnostics;
using System.IO;

namespace AddinLoader.Core
{
    /// <summary>
    /// Minimal logger. Messages go to <see cref="Sink"/> (the Console pane inside Revit)
    /// and to the debugger output.
    /// </summary>
    public static class Log
    {
        public static Action<string> Sink { get; set; }
        public static string FilePath { get; private set; }
        static readonly object FileSync = new object();

        public static void StartSession(string revitVersion)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AddinLoader", "Logs");
                Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder, $"Revit{revitVersion}-{Process.GetCurrentProcess().Id}.log");
            }
            catch { /* The in-process console remains available if file logging fails. */ }
        }

        public static void Info(string message) => Write(message);

        public static void Error(string message, Exception exception = null)
        {
            Write(exception == null ? "ERROR: " + message : $"ERROR: {message}{Environment.NewLine}{exception}");
        }

        static void Write(string message)
        {
            var line = $"[AddinLoader {DateTime.Now:HH:mm:ss}] {message}";
            Debugger.Log(0, "AddinLoader", line + Environment.NewLine);
            try
            {
                if (FilePath != null)
                    lock (FileSync)
                        File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
            try { Sink?.Invoke(line + Environment.NewLine); }
            catch { /* logging must never throw */ }
        }
    }
}
