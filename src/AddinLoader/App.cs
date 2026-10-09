using System;
using System.Reflection;
using Autodesk.Revit.UI;
using AddinLoader.Commands;
using AddinLoader.ConsolePane;
using AddinLoader.Core;
using AddinLoader.UI;

namespace AddinLoader
{
    /// <summary>
    /// AddinLoader: load, reload and run Revit add-ins without restarting Revit.
    /// Adds the "AddinLoader" tab with the AddinLoader pulldown:
    /// Application Loader, Application Loader (Faceless), Console (Show/Hide), Reload All.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "AddinLoader";
        public const string PanelName = "AddinLoader";

        public Result OnStartup(UIControlledApplication application)
        {
            ConsoleHub.Install();
            application.RegisterDockablePane(ConsolePaneProvider.PaneId, ConsolePaneProvider.Title, new ConsolePaneProvider());
            CreateRibbon(application);
            Log.Info($"AddinLoader {typeof(App).Assembly.GetName().Version} started in Revit {application.ControlledApplication.VersionNumber}.");

            try
            {
                AddinLoaderService.Initialize(application);
            }
            catch (Exception ex)
            {
                Log.Error("AddinLoader initialization failed", ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            AddinLoaderService.Shutdown();
            return Result.Succeeded;
        }

        static void CreateRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

            var panel = application.CreateRibbonPanel(TabName, PanelName);
            var pulldown = (PulldownButton)panel.AddItem(new PulldownButtonData("AddinLoader", "AddinLoader")
            {
                ToolTip = "Load, reload and run add-ins without restarting Revit.",
                LargeImage = Icons.Loader(32),
                Image = Icons.Loader(16),
            });

            AddButton<ApplicationLoaderCommand>(pulldown, "Application Loader",
                "Load .dll/.addin files and execute their commands. Double-click a command to execute it.", Icons.Loader);
            AddButton<ApplicationLoaderFacelessCommand>(pulldown, "Application Loader (Faceless)",
                "Execute the last command used in the Application Loader again, with the newest build.", Icons.Faceless);
            AddButton<ConsoleCommand>(pulldown, "Console (Show/Hide)",
                "Show or hide the Console pane with Console.WriteLine and Debug.WriteLine output. Press F5 in the pane to clear it.", Icons.Console);
            pulldown.AddSeparator();
            AddButton<ReloadAllCommand>(pulldown, "Reload All",
                "Reload the newest build of every loaded add-in and restart their applications.", Icons.Reload);
        }

        static void AddButton<TCommand>(PulldownButton pulldown, string text, string tooltip, Func<int, System.Windows.Media.ImageSource> icon)
            where TCommand : IExternalCommand
        {
            var data = new PushButtonData(typeof(TCommand).Name, text, Assembly.GetExecutingAssembly().Location, typeof(TCommand).FullName)
            {
                ToolTip = tooltip,
                LargeImage = icon(32),
                Image = icon(16),
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            };
            pulldown.AddPushButton(data);
        }
    }
}
