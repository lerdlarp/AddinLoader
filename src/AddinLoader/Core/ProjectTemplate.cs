using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AddinLoader.Core
{
    /// <summary>
    /// "Create" button: writes a ready-to-build Revit add-in project (command + application)
    /// for the running Revit version. Its build output is watched by the loader.
    /// </summary>
    public static class ProjectTemplate
    {
        /// <returns>The path of the assembly the project will build (bin\Debug\Name.dll).</returns>
        public static string Create(string projectFile, string revitVersion)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            var name = ToIdentifier(Path.GetFileNameWithoutExtension(projectFile));
            Directory.CreateDirectory(directory);

            var year = int.TryParse(revitVersion, out var v) ? v : 2024;
            var framework = year >= 2027 ? "net10.0-windows7.0" : year >= 2025 ? "net8.0-windows" : "net48";

            Write(Path.Combine(directory, name + ".csproj"), Csproj(framework, year));
            Write(Path.Combine(directory, "Command.cs"), CommandSource(name));
            Write(Path.Combine(directory, "App.cs"), AppSource(name));
            Write(Path.Combine(directory, name + ".addin"), Manifest(name));

            return Path.Combine(directory, "bin", "Debug", name + ".dll");
        }

        static void Write(string path, string content)
        {
            if (File.Exists(path))
                throw new IOException($"{path} already exists.");
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        static string ToIdentifier(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            var identifier = new string(chars);
            return identifier.Length == 0 || char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
        }

        static string Csproj(string framework, int year) => $@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>{framework}</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>latest</LangVersion>
    <PlatformTarget>x64</PlatformTarget>
    <OutputPath>bin\$(Configuration)\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <DebugType>portable</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Nice3point.Revit.Api.RevitAPI"" Version=""{year}.*"" PrivateAssets=""all"" ExcludeAssets=""runtime"" />
    <PackageReference Include=""Nice3point.Revit.Api.RevitAPIUI"" Version=""{year}.*"" PrivateAssets=""all"" ExcludeAssets=""runtime"" />
  </ItemGroup>

</Project>
";

        static string CommandSource(string name) => $@"using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace {name}
{{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {{
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {{
            var uiapp = commandData.Application;
            Console.WriteLine($""Hello from {name} in Revit {{uiapp.Application.VersionNumber}}"");
            TaskDialog.Show(""{name}"", ""Edit me, build, and run me again without restarting Revit."");
            return Result.Succeeded;
        }}
    }}
}}
";

        static string AppSource(string name) => $@"using System.Reflection;
using Autodesk.Revit.UI;

namespace {name}
{{
    public class App : IExternalApplication
    {{
        public Result OnStartup(UIControlledApplication application)
        {{
            var panel = application.CreateRibbonPanel(""{name}"");
            panel.AddItem(new PushButtonData(""{name}_Command"", ""{name}"",
                Assembly.GetExecutingAssembly().Location, typeof(Command).FullName));
            return Result.Succeeded;
        }}

        public Result OnShutdown(UIControlledApplication application)
        {{
            // Unsubscribe events / unregister updaters here: AddinLoader calls this before every reload.
            return Result.Succeeded;
        }}
    }}
}}
";

        static string Manifest(string name) => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<!-- For deployment only: copy next to {name}.dll into %AppData%\Autodesk\Revit\Addins\<version>.
     While developing, load bin\Debug\{name}.dll with AddinLoader instead. -->
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>{name}</Name>
    <Assembly>{name}.dll</Assembly>
    <AddInId>{Guid.NewGuid().ToString().ToUpperInvariant()}</AddInId>
    <FullClassName>{name}.App</FullClassName>
    <VendorId>{name}</VendorId>
  </AddIn>
</RevitAddIns>
";
    }
}
