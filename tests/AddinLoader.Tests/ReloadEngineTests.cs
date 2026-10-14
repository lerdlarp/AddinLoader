using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AddinLoader.Core;
using Mono.Cecil;
using Xunit;

namespace AddinLoader.Tests
{
    public sealed class ReloadEngineTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "AddinLoaderTests", Guid.NewGuid().ToString("N"));
        string Build => Path.Combine(root, "bin", "Debug");
        string Shadow => Path.Combine(root, "shadow");

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
        }

        static string Call(Assembly assembly, string typeName, string method) =>
            (string)assembly.GetType(typeName, throwOnError: true).GetMethod(method).Invoke(null, null);

        [Fact]
        public void Rewrite_renames_assembly_and_everything_that_refers_to_it()
        {
            TestAssemblies.Build(Build, "Alpha", "AlphaLib", 1);
            var renames = new Dictionary<string, string> { ["Alpha"] = "Alpha.r7", ["AlphaLib"] = "AlphaLib.r7" };
            Directory.CreateDirectory(Shadow);

            var output = AssemblyRewriter.Rewrite(Path.Combine(Build, "Alpha.dll"), Shadow, renames);

            Assert.Equal(Path.Combine(Shadow, "Alpha.r7.dll"), output);
            using var rewritten = AssemblyDefinition.ReadAssembly(output);
            Assert.Equal("Alpha.r7", rewritten.Name.Name);
            Assert.Contains(rewritten.MainModule.AssemblyReferences, r => r.Name == "AlphaLib.r7");
            Assert.DoesNotContain(rewritten.MainModule.AssemblyReferences, r => r.Name == "AlphaLib");

            var ivt = rewritten.CustomAttributes.Single(a => a.AttributeType.Name == "InternalsVisibleToAttribute");
            Assert.Equal("AlphaLib.r7", ivt.ConstructorArguments[0].Value);

            var resources = rewritten.MainModule.Resources.Select(r => r.Name).ToList();
            Assert.Contains("Alpha.g.resources", resources);
            Assert.Contains("Alpha.r7.g.resources", resources);

            var strings = rewritten.MainModule.GetType("Alpha.Probe").Methods
                .ToDictionary(m => m.Name, m => m.Body.Instructions.First().Operand as string);
            Assert.Equal("/Alpha.r7;component/view.xaml", strings["Uri"]);
            Assert.Equal("Alpha.Probe, Alpha.r7", strings["QualifiedType"]);
            Assert.Equal("Hello, Alpha", strings["Message"]); // ordinary text is left alone
        }

        [Theory]
        [InlineData("/MyAddin;component/Views/MainWindow.xaml", "/MyAddin.r2;component/Views/MainWindow.xaml")]
        [InlineData("pack://application:,,,/MyAddin;component/Resources/icon.png", "pack://application:,,,/MyAddin.r2;component/Resources/icon.png")]
        [InlineData("/MyAddin;v1.0.0.0;component/a.xaml", "/MyAddin.r2;v1.0.0.0;component/a.xaml")]
        [InlineData("MyAddin.Commands.Command, MyAddin", "MyAddin.Commands.Command, MyAddin.r2")]
        [InlineData("MyAddin.Commands.Command, MyAddin, Version=1.0.0.0, Culture=neutral", "MyAddin.Commands.Command, MyAddin.r2, Version=1.0.0.0, Culture=neutral")]
        [InlineData("/MyAddinExtra;component/a.xaml", "/MyAddinExtra;component/a.xaml")]
        [InlineData("/OtherMyAddin;component/a.xaml", "/OtherMyAddin;component/a.xaml")]
        [InlineData("MyAddin", "MyAddin")]
        [InlineData("myaddin;", "MyAddin.r2;")]
        [InlineData("MyAddinExtra;", "MyAddinExtra;")]
        [InlineData("Hello, MyAddin;", "Hello, MyAddin;")]
        [InlineData("Hello, MyAddin users", "Hello, MyAddin users")]
        public void PatchString_only_touches_assembly_references(string input, string expected)
        {
            var rules = new[] { new AssemblyRewriter.StringRule("MyAddin", "MyAddin.r2") };
            Assert.Equal(expected, AssemblyRewriter.PatchString(input, rules));
        }

        [Fact]
        public void New_builds_load_side_by_side_without_locking_the_build_output()
        {
            TestAssemblies.Build(Build, "Beta", "BetaLib", 1);
            var first = ShadowLoader.Load(Path.Combine(Build, "Beta.dll"), Shadow);

            Assert.Equal("main-v1", Call(first.Assembly, "Beta.Probe", "Value"));
            Assert.Equal("lib-v1", Call(first.Assembly, "Beta.Probe", "LibValue"));
            Assert.Equal($"/Beta.r{first.Generation};component/view.xaml", Call(first.Assembly, "Beta.Probe", "Uri"));
            Assert.True(Call(first.Assembly, "Beta.Probe", "Uri").Contains(
                Call(first.Assembly, "Beta.Probe", "ResourceNamespace"), StringComparison.OrdinalIgnoreCase));

            // "Visual Studio" overwrites the build output while the first build is loaded.
            TestAssemblies.Build(Build, "Beta", "BetaLib", 2);
            var second = ShadowLoader.Load(Path.Combine(Build, "Beta.dll"), Shadow);

            Assert.NotSame(first.Assembly, second.Assembly);
            Assert.Equal("main-v2", Call(second.Assembly, "Beta.Probe", "Value"));
            Assert.Equal("lib-v2", Call(second.Assembly, "Beta.Probe", "LibValue"));
            Assert.True(Call(second.Assembly, "Beta.Probe", "Uri").Contains(
                Call(second.Assembly, "Beta.Probe", "ResourceNamespace"), StringComparison.OrdinalIgnoreCase));
            Assert.False(Call(second.Assembly, "Beta.Probe", "Uri").Contains(
                Call(first.Assembly, "Beta.Probe", "ResourceNamespace"), StringComparison.OrdinalIgnoreCase));
            Assert.Equal("main-v1", Call(first.Assembly, "Beta.Probe", "Value"));
            Assert.Equal("lib-v1", Call(first.Assembly, "Beta.Probe", "LibValue"));
            Assert.StartsWith(Shadow, second.Assembly.Location);
        }

        [Fact]
        public void Requests_by_original_name_resolve_to_newest_build()
        {
            TestAssemblies.Build(Build, "Gamma", "GammaLib", 1);
            ShadowLoader.Load(Path.Combine(Build, "Gamma.dll"), Shadow);
            TestAssemblies.Build(Build, "Gamma", "GammaLib", 2);
            var newest = ShadowLoader.Load(Path.Combine(Build, "Gamma.dll"), Shadow);

            var resolved = Assembly.Load(new AssemblyName("Gamma"));
            Assert.Same(newest.Assembly, resolved);

            var type = Type.GetType("Gamma.Probe, Gamma", throwOnError: true);
            Assert.Equal("main-v2", type.GetMethod("Value").Invoke(null, null));
        }

        [Fact]
        public void Manifest_assemblies_are_resolved_relative_to_the_manifest()
        {
            Directory.CreateDirectory(root);
            var manifest = Path.Combine(root, "My.addin");
            File.WriteAllText(manifest, @"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
  <AddIn Type=""Application""><Assembly>bin\My.dll</Assembly></AddIn>
  <AddIn Type=""Command""><Assembly>bin\My.dll</Assembly></AddIn>
  <AddIn Type=""Command""><Assembly>Other.dll</Assembly></AddIn>
</RevitAddIns>".Replace('\\', Path.DirectorySeparatorChar));

            var assemblies = AddinManifest.ReadAssemblies(manifest);

            Assert.Equal(new[] { Path.Combine(root, "bin", "My.dll"), Path.Combine(root, "Other.dll") }, assemblies);
        }

        [Theory]
        [InlineData("2024", "net48")]
        [InlineData("2026", "net8.0-windows")]
        [InlineData("2027", "net10.0-windows7.0")]
        public void Create_writes_a_project_for_the_running_revit_version(string revitVersion, string framework)
        {
            var project = Path.Combine(root, "My Tool", "My Tool.csproj");

            var output = ProjectTemplate.Create(project, revitVersion);

            var directory = Path.Combine(root, "My Tool");
            Assert.Equal(Path.Combine(directory, "bin", "Debug", "My_Tool.dll"), output);
            var csproj = File.ReadAllText(Path.Combine(directory, "My_Tool.csproj"));
            Assert.Contains($"<TargetFramework>{framework}</TargetFramework>", csproj);
            Assert.Contains($"Version=\"{revitVersion}.*\"", csproj);
            Assert.Contains("namespace My_Tool", File.ReadAllText(Path.Combine(directory, "Command.cs")));
            Assert.Contains("IExternalApplication", File.ReadAllText(Path.Combine(directory, "App.cs")));
            Assert.True(File.Exists(Path.Combine(directory, "My_Tool.addin")));
        }

        [Fact]
        public void Serviced_runtime_files_are_found_in_the_newest_folder_of_the_same_release()
        {
            var framework = Path.Combine(root, "dotnet", "shared", "Microsoft.NETCore.App");
            foreach (var version in new[] { "8.0.30", "8.0.32", "8.0.33", "9.0.0" })
            {
                Directory.CreateDirectory(Path.Combine(framework, version));
                File.WriteAllText(Path.Combine(framework, version, "System.Runtime.dll"), "");
            }
            Directory.CreateDirectory(Path.Combine(framework, "8.0.40")); // incomplete install

            // Revit started on 8.0.31, which was removed by an update.
            var missing = Path.Combine(framework, "8.0.31", "System.Runtime.dll");

            Assert.Equal(Path.Combine(framework, "8.0.33", "System.Runtime.dll"), AssemblyRewriter.FindServicedFrameworkFile(missing));
            Assert.Null(AssemblyRewriter.FindServicedFrameworkFile(Path.Combine(framework, "8.0.31", "Missing.dll")));
            Assert.Null(AssemblyRewriter.FindServicedFrameworkFile(Path.Combine(framework, "8.0.30", "System.Runtime.dll"))); // still there
            Assert.Null(AssemblyRewriter.FindServicedFrameworkFile(Path.Combine(root, "nowhere", "8.0.31", "System.Runtime.dll")));
            Assert.Null(AssemblyRewriter.FindServicedFrameworkFile(null));
        }
    }
}
