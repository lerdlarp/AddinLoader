using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SampleAddin
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var document = commandData.Application.ActiveUIDocument?.Document;
            Console.WriteLine($"Console.WriteLine from {GetType().Assembly.GetName().Name}");
            Debug.WriteLine("Debug.WriteLine works too (Debug build).");

            var window = new HelloWindow(document?.Title ?? "no document open");
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
