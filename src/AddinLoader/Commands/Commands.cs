using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AddinLoader.ConsolePane;
using AddinLoader.Core;
using AddinLoader.UI;

namespace AddinLoader.Commands
{
    /// <summary>Opens the Application Loader; the chosen command runs inside this command's context.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ApplicationLoaderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var window = new ApplicationLoaderWindow(commandData.Application.MainWindowHandle);
            if (window.ShowDialog() != true || window.SelectedEntry == null)
                return Result.Cancelled;
            return AddinLoaderService.Execute(window.SelectedEntry, window.SelectedCommand, commandData, ref message, elements);
        }
    }

    /// <summary>Executes the last command used in the Application Loader, with the newest build.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ApplicationLoaderFacelessCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return AddinLoaderService.ExecuteLast(commandData, ref message, elements);
        }
    }

    /// <summary>Shows or hides the Console pane.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ConsoleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var pane = commandData.Application.GetDockablePane(ConsolePaneProvider.PaneId);
            if (pane.IsShown())
                pane.Hide();
            else
                pane.Show();
            return Result.Succeeded;
        }
    }

    /// <summary>Reloads the newest build of every add-in in the list (applications are restarted).</summary>
    [Transaction(TransactionMode.Manual)]
    public class ReloadAllCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var reloaded = 0;
            foreach (var entry in AddinLoaderService.Entries)
            {
                if (entry.IsBuilt && AddinLoaderService.TryLoad(entry))
                    reloaded++;
            }
            Log.Info($"Reloaded {reloaded} of {AddinLoaderService.Entries.Count} add-in(s).");
            return Result.Succeeded;
        }
    }

    /// <summary>All AddinLoader buttons work with or without an open document.</summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
