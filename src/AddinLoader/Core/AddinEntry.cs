using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace AddinLoader.Core
{
    /// <summary>One add-in assembly managed by the loader (one row in the Application Loader).</summary>
    public sealed class AddinEntry : IDisposable
    {
        FileSystemWatcher watcher;
        readonly object sync = new object();
        DateTime lastChangeUtc;
        bool changePending;

        public AddinEntry(string sourcePath)
        {
            SourcePath = Path.GetFullPath(sourcePath);
            Name = Path.GetFileNameWithoutExtension(SourcePath);
        }

        public string SourcePath { get; }
        public string Name { get; }

        public ShadowLoadResult Current { get; internal set; }
        public DateTime? LoadedAt { get; internal set; }
        public string LoadedStamp { get; internal set; }
        public string LastError { get; internal set; }

        public IReadOnlyList<Type> CommandTypes { get; internal set; } = Array.Empty<Type>();
        public IReadOnlyList<Type> ApplicationTypes { get; internal set; } = Array.Empty<Type>();

        internal List<IExternalApplication> RunningApplications { get; } = new List<IExternalApplication>();
        internal RibbonTracker Ribbon { get; set; }

        public bool IsLoaded => Current != null;
        public bool IsBuilt => File.Exists(SourcePath);

        /// <summary>True when the build output changed since it was loaded.</summary>
        public bool IsStale => IsLoaded && IsBuilt && ComputeStamp() != LoadedStamp;

        public string Status
        {
            get
            {
                if (!IsBuilt) return "not built yet";
                if (LastError != null && !IsLoaded) return "error";
                if (!IsLoaded) return "not loaded";
                var status = $"r{Current.Generation} loaded {LoadedAt:HH:mm:ss}";
                if (RunningApplications.Count > 0) status += $", {RunningApplications.Count} app(s) running";
                if (IsStale) status += ", new build available";
                return status;
            }
        }

        /// <summary>Signature of the build output folder (newest write time of its assemblies).</summary>
        internal string ComputeStamp()
        {
            var directory = Path.GetDirectoryName(SourcePath);
            if (!Directory.Exists(directory))
                return string.Empty;
            var newest = Directory.GetFiles(directory, "*.dll")
                .Concat(Directory.GetFiles(directory, "*.exe"))
                .Select(f => new FileInfo(f))
                .Select(f => f.LastWriteTimeUtc.Ticks ^ f.Length)
                .DefaultIfEmpty(0)
                .Max();
            return newest.ToString();
        }

        internal void ReadTypes(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
                foreach (var loaderException in ex.LoaderExceptions.Take(5))
                    Log.Info($"{Name}: {loaderException?.Message}");
            }

            bool Creatable(Type t) => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters && t.GetConstructor(Type.EmptyTypes) != null;

            CommandTypes = types.Where(t => Creatable(t) && typeof(IExternalCommand).IsAssignableFrom(t))
                .OrderBy(t => t.FullName).ToList();
            ApplicationTypes = types.Where(t => Creatable(t) && typeof(IExternalApplication).IsAssignableFrom(t))
                .OrderBy(t => t.FullName).ToList();
        }

        /// <summary>Watches the build output folder; changes are picked up on Revit's Idling event.</summary>
        internal void StartWatching()
        {
            StopWatching();
            var directory = Path.GetDirectoryName(SourcePath);
            var watched = directory;
            while (!string.IsNullOrEmpty(watched) && !Directory.Exists(watched))
                watched = Path.GetDirectoryName(watched);
            if (string.IsNullOrEmpty(watched))
                return;

            try
            {
                watcher = new FileSystemWatcher(watched)
                {
                    IncludeSubdirectories = !string.Equals(watched, directory, StringComparison.OrdinalIgnoreCase),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Renamed += OnFileChanged;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Error($"Cannot watch {watched}", ex);
            }
        }

        void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            var extension = Path.GetExtension(e.FullPath);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.Equals(Path.GetDirectoryName(e.FullPath), Path.GetDirectoryName(SourcePath), StringComparison.OrdinalIgnoreCase))
                return;
            lock (sync)
            {
                changePending = true;
                lastChangeUtc = DateTime.UtcNow;
            }
        }

        /// <summary>True while watching a parent folder because the output folder did not exist yet.</summary>
        internal bool WatchingParentFolder => watcher != null && watcher.IncludeSubdirectories;

        /// <summary>True once the build has been quiet for <paramref name="quietTime"/> (the compiler has finished writing).</summary>
        internal bool TakeSettledChange(TimeSpan quietTime)
        {
            lock (sync)
            {
                if (!changePending || DateTime.UtcNow - lastChangeUtc < quietTime)
                    return false;
                changePending = false;
                return true;
            }
        }

        internal void StopWatching()
        {
            if (watcher == null)
                return;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            watcher = null;
        }

        public void Dispose() => StopWatching();
    }
}
