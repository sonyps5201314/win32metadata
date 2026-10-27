using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ClangSharpSourceToWinmd;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClangSharpSourceToWinmdTests
{
    [TestClass]
    public class GeneratedTypedefValidationTests
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void GeneratedTypedefIsEmittedWithAndWithoutOptionalValidation(bool validate)
        {
            WithSource("NATIVE_COUNT", (directory, output) =>
            {
                var arguments = new List<string> { "--sourceDir", directory, "--arch", "x64", "--outputFileName", output };
                if (validate)
                {
                    arguments.Add("--validateGeneratedTypedefs");
                }

                Assert.AreEqual(0, Program.Main(arguments.ToArray()));
                using var stream = File.OpenRead(output);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                Assert.IsTrue(reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                    .Any(t => reader.GetString(t.Name) == "NATIVE_COUNT"));
            });
        }

        [TestMethod]
        public void OptionalValidationRejectsInvalidGeneratedTypedefBeforeWritingWinmd()
        {
            WithSource("class", (directory, output) =>
            {
                Assert.AreNotEqual(0, Program.Main(new[]
                {
                    "--sourceDir", directory, "--arch", "x64", "--outputFileName", output,
                    "--validateGeneratedTypedefs"
                }));
                Assert.IsFalse(File.Exists(output));
            });
        }

        [TestMethod]
        public void ValidationPreservesWinmdPointerAndImportedTypeExceptions()
        {
            var reference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"dotnet\packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1\netstandard.dll");
            var compilation = CSharpCompilation.Create("ValidationTests", new[] { CSharpSyntaxTree.ParseText(@"
public struct Managed { public string Text; }
public unsafe struct Holder
{
    public Managed* Pointer;
    public IImported External;
    public IStillMissing Invalid;
}") }, new[] { MetadataReference.CreateFromFile(reference) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var diagnostics = ClangSharpSourceCompilation.GetDeclarationDiagnostics(compilation,
                new Dictionary<string, string> { ["IImported(interface)"] = "external" });
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual("CS0246", diagnostics[0].Id);
            Assert.IsTrue(diagnostics[0].GetMessage().Contains("IStillMissing"));
        }

        private static void WithSource(string nativeTypeName, Action<string, string> action)
        {
            var directory = Path.Combine(Path.GetTempPath(), "WinmdTypedefValidationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "Typedef.cs"), Source.Replace("NATIVE_TYPE_NAME", nativeTypeName));
                action(directory, Path.Combine(directory, "Typedef.winmd"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
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
    public struct Holder
    {
        [NativeTypeName(""NATIVE_TYPE_NAME"")]
        public uint Count;
    }
}";
    }
}
