using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ClangSharpSourceToWinmd;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClangSharpSourceToWinmdTests
{
    [TestClass]
    public class NativeTypedefRecoveryTests
    {
        [TestMethod]
        public void DiscoverySkipsEnumAndMetadataOverridesAndDeduplicatesPrimitiveAliases()
        {
            using var module = new GeneratedModule(@"
namespace Semantic
{
    public enum Flags : uint { One = 1 }
    [MetadataTypedef] public unsafe struct PrinterHandle { public void* Value; }
    public unsafe struct Holder
    {
        [NativeTypeName(""DWORD"")] public Flags Options;
        [NativeTypeName(""LPHANDLE"")] public PrinterHandle* Printer;
        [NativeTypeName(""COUNT"")] public uint First;
    }
}
namespace Other
{
    public struct Holder { [NativeTypeName(""COUNT"")] public uint Second; }
}
");
            var names = module.Types.Select(t => module.Reader.GetString(t.Name)).ToArray();
            Assert.IsFalse(names.Contains("DWORD"));
            Assert.IsFalse(names.Contains("LPHANDLE"));
            Assert.AreEqual(1, names.Count(n => n == "COUNT"));
            AssertValueSignature(module, "COUNT", 0, SignatureTypeCode.UInt32);
        }

        [TestMethod]
        public void RecoveryPreservesPointersInsideAndOutsideTypedefs()
        {
            using var module = new GeneratedModule(@"
namespace Native
{
    public struct Node { public int Value; }
    public delegate void Callback();
    public unsafe struct Holder
    {
        [NativeTypeName(""NODE_ALIAS *"")] public Node* First;
        [NativeTypeName(""NODE_ALIAS **"")] public Node** Second;
        [NativeTypeName(""DOUBLE_NODE_POINTER"")] public Node** Indirect;
        [NativeTypeName(""PCALLBACK"")] public Callback* Callback;
        [NativeTypeName(""const byte *"")] public byte* Bytes;
    }
}
");
            AssertValueSignature(module, "NODE_ALIAS", 0, SignatureTypeCode.TypeHandle);
            AssertValueSignature(module, "DOUBLE_NODE_POINTER", 2, SignatureTypeCode.TypeHandle);
            AssertValueSignature(module, "PCALLBACK", 1, SignatureTypeCode.TypeHandle);
            Assert.IsFalse(module.Types.Any(t => module.Reader.GetString(t.Name) == "byte"));
            var holder = module.Types.Single(t => module.Reader.GetString(t.Name) == "Holder");
            var fields = holder.GetFields().Select(module.Reader.GetFieldDefinition)
                .ToDictionary(f => module.Reader.GetString(f.Name));
            AssertFieldSignature(module.Reader, fields["First"], 1, SignatureTypeCode.TypeHandle);
            AssertFieldSignature(module.Reader, fields["Second"], 2, SignatureTypeCode.TypeHandle);
        }

        [TestMethod]
        public void NativeAliasCanHaveStructAndFloatingPointVariantsOnDifferentArchitectures()
        {
            using var module = new GeneratedModule(@"
namespace Display
{
    [SupportedArchitecture(Architecture.X86)]
    public struct FLOATOBJ { public uint First; public uint Second; }
    [SupportedArchitecture(Architecture.X86)]
    public struct Owner32 { [NativeTypeName(""FLOATOBJ"")] public FLOATOBJ Value; }
    [SupportedArchitecture(Architecture.X64 | Architecture.Arm64)]
    public struct Owner64 { [NativeTypeName(""FLOATOBJ"")] public float Value; }
}
");
            var variants = module.Types.Where(t => module.Reader.GetString(t.Name) == "FLOATOBJ").ToArray();
            Assert.AreEqual(2, variants.Length);
            var alias = variants.Single(t => t.GetFields().Select(module.Reader.GetFieldDefinition)
                .Any(f => module.Reader.GetString(f.Name) == "Value"));
            var value = alias.GetFields().Select(module.Reader.GetFieldDefinition).Single();
            AssertFieldSignature(module.Reader, value, 0, SignatureTypeCode.Single);
            var attributes = alias.GetCustomAttributes().Select(module.Reader.GetCustomAttribute);
            var architecture = attributes.Single(a => AttributeName(module.Reader, a) == "SupportedArchitectureAttribute");
            var blob = module.Reader.GetBlobReader(architecture.Value);
            Assert.AreEqual(1, blob.ReadUInt16());
            Assert.AreEqual(6, blob.ReadInt32());
        }

        private static void AssertValueSignature(GeneratedModule module, string name, int pointers, SignatureTypeCode element)
        {
            var definition = module.Types.Single(t => module.Reader.GetString(t.Name) == name);
            var field = definition.GetFields().Select(module.Reader.GetFieldDefinition).Single();
            Assert.AreEqual("Value", module.Reader.GetString(field.Name));
            AssertFieldSignature(module.Reader, field, pointers, element);
        }

        [TestMethod]
        public void AnsiStringsKeepTheirAliasesAcrossSignedAndUnsignedCharProjections()
        {
            using var module = new GeneratedModule(@"
namespace Windows.Win32.Foundation
{
    [NativeTypedef] public unsafe struct PSTR { public byte* Value; }
}
namespace Strings
{
    public unsafe struct Holder
    {
        [NativeTypeName(""PSTR"")] public sbyte* Text;
        [NativeTypeName(""PCSTR"")] public sbyte* ConstantText;
        [NativeTypeName(""PCSTR *"")] public sbyte** TextArray;
    }
}
");
            AssertFieldTarget(module, "Holder", "Text", "PSTR");
            AssertFieldTarget(module, "Holder", "ConstantText", "PSTR");
            AssertFieldTarget(module, "Holder", "TextArray", "PSTR", 1);
        }

        [TestMethod]
        public void PointerAliasDiscoveryAcceptsConstPrefixesAndAbbreviatedNames()
        {
            using var module = new GeneratedModule(@"
namespace Shell
{
    public struct ITEMIDLIST { public int Value; }
    public struct IBindCtx { public int Value; }
    public unsafe struct Holder
    {
        [NativeTypeName(""LPCITEMIDLIST"")] public ITEMIDLIST* Item;
        [NativeTypeName(""LPCITEMIDLIST *"")] public ITEMIDLIST** Items;
        [NativeTypeName(""LPBC"")] public IBindCtx* Context;
    }
}
");
            AssertFieldTarget(module, "Holder", "Item", "LPCITEMIDLIST");
            AssertFieldTarget(module, "Holder", "Items", "LPCITEMIDLIST", 1);
            AssertFieldTarget(module, "Holder", "Context", "LPBC");
            AssertValueSignature(module, "LPCITEMIDLIST", 1, SignatureTypeCode.TypeHandle);
            AssertValueSignature(module, "LPBC", 1, SignatureTypeCode.TypeHandle);
        }

        [TestMethod]
        public void OpaqueHandleAliasesPreserveTheExtraOutputPointer()
        {
            using var module = new GeneratedModule(@"
namespace Shell
{
    [NativeTypedef] public struct PAPPSTATE_REGISTRATION { public IntPtr Value; }
    public unsafe struct Holder
    {
        [NativeTypeName(""PAPPSTATE_REGISTRATION"")] public IntPtr* Handle;
        [NativeTypeName(""PAPPSTATE_REGISTRATION *"")] public IntPtr** Output;
    }
}
");
            AssertFieldTarget(module, "Holder", "Handle", "PAPPSTATE_REGISTRATION");
            AssertFieldTarget(module, "Holder", "Output", "PAPPSTATE_REGISTRATION", 1);
        }

        [TestMethod]
        public void ExistingEnumWithNativeAliasNameIsKeptForCompatibleIntegers()
        {
            using var module = new GeneratedModule(@"
namespace Shell
{
    public enum SFBS_FLAGS : int { Round = 1, Truncate = 2 }
    public struct Holder { [NativeTypeName(""SFBS_FLAGS"")] public int Flags; }
}
");
            AssertFieldTarget(module, "Holder", "Flags", "SFBS_FLAGS");
        }

        [TestMethod]
        public void PointerAliasesToEnumsPreserveExistingSemanticProjections()
        {
            using var module = new GeneratedModule(@"
namespace Semantic
{
    public enum Flags : uint { One = 1 }
    public unsafe struct Holder
    {
        [NativeTypeName(""DWORD"")] public uint Raw;
        [NativeTypeName(""PCFLAGS"")] public Flags* Named;
        [NativeTypeName(""DWORD *"")] public Flags* Projected;
    }
}
");
            AssertFieldTarget(module, "Holder", "Named", "PCFLAGS");
            AssertFieldTarget(module, "Holder", "Projected", "Flags", 1);
            AssertValueSignature(module, "PCFLAGS", 1, SignatureTypeCode.TypeHandle);
            AssertValueSignature(module, "DWORD", 0, SignatureTypeCode.UInt32);
        }

        private static void AssertFieldTarget(GeneratedModule module, string owner, string fieldName, string target, int pointers = 0)
        {
            var definition = module.Types.Single(t => module.Reader.GetString(t.Name) == owner);
            var field = definition.GetFields().Select(module.Reader.GetFieldDefinition)
                .Single(f => module.Reader.GetString(f.Name) == fieldName);
            var blob = module.Reader.GetBlobReader(field.Signature);
            Assert.AreEqual(SignatureKind.Field, blob.ReadSignatureHeader().Kind);
            for (int i = 0; i < pointers; i++)
            {
                Assert.AreEqual(SignatureTypeCode.Pointer, blob.ReadSignatureTypeCode(), fieldName);
            }

            Assert.AreEqual(SignatureTypeCode.TypeHandle, blob.ReadSignatureTypeCode(), fieldName);
            var handle = blob.ReadTypeHandle();
            string actual = handle.Kind == HandleKind.TypeDefinition
                ? module.Reader.GetString(module.Reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name)
                : module.Reader.GetString(module.Reader.GetTypeReference((TypeReferenceHandle)handle).Name);
            Assert.AreEqual(target, actual, fieldName);
            Assert.AreEqual(0, blob.RemainingBytes, fieldName);
        }

        [TestMethod]
        public void NativeBooleanAliasDoesNotResolveToFrameworkBoolean()
        {
            using var module = new GeneratedModule(@"
namespace MessageQueuing
{
    public unsafe struct Holder
    {
        [NativeTypeName(""Boolean *"")] public short* Transactional;
        public bool ManagedBoolean;
    }
}
");
            AssertFieldTarget(module, "Holder", "Transactional", "Boolean", 1);
            AssertValueSignature(module, "Boolean", 0, SignatureTypeCode.Int16);
            var definition = module.Types.Single(t => module.Reader.GetString(t.Name) == "Boolean");
            Assert.AreNotEqual("System", module.Reader.GetString(definition.Namespace));
            var holder = module.Types.Single(t => module.Reader.GetString(t.Name) == "Holder");
            var boolean = holder.GetFields().Select(module.Reader.GetFieldDefinition)
                .Single(f => module.Reader.GetString(f.Name) == "ManagedBoolean");
            AssertFieldSignature(module.Reader, boolean, 0, SignatureTypeCode.Boolean);
        }

        [TestMethod]
        public void BytePlaceholdersRecoverNativePointerTargetsWithoutChangingScalarBytes()
        {
            using var module = new GeneratedModule(@"
namespace Imaging
{
    [NativeTypedef] public struct Image { public IntPtr Value; }
    public struct EncoderParameters { public uint Count; }
    public unsafe struct Holder
    {
        [NativeTypeName(""Image *"")] public byte* Frame;
        [NativeTypeName(""Image **"")] public byte** Frames;
        [NativeTypeName(""EncoderParameters **"")] public byte** Encoders;
        [NativeTypeName(""Image"")] public byte Scalar;
        public byte* Bytes;
    }
}
");
            AssertFieldTarget(module, "Holder", "Frame", "Image", 1);
            AssertFieldTarget(module, "Holder", "Frames", "Image", 2);
            AssertFieldTarget(module, "Holder", "Encoders", "EncoderParameters", 2);
            var holder = module.Types.Single(t => module.Reader.GetString(t.Name) == "Holder");
            var fields = holder.GetFields().Select(module.Reader.GetFieldDefinition)
                .ToDictionary(f => module.Reader.GetString(f.Name));
            AssertFieldSignature(module.Reader, fields["Scalar"], 0, SignatureTypeCode.Byte);
            AssertFieldSignature(module.Reader, fields["Bytes"], 1, SignatureTypeCode.Byte);
        }

        [TestMethod]
        public void ExplicitEnumMappingAndPointerAliasesKeepWidthAndRejectUnrelatedScalarEnums()
        {
            using var module = new GeneratedModule(@"
namespace Graphics
{
    public enum InterpolationMode : int { Invalid = -1, Default = 0 }
}
namespace Imaging
{
    public enum PRIORITY : int { Low = 1, Normal = 2 }
    [NativeTypedef] public struct PixelFormat { public int Value; }
    public unsafe struct Holder
    {
        [NativeTypeName(""InterpolationMode"")] public uint Hints;
        [NativeTypeName(""PixelFormat *"")] public uint* Format;
        [NativeTypeName(""InterpolationMode"")] public ulong WrongWidth;
        [NativeTypeName(""PixelFormat"")] public uint Scalar;
        [NativeTypeName(""PRIORITY"")] public uint Priority;
    }
}
", new Dictionary<string, string> { ["Holder::Hints"] = "Graphics.InterpolationMode" });
            AssertFieldTarget(module, "Holder", "Hints", "InterpolationMode");
            AssertFieldTarget(module, "Holder", "Format", "PixelFormat", 1);
            var holder = module.Types.Single(t => module.Reader.GetString(t.Name) == "Holder");
            var fields = holder.GetFields().Select(module.Reader.GetFieldDefinition)
                .ToDictionary(f => module.Reader.GetString(f.Name));
            AssertFieldSignature(module.Reader, fields["WrongWidth"], 0, SignatureTypeCode.UInt64);
            AssertFieldSignature(module.Reader, fields["Scalar"], 0, SignatureTypeCode.UInt32);
            AssertFieldSignature(module.Reader, fields["Priority"], 0, SignatureTypeCode.UInt32);
        }

        [TestMethod]
        public void ConflictingProjectionsKeepOneAliasWithoutChangingSpecializedBuffers()
        {
            using var module = new GeneratedModule(@"
namespace Projection
{
    public struct DocumentInfo { public int Value; }
    public unsafe struct Holder
    {
        [NativeTypeName(""LPBYTE"")] public DocumentInfo* Document;
        [NativeTypeName(""COUNT"")] public uint Unsigned;
        [NativeTypeName(""COUNT"")] public int Signed;
        [NativeTypeName(""LPVOID"")] public IntPtr Opaque;
        [NativeTypeName(""LPVOID"")] public void* Address;
        [NativeTypeName(""LPBYTE"")] public byte* Bytes;
        [NativeTypeName(""ULONG_PTR"")] public UIntPtr Portable;
    }
    [SupportedArchitecture(Architecture.Arm64)]
    public struct Arm64Holder { [NativeTypeName(""ULONG_PTR"")] public ulong Value; }
}
");
            foreach (var name in new[] { "COUNT", "LPVOID", "LPBYTE", "ULONG_PTR" })
            {
                Assert.AreEqual(1, module.Types.Count(t => module.Reader.GetString(t.Name) == name), name);
            }

            AssertValueSignature(module, "COUNT", 0, SignatureTypeCode.UInt32);
            AssertValueSignature(module, "LPVOID", 1, SignatureTypeCode.Void);
            AssertValueSignature(module, "LPBYTE", 1, SignatureTypeCode.Byte);
            AssertValueSignature(module, "ULONG_PTR", 0, SignatureTypeCode.UIntPtr);
            var holder = module.Types.Single(t => module.Reader.GetString(t.Name) == "Holder");
            var fields = holder.GetFields().Select(module.Reader.GetFieldDefinition)
                .ToDictionary(f => module.Reader.GetString(f.Name));
            AssertFieldSignature(module.Reader, fields["Signed"], 0, SignatureTypeCode.Int32);
            var signature = module.Reader.GetBlobReader(fields["Document"].Signature);
            signature.ReadSignatureHeader();
            Assert.AreEqual(SignatureTypeCode.Pointer, signature.ReadSignatureTypeCode());
            Assert.AreEqual(SignatureTypeCode.TypeHandle, signature.ReadSignatureTypeCode());
            var handle = signature.ReadTypeHandle();
            string actualName = handle.Kind == HandleKind.TypeDefinition
                ? module.Reader.GetString(module.Reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name)
                : module.Reader.GetString(module.Reader.GetTypeReference((TypeReferenceHandle)handle).Name);
            Assert.AreEqual("DocumentInfo", actualName);
        }

        private static void AssertFieldSignature(MetadataReader reader, FieldDefinition field, int pointers, SignatureTypeCode element)
        {
            var blob = reader.GetBlobReader(field.Signature);
            Assert.AreEqual(SignatureKind.Field, blob.ReadSignatureHeader().Kind);
            for (int i = 0; i < pointers; i++)
            {
                Assert.AreEqual(SignatureTypeCode.Pointer, blob.ReadSignatureTypeCode());
            }

            Assert.AreEqual(element, blob.ReadSignatureTypeCode());
        }

        private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
        {
            var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            return parent.Kind == HandleKind.TypeDefinition
                ? reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name)
                : reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name);
        }

        [TestMethod]
        public void NewlyDiscoveredAliasesInheritUnderlyingArchitectureInsteadOfCombinedOwner()
        {
            using var module = new GeneratedModule(@"
namespace Hypervisor
{
    [SupportedArchitecture(Architecture.X64)]
    public struct X64Features { public ulong Value; }
    [SupportedArchitecture(Architecture.Arm64)]
    public struct Arm64Features { public ulong Value; }
    [SupportedArchitecture(Architecture.X64 | Architecture.Arm64)]
    public struct Combined
    {
        [NativeTypeName(""FEATURES"")] public X64Features X64View;
        [NativeTypeName(""FEATURES"")] public Arm64Features Arm64View;
    }
}
");
            var variants = module.Types.Where(t => module.Reader.GetString(t.Name) == "FEATURES").ToArray();
            Assert.AreEqual(2, variants.Length);
            var architectures = new List<int>();
            foreach (var variant in variants)
            {
                var attribute = variant.GetCustomAttributes().Select(module.Reader.GetCustomAttribute)
                    .Single(a => AttributeName(module.Reader, a) == "SupportedArchitectureAttribute");
                var blob = module.Reader.GetBlobReader(attribute.Value);
                Assert.AreEqual(1, blob.ReadUInt16());
                architectures.Add(blob.ReadInt32());
            }

            CollectionAssert.AreEquivalent(new[] { 2, 4 }, architectures);
        }

        private sealed class GeneratedModule : IDisposable
        {
            private readonly string directory = Path.Combine(Path.GetTempPath(), "NativeTypedefRecoveryTests", Guid.NewGuid().ToString("N"));
            private readonly PEReader pe;
            private readonly Stream stream;

            internal GeneratedModule(string source, Dictionary<string, string>? remaps = null)
            {
                Directory.CreateDirectory(this.directory);
                File.WriteAllText(Path.Combine(this.directory, "Source.cs"), MetadataSource + source);
                var compilation = ClangSharpSourceCompilation.Create(this.directory, "x64", remaps ?? new Dictionary<string, string>(),
                    new Dictionary<string, Dictionary<string, string>>(), Array.Empty<string>(), new Dictionary<string, string>(),
                    new Dictionary<string, string>(), new HashSet<string>(), Array.Empty<string>(),
                    new Dictionary<string, string>(), new Dictionary<string, string>());
                Assert.AreEqual(0, compilation.GetDiagnostics().Count, string.Join(Environment.NewLine, compilation.GetDiagnostics()));
                var output = Path.Combine(this.directory, "Types.winmd");
                var generator = ClangSharpSourceWinmdGenerator.GenerateWindmdForCompilation(compilation,
                    new Dictionary<string, string>(), new HashSet<string>(), new HashSet<string>(),
                    new Version(1, 0, 0, 0), output, validateGeneratedTypedefs: true);
                Assert.IsTrue(generator.WroteWinmd, string.Join(Environment.NewLine, generator.GetDiagnostics()));
                this.stream = File.OpenRead(output);
                this.pe = new PEReader(this.stream);
                this.Reader = this.pe.GetMetadataReader();
            }

            internal MetadataReader Reader { get; }
            internal IEnumerable<TypeDefinition> Types => this.Reader.TypeDefinitions.Select(this.Reader.GetTypeDefinition);

            public void Dispose()
            {
                this.pe.Dispose();
                this.stream.Dispose();
                Directory.Delete(this.directory, true);
            }
        }

        private const string MetadataSource = @"
using System;
using Windows.Win32.Foundation.Metadata;
namespace Windows.Win32.Foundation.Metadata
{
    [Flags] public enum Architecture { X86 = 1, X64 = 2, Arm64 = 4, All = 7 }
    public class SupportedArchitectureAttribute : Attribute { public SupportedArchitectureAttribute(Architecture value) { } }
    public class NativeTypedefAttribute : Attribute { }
    public class MetadataTypedefAttribute : Attribute { }
    public class ConstAttribute : Attribute { }
    public class NativeTypeNameAttribute : Attribute { public NativeTypeNameAttribute(string value) { } }
}
";
    }
}
