using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Resources;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AddinLoader.Tests
{
    /// <summary>
    /// Builds a tiny "add-in" (main assembly + project library) with Cecil, the way a
    /// Visual Studio build would write it into bin\Debug.
    /// </summary>
    static class TestAssemblies
    {
        public static void Build(string directory, string mainName, string libName, int version)
        {
            Directory.CreateDirectory(directory);

            using var lib = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(libName, new Version(1, 0, 0, 0)), libName + ".dll", ModuleKind.Dll);
            var thing = AddStaticClass(lib.MainModule, libName, "Thing");
            AddStringMethod(lib.MainModule, thing, "Value", $"lib-v{version}");
            var mode = new TypeDefinition(libName, "Mode", TypeAttributes.Public | TypeAttributes.Sealed,
                lib.MainModule.ImportReference(typeof(Enum)));
            mode.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, lib.MainModule.TypeSystem.Int32));
            lib.MainModule.Types.Add(mode);
            lib.Write(Path.Combine(directory, libName + ".dll"));

            using var main = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(mainName, new Version(1, 0, 0, 0)), mainName + ".dll", ModuleKind.Dll);
            var module = main.MainModule;
            ((DefaultAssemblyResolver)module.AssemblyResolver).AddSearchDirectory(directory);
            var probe = AddStaticClass(module, mainName, "Probe");
            AddStringMethod(module, probe, "Value", $"main-v{version}");
            AddStringMethod(module, probe, "Uri", $"/{mainName};component/view.xaml");
            AddStringMethod(module, probe, "QualifiedType", $"{mainName}.Probe, {mainName}");
            AddStringMethod(module, probe, "Message", $"Hello, {mainName}");
            AddStringMethod(module, probe, "ResourceNamespace", mainName.ToLowerInvariant() + ";");

            var libValue = new MethodDefinition("LibValue", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.String);
            var il = libValue.Body.GetILProcessor();
            il.Emit(OpCodes.Call, module.ImportReference(thing.Methods.Single()));
            il.Emit(OpCodes.Ret);
            probe.Methods.Add(libValue);

            var enumArgument = new MethodDefinition("EnumArgument", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            enumArgument.Parameters.Add(new ParameterDefinition("mode", ParameterAttributes.Optional | ParameterAttributes.HasDefault,
                module.ImportReference(mode)) { Constant = 0 });
            enumArgument.Body.GetILProcessor().Emit(OpCodes.Ret);
            probe.Methods.Add(enumArgument);

            var ivt = module.ImportReference(typeof(InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) }));
            var attribute = new CustomAttribute(ivt);
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, libName));
            main.CustomAttributes.Add(attribute);

            using (var resources = new MemoryStream())
            {
                using (var writer = new ResourceWriter(resources))
                {
                    writer.AddResource("icon", new byte[] { 1, 2, 3 });
                    writer.Generate();
                    module.Resources.Add(new EmbeddedResource(mainName + ".g.resources", ManifestResourceAttributes.Public, resources.ToArray()));
                }
            }
            main.Write(Path.Combine(directory, mainName + ".dll"));
        }

        static TypeDefinition AddStaticClass(ModuleDefinition module, string ns, string name)
        {
            var type = new TypeDefinition(ns, name,
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
                module.TypeSystem.Object);
            module.Types.Add(type);
            return type;
        }

        static void AddStringMethod(ModuleDefinition module, TypeDefinition type, string name, string value)
        {
            var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.String);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, value);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(method);
        }
    }
}
