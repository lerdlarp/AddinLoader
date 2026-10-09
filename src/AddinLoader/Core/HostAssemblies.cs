using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AddinLoader.Core
{
    /// <summary>
    /// Decides which assemblies belong to the host (Revit, Autodesk, .NET) and must never be
    /// renamed or loaded from an add-in's output folder, even when "Copy Local" put them there.
    /// </summary>
    public static class HostAssemblies
    {
        static readonly HashSet<string> KnownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RevitAPI", "RevitAPIUI", "RevitAPIIFC", "RevitAPIMacros", "RevitAPIUIMacros", "RevitAddInUtility",
            "RevitNET", "RevitDBAPI", "AdWindows", "UIFramework", "UIFrameworkServices", "UIFrameworkRes",
            "mscorlib", "netstandard", "System", "WindowsBase", "PresentationCore", "PresentationFramework",
            "System.Xaml", "AddinLoader", "Mono.Cecil", "Mono.Cecil.Pdb", "Mono.Cecil.Mdb", "Mono.Cecil.Rocks",
        };

        static readonly string[] KnownPrefixes = { "Autodesk.", "RevitAPI", "UIFramework" };

        public static bool IsHost(string simpleName)
        {
            if (KnownNames.Contains(simpleName) || KnownPrefixes.Any(p => simpleName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || !string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsHostLocation(SafeLocation(assembly)))
                    return true;
            }
            return false;
        }

        static string SafeLocation(System.Reflection.Assembly assembly)
        {
            try { return assembly.Location; }
            catch { return null; }
        }

        static bool IsHostLocation(string location)
        {
            if (string.IsNullOrEmpty(location))
                return false;
            var full = Path.GetFullPath(location);
            return IsUnder(full, AppDomain.CurrentDomain.BaseDirectory)
                   || IsUnder(full, RuntimeEnvironment.GetRuntimeDirectory())
                   || full.IndexOf(@"\assembly\GAC", StringComparison.OrdinalIgnoreCase) >= 0
                   || full.IndexOf(@"\Microsoft.NET\assembly\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsUnder(string path, string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return false;
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
