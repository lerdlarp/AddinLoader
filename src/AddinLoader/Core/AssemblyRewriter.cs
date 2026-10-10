using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AddinLoader.Core
{
    /// <summary>
    /// Gives add-in assemblies a new identity (e.g. <c>MyAddin</c> becomes <c>MyAddin.r3</c>)
    /// so a fresh build can be loaded next to the previous one in the same Revit process.
    /// .NET cannot unload an assembly, and loading a second assembly with the same identity
    /// would just hand back the old one.
    /// </summary>
    /// <remarks>
    /// Besides the assembly name, the rewriter fixes everything that refers to the old name:
    /// assembly references, InternalsVisibleTo, WPF pack URIs (<c>/MyAddin;component/View.xaml</c>),
    /// assembly-qualified type names and the WPF <c>MyAddin.g.resources</c> resource.
    /// Debug symbols are rewritten too, so breakpoints keep working.
    /// </remarks>
    public static class AssemblyRewriter
    {
        const string InternalsVisibleTo = "System.Runtime.CompilerServices.InternalsVisibleToAttribute";

        /// <summary>
        /// Rewrites <paramref name="sourceFile"/> into <paramref name="outputDirectory"/>.
        /// </summary>
        /// <param name="renames">Original simple name -> new simple name, for every assembly renamed in this load.</param>
        /// <returns>The path of the written file, named after the new assembly name.</returns>
        public static string Rewrite(string sourceFile, string outputDirectory, IDictionary<string, string> renames)
        {
            var map = new Dictionary<string, string>(renames, StringComparer.OrdinalIgnoreCase);
            var withSymbols = File.Exists(Path.ChangeExtension(sourceFile, ".pdb"));
            try
            {
                return RewriteCore(sourceFile, outputDirectory, map, withSymbols);
            }
            catch (Exception ex) when (withSymbols)
            {
                Log.Info($"Could not rewrite debug symbols of {Path.GetFileName(sourceFile)}, continuing without them ({ex.Message}).");
                return RewriteCore(sourceFile, outputDirectory, map, false);
            }
        }

        static string RewriteCore(string sourceFile, string outputDirectory, Dictionary<string, string> renames, bool withSymbols)
        {
            using (var resolver = new RenamedAssemblyResolver(renames))
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(sourceFile));
                var readerParameters = new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadSymbols = withSymbols,
                };
                if (withSymbols)
                    readerParameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false);

                using (var assembly = AssemblyDefinition.ReadAssembly(sourceFile, readerParameters))
                {
                    var module = assembly.MainModule;
                    var oldName = assembly.Name.Name;
                    var newName = renames.TryGetValue(oldName, out var renamed) ? renamed : oldName;
                    var extension = Path.GetExtension(sourceFile);

                    if (!string.Equals(oldName, newName, StringComparison.Ordinal))
                    {
                        assembly.Name.Name = newName;
                        RemoveStrongName(assembly);
                        module.Name = newName + extension;
                    }

                    foreach (var reference in module.AssemblyReferences)
                    {
                        if (renames.TryGetValue(reference.Name, out var newReference))
                        {
                            reference.Name = newReference;
                            reference.PublicKeyToken = Array.Empty<byte>();
                        }
                    }

                    PatchInternalsVisibleTo(assembly, renames);
                    PatchStrings(module, renames);
                    PatchWpfResources(module, oldName, newName, renames);

                    var output = Path.Combine(outputDirectory, newName + extension);
                    assembly.Write(output, new WriterParameters { WriteSymbols = withSymbols && module.HasSymbols });
                    return output;
                }
            }
        }

        static void RemoveStrongName(AssemblyDefinition assembly)
        {
            assembly.Name.PublicKey = Array.Empty<byte>();
            assembly.Name.PublicKeyToken = Array.Empty<byte>();
            assembly.Name.HasPublicKey = false;
            assembly.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
        }

        // Cecil resolves enum parameter defaults while writing metadata. A referenced project
        // may not have been rewritten yet, so resolve its generation name against the source DLL.
        sealed class RenamedAssemblyResolver : DefaultAssemblyResolver
        {
            readonly Dictionary<string, string> originals;

            public RenamedAssemblyResolver(Dictionary<string, string> renames)
            {
                originals = renames.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            }

            public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
            {
                if (originals.TryGetValue(name.Name, out var original))
                    name = new AssemblyNameReference(original, name.Version) { Culture = name.Culture };
                return base.Resolve(name, parameters);
            }
        }

        /// <summary>
        /// WPF looks up XAML/BAML in "&lt;AssemblyName&gt;.g.resources". Keep the old resource
        /// and add a copy under the new name, whichever name WPF asks for.
        /// </summary>
        static void PatchWpfResources(ModuleDefinition module, string oldName, string newName, Dictionary<string, string> renames)
        {
            var wpfResource = oldName + ".g.resources";
            foreach (var resource in module.Resources.OfType<EmbeddedResource>().ToList())
            {
                if (resource.Name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase))
                {
                    var data = BamlRewriter.RewriteResources(resource.GetResourceData(), renames);
                    var index = module.Resources.IndexOf(resource);
                    module.Resources[index] = new EmbeddedResource(resource.Name, resource.Attributes, data);
                    if (!string.Equals(oldName, newName, StringComparison.Ordinal)
                        && string.Equals(resource.Name, wpfResource, StringComparison.OrdinalIgnoreCase))
                        module.Resources.Add(new EmbeddedResource(newName + ".g.resources", resource.Attributes, data));
                }
            }
        }

        static void PatchInternalsVisibleTo(AssemblyDefinition assembly, Dictionary<string, string> renames)
        {
            foreach (var attribute in assembly.CustomAttributes)
            {
                if (attribute.AttributeType.FullName != InternalsVisibleTo || attribute.ConstructorArguments.Count != 1)
                    continue;
                var argument = attribute.ConstructorArguments[0];
                if (!(argument.Value is string value))
                    continue;
                var friend = value.Split(',')[0].Trim();
                if (renames.TryGetValue(friend, out var newFriend))
                    attribute.ConstructorArguments[0] = new CustomAttributeArgument(argument.Type, newFriend);
            }
        }

        static void PatchStrings(ModuleDefinition module, Dictionary<string, string> renames)
        {
            var rules = renames.Select(r => new StringRule(r.Key, r.Value)).ToList();
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string value)
                            instruction.Operand = PatchString(value, rules);
                    }
                }
            }
        }

        internal static string PatchString(string value, IEnumerable<StringRule> rules)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            foreach (var rule in rules)
                value = rule.Apply(value);
            return value;
        }

        internal sealed class StringRule
        {
            readonly Regex packUri;
            readonly Regex qualifiedType;
            readonly Regex xmlNamespace;
            readonly string resourceNamespace;
            readonly string newName;

            public StringRule(string oldName, string newName)
            {
                this.newName = newName;
                resourceNamespace = oldName + ";";
                var name = Regex.Escape(oldName);
                xmlNamespace = new Regex(@"(?<=;assembly=)" + name + @"(?=$|;)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                // "/MyAddin;component/View.xaml", "pack://application:,,,/MyAddin;v1.0.0.0;component/..."
                packUri = new Regex(@"(?<=^|[/\\])" + name + @"(?=;(v[0-9.]+;)?([0-9a-fA-F]{16};)?component([/\\]|$))",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                // "MyAddin.Commands.Command, MyAddin" or "MyAddin.Commands.Command, MyAddin, Version=1.0.0.0"
                // (the type must be namespace-qualified, so plain text like "Hello, MyAddin" is left alone)
                qualifiedType = new Regex(@"^(?<type>[\w+`]+(\.[\w+`]+)+,\s*)" + name + @"(?=\s*(,|$))",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            public string Apply(string value)
            {
                // Theme managers use this assembly prefix to find an existing pack dictionary.
                // It must follow the same generation as the dictionary's rewritten URI.
                if (string.Equals(value, resourceNamespace, StringComparison.OrdinalIgnoreCase))
                    return newName + ";";
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal))
                    value = xmlNamespace.Replace(value, newName);
                if (value.IndexOf(";component", StringComparison.OrdinalIgnoreCase) >= 0)
                    value = packUri.Replace(value, newName);
                if (value.IndexOf(',') >= 0)
                    value = qualifiedType.Replace(value, m => m.Groups["type"].Value + newName);
                return value;
            }
        }
    }
}
