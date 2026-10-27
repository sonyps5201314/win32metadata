using System;
using System.IO;
using System.Linq;
using ClangSharpSourceToWinmd;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClangSharpSourceToWinmdTests
{
    [TestClass]
    public class CompilationTypeIndexTests
    {
        [TestMethod]
        public void IndexedLookupPreservesRoslynSelectionForUniqueAndAmbiguousNames()
        {
            var compilation = CreateCompilation(@"
namespace First
{
    public partial struct Shared { }
    public partial struct Shared { }
    public struct Unique { }
    public class Generic<T> { public class Nested { } }
    public class Generic { }
}
namespace Second
{
    public struct Shared { }
    public class Container { public struct Nested { } }
}");
            var index = new CompilationTypeIndex();
            foreach (var name in new[] { "Shared", "Unique", "Generic", "Nested", "Missing", "unique", "Int32", "First.Unique" })
            {
                var expected = compilation.GetSymbolsWithName(name, SymbolFilter.Type).FirstOrDefault();
                Assert.IsTrue(SymbolEqualityComparer.Default.Equals(expected, index.Find(compilation, name)), name);
                Assert.IsTrue(SymbolEqualityComparer.Default.Equals(expected, index.Find(compilation, name)), name);
            }
        }

        [TestMethod]
        public void AddingTypedefTreeInvalidatesMissingAndExistingSymbolResults()
        {
            var compilation = CreateCompilation("namespace Original { public struct Existing { } }");
            var index = new CompilationTypeIndex();
            Assert.IsNull(index.Find(compilation, "ADDED_TYPEDEF"));
            var original = index.Find(compilation, "Existing");
            Assert.IsNotNull(original);

            var updated = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(@"
namespace Generated
{
    public struct ADDED_TYPEDEF { public int Value; }
    public struct Existing { }
}"));
            foreach (var name in new[] { "Existing", "ADDED_TYPEDEF" })
            {
                var expected = updated.GetSymbolsWithName(name, SymbolFilter.Type).FirstOrDefault();
                Assert.IsNotNull(expected);
                Assert.IsTrue(SymbolEqualityComparer.Default.Equals(expected, index.Find(updated, name)), name);
            }

            Assert.IsNull(index.Find(compilation, "ADDED_TYPEDEF"));
            Assert.IsTrue(SymbolEqualityComparer.Default.Equals(original, index.Find(compilation, "Existing")));
        }

        private static CSharpCompilation CreateCompilation(string source)
        {
            var reference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"dotnet\packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1\netstandard.dll");
            return CSharpCompilation.Create("LookupTests", new[] { CSharpSyntaxTree.ParseText(source) },
                new[] { MetadataReference.CreateFromFile(reference) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }
    }
}
