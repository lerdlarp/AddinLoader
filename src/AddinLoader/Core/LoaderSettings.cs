using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace AddinLoader.Core
{
    /// <summary>Settings saved per Revit version in %AppData%\AddinLoader.</summary>
    public sealed class LoaderSettings
    {
        /// <summary>Add-in assemblies shown in the Application Loader (loaded on Revit startup).</summary>
        public List<string> Assemblies { get; set; } = new List<string>();

        /// <summary>Reload an add-in automatically as soon as a new build is written.</summary>
        public bool AutoReload { get; set; } = true;

        /// <summary>Last executed command, used by "Application Loader (Faceless)".</summary>
        public string LastCommandAssembly { get; set; }
        public string LastCommandType { get; set; }

        public static string Root =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AddinLoader");

        public static string ShadowRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AddinLoader", "Shadow");

        static string FilePath(string revitVersion) => Path.Combine(Root, $"settings.{revitVersion}.xml");

        public static LoaderSettings Load(string revitVersion)
        {
            try
            {
                var path = FilePath(revitVersion);
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                        return (LoaderSettings)new XmlSerializer(typeof(LoaderSettings)).Deserialize(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings, using defaults.", ex);
            }
            return new LoaderSettings();
        }

        public void Save(string revitVersion)
        {
            try
            {
                Directory.CreateDirectory(Root);
                using (var stream = File.Create(FilePath(revitVersion)))
                    new XmlSerializer(typeof(LoaderSettings)).Serialize(stream, this);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save settings.", ex);
            }
        }
    }
}
