using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AddinLoader.Core
{
    /// <summary>Reads the assembly paths from a Revit .addin manifest.</summary>
    public static class AddinManifest
    {
        public static List<string> ReadAssemblies(string manifestPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            var document = XDocument.Load(manifestPath);
            return document.Descendants()
                .Where(e => e.Name.LocalName.Equals("AddIn", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName.Equals("Assembly", StringComparison.OrdinalIgnoreCase))?.Value?.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.IsPathRooted(p) ? p : Path.Combine(directory, p))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
