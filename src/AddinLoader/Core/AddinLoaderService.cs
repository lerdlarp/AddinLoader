using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AddinLoader.Core
{
    /// <summary>
    /// Keeps the list of add-ins, loads/reloads them, starts their IExternalApplication classes,
    /// runs their IExternalCommand classes and reloads automatically after each build.
    /// </summary>
    public static class AddinLoaderService
    {
        static readonly TimeSpan BuildQuietTime = TimeSpan.FromMilliseconds(1500);
        static readonly List<AddinEntry> entries = new List<AddinEntry>();
        static readonly List<AddinEntry> pendingReloads = new List<AddinEntry>();
        static ExternalEvent reloadEvent;

        public static UIControlledApplication Application { get; private set; }
        public static string RevitVersion { get; private set; }
        public static LoaderSettings Settings { get; private set; }
        public static IReadOnlyList<AddinEntry> Entries => entries;

        /// <summary>Raised after the list or the state of an entry changed.</summary>
        public static event Action Changed;

        public static void Initialize(UIControlledApplication application)
        {
            Application = application;
            RevitVersion = application.ControlledApplication.VersionNumber;
            Settings = LoaderSettings.Load(RevitVersion);
            reloadEvent = ExternalEvent.Create(new ReloadHandler());

            try { ShadowLoader.CleanUp(LoaderSettings.ShadowRoot); }
            catch (Exception ex) { Log.Error("Shadow folder clean-up failed.", ex); }

            foreach (var path in Settings.Assemblies.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                var entry = new AddinEntry(path);
                entries.Add(entry);
                entry.StartWatching();
                if (entry.IsBuilt)
                    TryLoad(entry);
            }

            application.Idling += OnIdling;
        }

        public static void Shutdown()
        {
            if (Application != null)
                Application.Idling -= OnIdling;
            reloadEvent?.Dispose();
            reloadEvent = null;
            pendingReloads.Clear();
            foreach (var entry in entries)
            {
                StopApplications(entry);
                entry.Dispose();
            }
            Settings?.Save(RevitVersion);
        }

        /// <summary>Adds an add-in assembly (or every assembly of a .addin manifest) and loads it.</summary>
        public static IList<AddinEntry> Add(string path)
        {
            var paths = Path.GetExtension(path).Equals(".addin", StringComparison.OrdinalIgnoreCase)
                ? AddinManifest.ReadAssemblies(path)
                : new List<string> { path };

            var added = new List<AddinEntry>();
            foreach (var assemblyPath in paths.Select(Path.GetFullPath))
            {
                var entry = entries.FirstOrDefault(e => string.Equals(e.SourcePath, assemblyPath, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new AddinEntry(assemblyPath);
                    entries.Add(entry);
                    Settings.Assemblies.Add(entry.SourcePath);
                    entry.StartWatching();
                }
                if (entry.IsBuilt)
                    TryLoad(entry);
                added.Add(entry);
            }
            Settings.Save(RevitVersion);
            OnChanged();
            return added;
        }

        public static void Remove(AddinEntry entry)
        {
            StopApplications(entry);
            entry.Dispose();
            entries.Remove(entry);
            Settings.Assemblies.RemoveAll(p => string.Equals(p, entry.SourcePath, StringComparison.OrdinalIgnoreCase));
            Settings.Save(RevitVersion);
            Log.Info($"Removed {entry.Name}.");
            OnChanged();
        }

        /// <summary>Loads the newest build: stops the running applications, loads, then starts them again.</summary>
        public static bool TryLoad(AddinEntry entry)
        {
            try
            {
                Load(entry);
                return true;
            }
            catch (Exception ex)
            {
                entry.LastError = ex.Message;
                Log.Error($"Failed to load {entry.SourcePath}", ex);
                return false;
            }
            finally
            {
                OnChanged();
            }
        }

        static void Load(AddinEntry entry)
        {
            var stamp = entry.ComputeStamp();
            var result = ShadowLoader.Load(entry.SourcePath, LoaderSettings.ShadowRoot);

            StopApplications(entry);
            ClearWpfResourceCache();

            entry.Current = result;
            entry.LoadedAt = DateTime.Now;
            entry.LoadedStamp = stamp;
            entry.LastError = null;
            entry.ReadTypes(result.Assembly);
            if (entry.WatchingParentFolder)
                entry.StartWatching();

            Log.Info($"Loaded {entry.Name} (r{result.Generation}): {entry.CommandTypes.Count} command(s), {entry.ApplicationTypes.Count} application(s).");
            StartApplications(entry);
        }

        static void StartApplications(AddinEntry entry)
        {
            if (entry.ApplicationTypes.Count == 0)
                return;

            var tracker = RibbonTracker.Begin(Application);
            try
            {
                foreach (var type in entry.ApplicationTypes)
                {
                    try
                    {
                        var application = (IExternalApplication)Activator.CreateInstance(type);
                        var result = application.OnStartup(Application);
                        if (result == Result.Succeeded)
                        {
                            entry.RunningApplications.Add(application);
                            Log.Info($"Started {type.FullName}.");
                        }
                        else
                        {
                            Log.Info($"{type.FullName}.OnStartup returned {result}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"{type.FullName}.OnStartup failed", ex);
                    }
                }
            }
            finally
            {
                tracker.End();
                entry.Ribbon = tracker;
            }
        }

        static void StopApplications(AddinEntry entry)
        {
            foreach (var application in entry.RunningApplications)
            {
                try
                {
                    application.OnShutdown(Application);
                    Log.Info($"Stopped {application.GetType().FullName}.");
                }
                catch (Exception ex)
                {
                    Log.Error($"{application.GetType().FullName}.OnShutdown failed", ex);
                }
            }
            entry.RunningApplications.Clear();
            entry.Ribbon?.Revert();
            entry.Ribbon = null;
        }

        /// <summary>
        /// Runs a command of an add-in. The newest build is loaded first, so every build made
        /// in Visual Studio is used right away.
        /// </summary>
        public static Result Execute(AddinEntry entry, string commandType, ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!entry.IsLoaded || entry.IsStale)
            {
                if (!TryLoad(entry))
                {
                    message = $"Could not load {entry.SourcePath}: {entry.LastError}";
                    return Result.Failed;
                }
            }

            var type = entry.Current.Assembly.GetType(commandType);
            if (type == null)
            {
                message = $"Command '{commandType}' was not found in {entry.Name}.";
                return Result.Failed;
            }

            Settings.LastCommandAssembly = entry.SourcePath;
            Settings.LastCommandType = commandType;
            Settings.Save(RevitVersion);

            Log.Info($"Execute {type.FullName} (r{entry.Current.Generation})");
            try
            {
                var command = (IExternalCommand)Activator.CreateInstance(type);
                var result = command.Execute(commandData, ref message, elements);
                if (result != Result.Succeeded)
                    Log.Info($"{type.Name} returned {result}{(string.IsNullOrEmpty(message) ? "" : ": " + message)}");
                return result;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Log.Error($"{type.FullName} threw an exception", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Runs the last command again ("Application Loader (Faceless)").</summary>
        public static Result ExecuteLast(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (string.IsNullOrEmpty(Settings.LastCommandAssembly) || string.IsNullOrEmpty(Settings.LastCommandType))
            {
                message = "No command has been executed yet. Use 'Application Loader' first.";
                return Result.Failed;
            }

            var entry = entries.FirstOrDefault(e => string.Equals(e.SourcePath, Settings.LastCommandAssembly, StringComparison.OrdinalIgnoreCase))
                        ?? Add(Settings.LastCommandAssembly).FirstOrDefault();
            if (entry == null)
            {
                message = $"Add-in not found: {Settings.LastCommandAssembly}";
                return Result.Failed;
            }
            return Execute(entry, Settings.LastCommandType, commandData, ref message, elements);
        }

        public static void SetAutoReload(bool enabled)
        {
            Settings.AutoReload = enabled;
            Settings.Save(RevitVersion);
        }

        /// <summary>Detects completed builds; lifecycle callbacks run later in an ExternalEvent.</summary>
        static void OnIdling(object sender, IdlingEventArgs e)
        {
            foreach (var entry in entries.ToList())
            {
                if (!entry.TakeSettledChange(BuildQuietTime) || !Settings.AutoReload)
                    continue;
                if (!entry.IsBuilt || (entry.IsLoaded && !entry.IsStale))
                    continue;
                if (!pendingReloads.Contains(entry))
                {
                    Log.Info($"New build of {entry.Name} detected, scheduling reload...");
                    pendingReloads.Add(entry);
                }
            }
            if (pendingReloads.Count > 0)
                reloadEvent.Raise();
        }

        sealed class ReloadHandler : IExternalEventHandler
        {
            public string GetName() => "AddinLoader automatic reload";

            public void Execute(UIApplication application)
            {
                var pending = pendingReloads.ToList();
                pendingReloads.Clear();
                foreach (var entry in pending)
                    if (Settings.AutoReload && entries.Contains(entry) && entry.IsBuilt && (!entry.IsLoaded || entry.IsStale))
                        TryLoad(entry);
            }
        }

        /// <summary>
        /// WPF caches resource managers per assembly name. Clear the cache so XAML of the new
        /// build is used instead of the first one.
        /// </summary>
        static void ClearWpfResourceCache()
        {
            try
            {
                var type = typeof(System.Windows.Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer");
                var field = type?.GetField("_registeredResourceManagers", BindingFlags.Static | BindingFlags.NonPublic);
                (field?.GetValue(null) as IDictionary)?.Clear();
            }
            catch
            {
                // best effort
            }
        }

        static void OnChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("Change notification failed", ex); }
        }
    }
}
