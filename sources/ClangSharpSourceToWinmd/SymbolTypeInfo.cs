using System;
using MetadataUtils;
using Microsoft.CodeAnalysis;

namespace ClangSharpSourceToWinmd
{
    internal static class SymbolTypeInfo
    {
        internal static ITypeSymbol GetElementType(ITypeSymbol type)
        {
            while (true)
            {
                if (type is IPointerTypeSymbol pointer)
                {
                    type = pointer.PointedAtType;
                }
                else if (type is IArrayTypeSymbol array)
                {
                    type = array.ElementType;
                }
                else
                {
                    return type;
                }
            }
        }

        internal static bool IsMetadataTypedef(ITypeSymbol type)
        {
            var element = GetElementType(type);
            if (element == null)
            {
                return false;
            }

            foreach (var attribute in element.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "MetadataTypedefAttribute")
                {
                    return true;
                }
            }

            return false;
        }

        internal static Architecture GetArchitecture(ISymbol symbol)
        {
            var result = Architecture.All;
            while (symbol != null && !(symbol is INamespaceSymbol) && !(symbol is IAssemblySymbol))
            {
                foreach (var attribute in symbol.GetAttributes())
                {
                    if (attribute.AttributeClass?.Name == "SupportedArchitectureAttribute")
                    {
                        result &= (Architecture)Convert.ToInt32(attribute.ConstructorArguments[0].Value);
                    }
                }

                symbol = symbol.ContainingSymbol;
            }

            return result;
        }

        internal static string GetNativeName(string name)
        {
            return name.EndsWith("____1", StringComparison.Ordinal) || name.EndsWith("____2", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - 5)
                : name;
        }

        internal static string GetTypeKey(ITypeSymbol type)
        {
            if (type is IPointerTypeSymbol pointer)
            {
                return GetTypeKey(pointer.PointedAtType) + "*";
            }

            if (type is IArrayTypeSymbol array)
            {
                return GetTypeKey(array.ElementType) + "[" + array.Rank + "]";
            }

            return type.SpecialType != SpecialType.None
                ? type.SpecialType.ToString()
                : GetNativeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
    }
}
