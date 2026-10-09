using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace AddinLoader.Core
{
    /// <summary>Result of loading one build of an add-in.</summary>
    public sealed class ShadowLoadResult
    {
        public string SourcePath { get; internal set; }
        public string ShadowDirectory { get; internal set; }
        public Assembly Assembly { get; internal set; }
        public int Generation { get; internal set; }
        /// <summary>Original simple name -> renamed simple name.</summary>
        public IDictionary<string, string> Renames { get; internal set; }
    }

    /// <summary>
    /// Loads an add-in build without locking its files:
    /// 1. copies the whole output folder into a private "shadow" folder,
    /// 2. gives the add-in's own assemblies a unique identity (<see cref="AssemblyRewriter"/>),
    /// 3. loads the renamed main assembly from the shadow folder.
    /// Visual Studio can then overwrite the original files on the next build while Revit keeps running.
    /// </summary>
    public static class ShadowLoader
    {
        static int generation;
        static readonly object Sync = new object();

        /// <summary>Simple name (original or renamed) -> file of the newest generation.</summary>
        static readonly Dictionary<string, string> LatestFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static bool resolverInstalled;

        public static string ProcessFolderPrefix => Process.GetCurrentProcess().Id + "_";

        public static ShadowLoadResult Load(string sourcePath, string shadowRoot)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Add-in assembly not found. Build the project first.", sourcePath);

            InstallResolver();

            var number = Interlocked.Increment(ref generation);
            var sourceDirectory = Path.GetDirectoryName(sourcePath);
            var mainFileName = Path.GetFileName(sourcePath);
            var shadowDirectory = Path.Combine(shadowRoot, Sanitize(Path.GetFileNameWithoutExtension(sourcePath)), ProcessFolderPrefix + number);

            CopyDirectory(sourceDirectory, shadowDirectory);

            // Decide which assemblies get a new identity.
            var files = Directory.GetFiles(shadowDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(shadowDirectory, "*.exe", SearchOption.TopDirectoryOnly))
                .ToList();
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var toRewrite = new List<string>();
            string mainOriginalName = null;
            foreach (var file in files)
            {
                var isMain = string.Equals(Path.GetFileName(file), mainFileName, StringComparison.OrdinalIgnoreCase);
                var name = TryGetAssemblyName(file);
                if (name == null)
                {
                    if (isMain)
                        throw new BadImageFormatException("The selected file is not a .NET assembly.", sourcePath);
                    continue; // native dll
                }
                if (isMain)
                    mainOriginalName = name.Name;
                else if (!ShouldRename(file, name))
                    continue;
                if (renames.ContainsKey(name.Name))
                    continue;
                renames[name.Name] = $"{name.Name}.r{number}";
                toRewrite.Add(file);
            }

            var rewritten = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in toRewrite)
            {
                var output = AssemblyRewriter.Rewrite(file, shadowDirectory, renames);
                var original = renames.First(r => string.Equals(Path.GetFileNameWithoutExtension(output), r.Value, StringComparison.OrdinalIgnoreCase)).Key;
                rewritten[original] = output;
            }

            lock (Sync)
            {
                foreach (var pair in rewritten)
                {
                    LatestFiles[pair.Key] = pair.Value;
                    LatestFiles[renames[pair.Key]] = pair.Value;
                }
            }

            var assembly = Assembly.LoadFrom(rewritten[mainOriginalName]);
            return new ShadowLoadResult
            {
                SourcePath = sourcePath,
                ShadowDirectory = shadowDirectory,
                Assembly = assembly,
                Generation = number,
                Renames = renames,
            };
        }

        /// <summary>
        /// The add-in's own assemblies are renamed; third-party strong-named packages are shared as-is,
        /// unless they come with a .pdb (then they are most likely projects of the same solution).
        /// </summary>
        static bool ShouldRename(string file, AssemblyName name)
        {
            if (HostAssemblies.IsHost(name.Name))
                return false;
            var token = name.GetPublicKeyToken();
            var signed = token != null && token.Length > 0;
            return !signed || File.Exists(Path.ChangeExtension(file, ".pdb"));
        }

        static AssemblyName TryGetAssemblyName(string file)
        {
            try { return AssemblyName.GetAssemblyName(file); }
            catch (BadImageFormatException) { return null; }
        }

        /// <summary>
        /// Requests for an add-in assembly by its original name (WPF/BAML, Type.GetType, serializers…)
        /// are answered with the newest loaded generation.
        /// </summary>
        static void InstallResolver()
        {
            lock (Sync)
            {
                if (resolverInstalled)
                    return;
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                resolverInstalled = true;
            }
        }

        static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string name;
            try { name = new AssemblyName(args.Name).Name; }
            catch { return null; }

            string file;
            lock (Sync)
            {
                if (!LatestFiles.TryGetValue(name, out file))
                    return null;
            }

            var targetName = Path.GetFileNameWithoutExtension(file);
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .LastOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, targetName, StringComparison.OrdinalIgnoreCase));
            return loaded ?? Assembly.LoadFrom(file);
        }

        static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(source, target));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                CopyWithRetry(file, file.Replace(source, target));
        }

        /// <summary>The build may still be writing the file; retry for a moment.</summary>
        static void CopyWithRetry(string source, string target)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, target, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(200);
                }
            }
        }

        static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>Deletes shadow folders left by Revit sessions that are no longer running.</summary>
        public static void CleanUp(string shadowRoot)
        {
            if (!Directory.Exists(shadowRoot))
                return;
            var running = new HashSet<string>(Process.GetProcesses().Select(p => p.Id + "_"));
            foreach (var addinDirectory in Directory.GetDirectories(shadowRoot))
            {
                foreach (var generationDirectory in Directory.GetDirectories(addinDirectory))
                {
                    var folder = Path.GetFileName(generationDirectory);
                    var underscore = folder.IndexOf('_');
                    if (underscore > 0 && running.Contains(folder.Substring(0, underscore + 1)))
                        continue;
                    TryDelete(generationDirectory);
                }
                if (!Directory.EnumerateFileSystemEntries(addinDirectory).Any())
                    TryDelete(addinDirectory);
            }
        }

        static void TryDelete(string directory)
        {
            try { Directory.Delete(directory, true); }
            catch { /* still in use */ }
        }
    }
}
