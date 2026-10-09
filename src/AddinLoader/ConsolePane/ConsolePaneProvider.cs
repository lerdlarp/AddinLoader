using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace AddinLoader.ConsolePane
{
    /// <summary>Registers the dockable Console pane (docked right by default).</summary>
    public sealed class ConsolePaneProvider : IDockablePaneProvider
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("A3E4B5C6-7D8E-4F90-A1B2-C3D4E5F60718"));
        public const string Title = "AddinLoader Console";

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = new ConsoleView();
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }
    }

    /// <summary>Console pane content. F5 clears it.</summary>
    public sealed class ConsoleView : UserControl
    {
        const int MaxLength = 500_000;
        readonly TextBox textBox;
        readonly StringBuilder pending = new StringBuilder();
        readonly object sync = new object();
        bool flushScheduled;

        public ConsoleView()
        {
            textBox = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ToolTip = "Console.WriteLine and Debug.WriteLine output. Press F5 to clear.",
            };

            var clear = new MenuItem { Header = "Clear (F5)" };
            clear.Click += (s, e) => ConsoleHub.Clear();
            var copy = new MenuItem { Header = "Copy" , Command = ApplicationCommands.Copy };
            textBox.ContextMenu = new ContextMenu { Items = { copy, clear } };

            Content = textBox;
            Focusable = true;
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F5)
                {
                    ConsoleHub.Clear();
                    e.Handled = true;
                }
            };

            textBox.Text = ConsoleHub.Snapshot();
            ConsoleHub.TextAppended += OnTextAppended;
            ConsoleHub.Cleared += OnCleared;
        }

        void OnTextAppended(string text)
        {
            lock (sync)
            {
                pending.Append(text);
                if (flushScheduled)
                    return;
                flushScheduled = true;
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Flush));
        }

        void Flush()
        {
            string text;
            lock (sync)
            {
                text = pending.ToString();
                pending.Clear();
                flushScheduled = false;
            }
            textBox.AppendText(text);
            if (textBox.Text.Length > MaxLength)
                textBox.Text = textBox.Text.Substring(textBox.Text.Length - MaxLength);
            textBox.ScrollToEnd();
        }

        void OnCleared()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (sync)
                    pending.Clear();
                textBox.Clear();
            }));
        }
    }
}
