using System;
using System.Diagnostics;

namespace AddinLoader.Core
{
    /// <summary>
    /// Minimal logger. Messages go to <see cref="Sink"/> (the Console pane inside Revit)
    /// and to the debugger output.
    /// </summary>
    public static class Log
    {
        public static Action<string> Sink { get; set; }

        public static void Info(string message) => Write(message);

        public static void Error(string message, Exception exception = null)
        {
            Write(exception == null ? "ERROR: " + message : $"ERROR: {message}{Environment.NewLine}{exception}");
        }

        static void Write(string message)
        {
            var line = $"[AddinLoader {DateTime.Now:HH:mm:ss}] {message}";
            Debugger.Log(0, "AddinLoader", line + Environment.NewLine);
            try { Sink?.Invoke(line + Environment.NewLine); }
            catch { /* logging must never throw */ }
        }
    }
}
