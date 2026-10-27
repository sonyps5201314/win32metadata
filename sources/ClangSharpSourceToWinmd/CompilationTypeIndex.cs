using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MetadataUtils;

namespace ClangSharpSourceToWinmd
{
    internal sealed class CompilationTypeIndex
    {
        private CSharpCompilation compilation;
        private readonly Dictionary<string, ITypeSymbol> types = new(StringComparer.Ordinal);
        private readonly HashSet<string> ambiguousNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ITypeSymbol>> nativeTypes = new(StringComparer.Ordinal);

        internal ITypeSymbol Find(CSharpCompilation compilation, string name)
        {
            if (!ReferenceEquals(this.compilation, compilation))
            {
                this.types.Clear();
                this.ambiguousNames.Clear();
                this.nativeTypes.Clear();
                foreach (var symbol in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<ITypeSymbol>())
                {
                    if (!this.types.TryAdd(symbol.Name, symbol))
                    {
                        this.ambiguousNames.Add(symbol.Name);
                    }

                    string nativeName = SymbolTypeInfo.GetNativeName(symbol.Name);
                    if (!this.nativeTypes.TryGetValue(nativeName, out var candidates))
                    {
                        this.nativeTypes[nativeName] = candidates = new List<ITypeSymbol>();
                    }

                    candidates.Add(symbol);
                }

                this.compilation = compilation;
            }

            if (this.ambiguousNames.Remove(name))
            {
                // 重名时保留 Roslyn 原查询的选择顺序。
                this.types[name] = compilation.GetSymbolsWithName(name, SymbolFilter.Type).FirstOrDefault() as ITypeSymbol;
            }

            this.types.TryGetValue(name, out var result);
            return result;
        }

        internal ITypeSymbol Find(CSharpCompilation compilation, string name, Architecture architecture)
        {
            var original = this.Find(compilation, name);
            if (architecture == Architecture.All || !this.nativeTypes.TryGetValue(name, out var candidates))
            {
                return original;
            }

            if (original != null && (SymbolTypeInfo.GetArchitecture(original) & architecture) == architecture)
            {
                return original;
            }

            var available = Architecture.None;
            ITypeSymbol matching = null;
            foreach (var candidate in candidates)
            {
                var candidateArchitecture = SymbolTypeInfo.GetArchitecture(candidate);
                if ((candidateArchitecture & architecture) == architecture)
                {
                    return candidate;
                }

                available |= candidateArchitecture;
                if ((candidateArchitecture & architecture) != Architecture.None)
                {
                    matching ??= candidate;
                }
            }

            return (available & architecture) == architecture ? matching : null;
        }

        internal IEnumerable<ITypeSymbol> GetNativeCandidates(CSharpCompilation compilation, string name)
        {
            this.Find(compilation, name);
            return this.nativeTypes.TryGetValue(name, out var candidates)
                ? candidates
                : Enumerable.Empty<ITypeSymbol>();
        }
    }
}
