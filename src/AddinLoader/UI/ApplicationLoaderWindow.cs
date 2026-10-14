using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AddinLoader.Core;
using Microsoft.Win32;

namespace AddinLoader.UI
{
    /// <summary>
    /// "Application Loader" window: load .dll/.addin files, see their commands and applications,
    /// double-click a command to execute it.
    /// </summary>
    public sealed class ApplicationLoaderWindow : Window
    {
        readonly TreeView tree = new TreeView { BorderThickness = new Thickness(1) };
        readonly Button executeButton;
        readonly Button reloadButton;
        readonly Button removeButton;

        /// <summary>The command chosen with Execute / double-click.</summary>
        public AddinEntry SelectedEntry { get; private set; }
        public string SelectedCommand { get; private set; }

        public ApplicationLoaderWindow(IntPtr owner)
        {
            Title = "AddinLoader - Application Loader";
            Width = 600;
            Height = 520;
            MinWidth = 540;
            MinHeight = 300;
            WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(this).Owner = owner;

            var tabs = new TabControl { Margin = new Thickness(8, 8, 8, 4) };
            tabs.Items.Add(new TabItem { Header = "Applications", Content = tree });

            var autoReload = new CheckBox
            {
                Content = "Reload automatically after build",
                IsChecked = AddinLoaderService.Settings.AutoReload,
                Margin = new Thickness(10, 2, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Watch the build output folders and reload the add-in (and its ribbon) as soon as Visual Studio writes a new build.",
            };
            autoReload.Checked += (s, e) => AddinLoaderService.SetAutoReload(true);
            autoReload.Unchecked += (s, e) => AddinLoaderService.SetAutoReload(false);

            // Footer: file actions on the left, actions on the selected item and Close on the right.
            // The auto-reload option sits on its own line so it never overlaps the buttons.
            var fileButtons = new StackPanel { Orientation = Orientation.Horizontal };
            fileButtons.Children.Add(MakeButton("Create", "Create a new Revit add-in project", OnCreate, first: true));
            fileButtons.Children.Add(MakeButton("Load", "Load a .dll or .addin file", OnLoad));

            var itemButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            itemButtons.Children.Add(executeButton = MakeButton("Execute", "Execute the selected command", OnExecute));
            itemButtons.Children.Add(reloadButton = MakeButton("Reload", "Load the newest build of the selected add-in", OnReload));
            itemButtons.Children.Add(removeButton = MakeButton("Remove", "Stop and remove the selected add-in", OnRemove));
            var closeButton = MakeButton("Close", null, (s, e) => Close());
            closeButton.Margin = new Thickness(12, 0, 0, 0);
            itemButtons.Children.Add(closeButton);

            var buttons = new Grid { Margin = new Thickness(8, 0, 8, 8) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(fileButtons, 0);
            Grid.SetColumn(itemButtons, 2);
            buttons.Children.Add(fileButtons);
            buttons.Children.Add(itemButtons);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(autoReload, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(autoReload);
            root.Children.Add(tabs);
            Content = root;

            tree.SelectedItemChanged += (s, e) => UpdateButtons();
            tree.MouseDoubleClick += OnTreeDoubleClick;
            tree.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) OnExecute(s, e);
                if (e.Key == Key.Delete) OnRemove(s, e);
            };

            AddinLoaderService.Changed += Refresh;
            Closed += (s, e) => AddinLoaderService.Changed -= Refresh;
            Refresh();
        }

        Button MakeButton(string text, string tooltip, RoutedEventHandler click, bool first = false)
        {
            var button = new Button { Content = text, MinWidth = 72, Margin = new Thickness(first ? 0 : 4, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3), ToolTip = tooltip };
            button.Click += click;
            return button;
        }

        sealed class CommandNode
        {
            public AddinEntry Entry;
            public string TypeName;
        }

        void Refresh()
        {
            var selectedPath = (SelectedNodeEntry())?.SourcePath;
            var selectedCommand = (tree.SelectedItem as TreeViewItem)?.Tag is CommandNode c ? c.TypeName : null;

            tree.Items.Clear();
            foreach (var entry in AddinLoaderService.Entries)
            {
                var node = new TreeViewItem
                {
                    Header = Header(entry.Name, entry.Status, entry.LastError != null ? Brushes.Firebrick : Brushes.Gray, bold: true),
                    Tag = entry,
                    ToolTip = entry.SourcePath + (entry.LastError != null ? Environment.NewLine + entry.LastError : string.Empty),
                    IsExpanded = true,
                };

                foreach (var type in entry.ApplicationTypes)
                {
                    var running = entry.RunningApplications.Any(a => a.GetType() == type);
                    node.Items.Add(new TreeViewItem
                    {
                        Header = Header("⚙ " + type.FullName, running ? "application, running" : "application", Brushes.Gray),
                        Tag = entry,
                        ToolTip = "IExternalApplication: started automatically, restarted on every reload.",
                    });
                }

                foreach (var type in entry.CommandTypes)
                {
                    var item = new TreeViewItem
                    {
                        Header = Header("▶ " + type.FullName, null, null),
                        Tag = new CommandNode { Entry = entry, TypeName = type.FullName },
                        ToolTip = "IExternalCommand: double-click to execute.",
                    };
                    node.Items.Add(item);
                    if (entry.SourcePath == selectedPath && type.FullName == selectedCommand)
                        item.IsSelected = true;
                }

                if (entry.SourcePath == selectedPath && selectedCommand == null)
                    node.IsSelected = true;
                tree.Items.Add(node);
            }
            UpdateButtons();
        }

        static object Header(string text, string detail, Brush detailBrush, bool bold = false)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
            if (!string.IsNullOrEmpty(detail))
                panel.Children.Add(new TextBlock { Text = "  (" + detail + ")", Foreground = detailBrush ?? Brushes.Gray });
            return panel;
        }

        AddinEntry SelectedNodeEntry()
        {
            switch ((tree.SelectedItem as TreeViewItem)?.Tag)
            {
                case AddinEntry entry: return entry;
                case CommandNode command: return command.Entry;
                default: return null;
            }
        }

        void UpdateButtons()
        {
            var tag = (tree.SelectedItem as TreeViewItem)?.Tag;
            executeButton.IsEnabled = tag is CommandNode;
            reloadButton.IsEnabled = SelectedNodeEntry()?.IsBuilt == true;
            removeButton.IsEnabled = SelectedNodeEntry() != null;
        }

        void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((tree.SelectedItem as TreeViewItem)?.Tag is CommandNode && IsOnSelectedItem(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                OnExecute(sender, e);
            }
        }

        bool IsOnSelectedItem(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            return source == tree.SelectedItem;
        }

        void OnExecute(object sender, RoutedEventArgs e)
        {
            if (!((tree.SelectedItem as TreeViewItem)?.Tag is CommandNode command))
                return;
            SelectedEntry = command.Entry;
            SelectedCommand = command.TypeName;
            DialogResult = true;
        }

        void OnLoad(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load add-in",
                Filter = "Revit add-in (*.dll;*.addin)|*.dll;*.addin|Assembly (*.dll)|*.dll|Add-in manifest (*.addin)|*.addin",
                Multiselect = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;
            RunGuarded(() =>
            {
                foreach (var file in dialog.FileNames)
                    AddinLoaderService.Add(file);
            });
        }

        void OnReload(object sender, RoutedEventArgs e)
        {
            var entry = SelectedNodeEntry();
            if (entry != null)
                RunGuarded(() => AddinLoaderService.TryLoad(entry));
        }

        void OnRemove(object sender, RoutedEventArgs e)
        {
            var entry = SelectedNodeEntry();
            if (entry != null)
                RunGuarded(() => AddinLoaderService.Remove(entry));
        }

        void OnCreate(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Create Revit add-in project",
                FileName = "MyRevitAddin",
                DefaultExt = ".csproj",
                Filter = "C# project (*.csproj)|*.csproj",
                OverwritePrompt = false,
            };
            if (dialog.ShowDialog(this) != true)
                return;

            RunGuarded(() =>
            {
                var output = ProjectTemplate.Create(dialog.FileName, AddinLoaderService.RevitVersion);
                AddinLoaderService.Add(output);
                Log.Info($"Created {dialog.FileName}. Build it (Debug) and the add-in appears here automatically.");
                var open = MessageBox.Show(this,
                    $"Project created:\n{dialog.FileName}\n\nBuild it in Debug and it is loaded automatically from:\n{output}\n\nOpen the project now?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            });
        }

        void RunGuarded(Action action)
        {
            try
            {
                Cursor = Cursors.Wait;
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Application Loader", ex);
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Cursor = null;
                Refresh();
            }
        }
    }
}
