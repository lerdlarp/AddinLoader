using System.Reflection;
using Autodesk.Revit.UI;

namespace SampleAddin
{
    /// <summary>Creates a "Sample" tab. AddinLoader removes and recreates it on every reload.</summary>
    public class App : IExternalApplication
    {
        public static int StartCount;

        public Result OnStartup(UIControlledApplication application)
        {
            StartCount++;
            application.CreateRibbonTab("Sample");
            var panel = application.CreateRibbonPanel("Sample", "Sample");
            panel.AddItem(new PushButtonData("SampleCommand", "Hello", Assembly.GetExecutingAssembly().Location, typeof(Command).FullName)
            {
                ToolTip = "Opens a WPF window from the latest build.",
            });
            System.Console.WriteLine("SampleAddin started");
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            System.Console.WriteLine("SampleAddin stopped");
            return Result.Succeeded;
        }
    }
}
