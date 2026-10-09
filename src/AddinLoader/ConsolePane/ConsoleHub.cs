using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AddinLoader.Core;

namespace AddinLoader.ConsolePane
{
    /// <summary>
    /// Collects everything written with Console.Write/WriteLine and Debug/Trace.Write/WriteLine
    /// so the Console pane can show it.
    /// </summary>
    public static class ConsoleHub
    {
        const int MaxLength = 500_000;
        static readonly StringBuilder Buffer = new StringBuilder();
        static readonly object Sync = new object();
        static bool installed;

        public static event Action<string> TextAppended;
        public static event Action Cleared;

        public static void Install()
        {
            if (installed)
                return;
            installed = true;
            Console.SetOut(new TeeWriter(Console.Out, new HubWriter()));
            Console.SetError(new TeeWriter(Console.Error, new HubWriter()));
            Trace.Listeners.Add(new HubTraceListener());
            Log.Sink = Append;
        }

        public static void Append(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            lock (Sync)
            {
                Buffer.Append(text);
                if (Buffer.Length > MaxLength)
                    Buffer.Remove(0, Buffer.Length - MaxLength);
            }
            TextAppended?.Invoke(text);
        }

        public static string Snapshot()
        {
            lock (Sync)
                return Buffer.ToString();
        }

        public static void Clear()
        {
            lock (Sync)
                Buffer.Clear();
            Cleared?.Invoke();
        }

        sealed class HubWriter : TextWriter
        {
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) => Append(value.ToString());
            public override void Write(string value) => Append(value);
            public override void Write(char[] buffer, int index, int count) => Append(new string(buffer, index, count));
        }

        sealed class TeeWriter : TextWriter
        {
            readonly TextWriter first;
            readonly TextWriter second;

            public TeeWriter(TextWriter first, TextWriter second)
            {
                this.first = first ?? Null;
                this.second = second;
            }

            public override Encoding Encoding => first.Encoding ?? Encoding.UTF8;
            public override void Write(char value) { first.Write(value); second.Write(value); }
            public override void Write(string value) { first.Write(value); second.Write(value); }
            public override void Write(char[] buffer, int index, int count) { first.Write(buffer, index, count); second.Write(buffer, index, count); }
            public override void Flush() { first.Flush(); second.Flush(); }
        }

        sealed class HubTraceListener : TraceListener
        {
            public HubTraceListener() : base("AddinLoader") { }
            public override void Write(string message) => Append(message);
            public override void WriteLine(string message) => Append(message + Environment.NewLine);
        }
    }
}
