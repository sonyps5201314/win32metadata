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
    public class FixedBufferPointerTests
    {
        [TestMethod]
        public void FixedBufferRemappingPreservesElementPointerDepth()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WinmdFixedBufferTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "Arrays.cs"), Source);
                var imports = new Dictionary<string, string>();
                var reducedPointers = new HashSet<string>();
                var compilation = ClangSharpSourceCompilation.Create(
                    directory, "x64", new Dictionary<string, string>(),
                    new Dictionary<string, Dictionary<string, string>>(), Array.Empty<string>(),
                    imports, new Dictionary<string, string>(), reducedPointers,
                    Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
                Assert.AreEqual(0, compilation.GetDiagnostics().Count);

                var output = Path.Combine(directory, "Arrays.winmd");
                var generator = ClangSharpSourceWinmdGenerator.GenerateWindmdForCompilation(
                    compilation, imports, reducedPointers, new HashSet<string>(), new Version(1, 0, 0, 0), output);
                Assert.IsTrue(generator.WroteWinmd, string.Join(Environment.NewLine, generator.GetDiagnostics()));

                using var stream = File.OpenRead(output);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                var holder = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                    .Single(t => reader.GetString(t.Name) == "Holder");
                var fields = holder.GetFields().Select(reader.GetFieldDefinition)
                    .ToDictionary(f => reader.GetString(f.Name));
                AssertArray(reader, fields["Pointers"], "NODE_ALIAS", 1);
                AssertArray(reader, fields["DoublePointers"], "NODE_ALIAS", 2);
                AssertArray(reader, fields["PointerTypedefs"], "PVOID", 0);
                AssertArray(reader, fields["Values"], "NODE_ALIAS", 0);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void AssertArray(MetadataReader reader, FieldDefinition field, string elementName, int pointerDepth)
        {
            var signature = reader.GetBlobReader(field.Signature);
            Assert.AreEqual(SignatureKind.Field, signature.ReadSignatureHeader().Kind);
            Assert.AreEqual(SignatureTypeCode.Array, signature.ReadSignatureTypeCode());
            for (int i = 0; i < pointerDepth; i++)
            {
                Assert.AreEqual(SignatureTypeCode.Pointer, signature.ReadSignatureTypeCode());
            }

            Assert.AreEqual(SignatureTypeCode.TypeHandle, signature.ReadSignatureTypeCode());
            var handle = signature.ReadTypeHandle();
            var actualName = handle.Kind == HandleKind.TypeDefinition
                ? reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name)
                : reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name);
            Assert.AreEqual(elementName, actualName, reader.GetString(field.Name));
            Assert.AreEqual(1, signature.ReadCompressedInteger());
            Assert.AreEqual(1, signature.ReadCompressedInteger());
            Assert.AreEqual(2, signature.ReadCompressedInteger());
            Assert.AreEqual(1, signature.ReadCompressedInteger());
            Assert.AreEqual(0, signature.ReadCompressedSignedInteger());
            Assert.AreEqual(0, signature.RemainingBytes);
        }

        private const string Source = @"
using System;
using Windows.Win32.Foundation.Metadata;
namespace Windows.Win32.Foundation.Metadata
{
    public class NativeTypeNameAttribute : Attribute
    {
        public NativeTypeNameAttribute(string value) { }
    }
    public class NativeTypedefAttribute : Attribute { }
}
namespace Windows.Win32.Test
{
    public struct Node { public int Data; }
    [NativeTypedef]
    public struct NODE_ALIAS { public Node Value; }
    [NativeTypedef]
    public unsafe struct PVOID { public void* Value; }
    public unsafe struct Holder
    {
        [NativeTypeName(""NODE_ALIAS *[2]"")]
        public _Pointers_e__FixedBuffer Pointers;
        [NativeTypeName(""NODE_ALIAS **[2]"")]
        public _DoublePointers_e__FixedBuffer DoublePointers;
        [NativeTypeName(""PVOID [2]"")]
        public _PointerTypedefs_e__FixedBuffer PointerTypedefs;
        [NativeTypeName(""NODE_ALIAS [2]"")]
        public _Values_e__FixedBuffer Values;
        public struct _Pointers_e__FixedBuffer { public Node* e0; public Node* e1; }
        public struct _DoublePointers_e__FixedBuffer { public Node** e0; public Node** e1; }
        public struct _PointerTypedefs_e__FixedBuffer { public void* e0; public void* e1; }
        public struct _Values_e__FixedBuffer { public Node e0; public Node e1; }
    }
}";
    }
}
