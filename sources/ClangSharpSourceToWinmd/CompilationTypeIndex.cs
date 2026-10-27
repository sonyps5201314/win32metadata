using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ClangSharpSourceToWinmd
{
    internal sealed class CompilationTypeIndex
    {
        private CSharpCompilation compilation;
        private readonly Dictionary<string, ITypeSymbol> types = new(StringComparer.Ordinal);
        private readonly HashSet<string> ambiguousNames = new(StringComparer.Ordinal);

        internal ITypeSymbol Find(CSharpCompilation compilation, string name)
        {
            if (!ReferenceEquals(this.compilation, compilation))
            {
                this.types.Clear();
                this.ambiguousNames.Clear();
                foreach (var symbol in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<ITypeSymbol>())
                {
                    if (!this.types.TryAdd(symbol.Name, symbol))
                    {
                        this.ambiguousNames.Add(symbol.Name);
                    }
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
    }
}
