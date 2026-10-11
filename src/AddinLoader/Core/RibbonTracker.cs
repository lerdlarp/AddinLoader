using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using AdRibbonPanel = Autodesk.Windows.RibbonPanel;
using AdRibbonTab = Autodesk.Windows.RibbonTab;

namespace AddinLoader.Core
{
    /// <summary>
    /// Records the ribbon tabs and panels an add-in creates in <c>OnStartup</c> so they can be
    /// removed again before the next build is started. The Revit API has no "remove panel",
    /// so the visual items are removed from the AdWindows ribbon and Revit's internal name
    /// registries are rolled back (otherwise CreateRibbonPanel would fail with
    /// "panel already exists" on reload).
    /// </summary>
    public sealed class RibbonTracker
    {
        readonly UIControlledApplication application;
        readonly HashSet<AdRibbonTab> tabsBefore;
        readonly Dictionary<AdRibbonTab, HashSet<AdRibbonPanel>> panelsBefore;
        readonly List<RegistrySnapshot> registries;

        readonly List<AdRibbonTab> newTabs = new List<AdRibbonTab>();
        readonly List<KeyValuePair<AdRibbonTab, AdRibbonPanel>> newPanels = new List<KeyValuePair<AdRibbonTab, AdRibbonPanel>>();
        readonly List<RegistryChange> registryChanges = new List<RegistryChange>();

        RibbonTracker(UIControlledApplication application)
        {
            this.application = application;
            var tabs = RibbonTabs().ToList();
            tabsBefore = new HashSet<AdRibbonTab>(tabs);
            panelsBefore = tabs.ToDictionary(t => t, t => new HashSet<AdRibbonPanel>(t.Panels));
            registries = Registries(application).Select(d => new RegistrySnapshot(d)).ToList();
        }

        /// <summary>Takes a snapshot of the ribbon before an add-in starts.</summary>
        public static RibbonTracker Begin(UIControlledApplication application) => new RibbonTracker(application);

        /// <summary>Compares with the snapshot and remembers what the add-in created.</summary>
        public void End()
        {
            foreach (var tab in RibbonTabs())
            {
                if (!tabsBefore.Contains(tab))
                {
                    newTabs.Add(tab);
                    continue;
                }
                var before = panelsBefore[tab];
                foreach (var panel in tab.Panels)
                {
                    if (!before.Contains(panel))
                        newPanels.Add(new KeyValuePair<AdRibbonTab, AdRibbonPanel>(tab, panel));
                }
            }
            foreach (var registry in registries)
                registryChanges.AddRange(registry.Diff());
        }

        public int CreatedItems => newTabs.Count + newPanels.Count;

        /// <summary>Removes everything the add-in added to the ribbon.</summary>
        public void Revert()
        {
            foreach (var pair in newPanels)
                TryRun(() => pair.Key.Panels.Remove(pair.Value));
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            if (ribbon != null)
            {
                foreach (var tab in newTabs)
                    TryRun(() => ribbon.Tabs.Remove(tab));
            }
            foreach (var change in registryChanges)
                TryRun(change.Undo);

            newPanels.Clear();
            newTabs.Clear();
            registryChanges.Clear();
        }

        static IEnumerable<AdRibbonTab> RibbonTabs()
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            return ribbon == null ? Enumerable.Empty<AdRibbonTab>() : ribbon.Tabs.ToList();
        }

        static void TryRun(Action action)
        {
            try { action(); }
            catch (Exception ex) { Log.Info("Ribbon clean-up: " + ex.Message); }
        }

        /// <summary>
        /// String-keyed dictionaries in which RevitAPIUI tracks tabs/panels by name
        /// (e.g. UIApplication.m_ItemsNameDictionary: tab name -> panel name -> panel).
        /// </summary>
        static IEnumerable<IDictionary> Registries(UIControlledApplication application)
        {
            const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var seen = new HashSet<object>();

            foreach (var type in new[] { typeof(UIApplication), typeof(UIControlledApplication), typeof(Autodesk.Revit.UI.RibbonPanel), typeof(RibbonItem) })
            {
                foreach (var field in type.GetFields(Static))
                {
                    if (Read(field, null) is IDictionary dictionary && IsStringKeyed(dictionary) && seen.Add(dictionary))
                        yield return dictionary;
                }
            }
            foreach (var field in typeof(UIControlledApplication).GetFields(Instance))
            {
                if (Read(field, application) is IDictionary dictionary && IsStringKeyed(dictionary) && seen.Add(dictionary))
                    yield return dictionary;
            }
        }

        static object Read(FieldInfo field, object target)
        {
            try { return typeof(IDictionary).IsAssignableFrom(field.FieldType) || field.FieldType == typeof(object) ? field.GetValue(target) : null; }
            catch { return null; }
        }

        static bool IsStringKeyed(IDictionary dictionary)
        {
            var type = dictionary.GetType();
            if (type.IsGenericType)
                return type.GetGenericArguments()[0] == typeof(string);
            return dictionary.Keys.Cast<object>().All(k => k is string);
        }

        sealed class RegistrySnapshot
        {
            readonly IDictionary dictionary;
            readonly HashSet<object> keys;
            readonly Dictionary<object, HashSet<object>> nestedKeys = new Dictionary<object, HashSet<object>>();

            public RegistrySnapshot(IDictionary dictionary)
            {
                this.dictionary = dictionary;
                keys = new HashSet<object>(dictionary.Keys.Cast<object>());
                foreach (var key in keys)
                {
                    if (dictionary[key] is IDictionary nested)
                        nestedKeys[key] = new HashSet<object>(nested.Keys.Cast<object>());
                }
            }

            public IEnumerable<RegistryChange> Diff()
            {
                foreach (var key in dictionary.Keys.Cast<object>().ToList())
                {
                    if (!keys.Contains(key))
                    {
                        yield return new RegistryChange(dictionary, key);
                    }
                    else if (dictionary[key] is IDictionary nested && nestedKeys.TryGetValue(key, out var before))
                    {
                        foreach (var nestedKey in nested.Keys.Cast<object>().ToList())
                        {
                            if (!before.Contains(nestedKey))
                                yield return new RegistryChange(nested, nestedKey);
                        }
                    }
                }
            }
        }

        sealed class RegistryChange
        {
            readonly IDictionary dictionary;
            readonly object key;

            public RegistryChange(IDictionary dictionary, object key)
            {
                this.dictionary = dictionary;
                this.key = key;
            }

            public void Undo()
            {
                if (dictionary.Contains(key))
                    dictionary.Remove(key);
            }
        }
    }
}
