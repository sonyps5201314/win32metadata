using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;

public class NativeTypeNameCollector : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    public List<FoundElementInfo> FoundElements { get; } = new List<FoundElementInfo>();

    private string _currentStructName = null;
    private string _currentMethodName = null;

    public NativeTypeNameCollector(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
    }

    private (bool Found, string Value) GetNativeTypeNameDetails(
        SyntaxList<AttributeListSyntax> attributeLists,
        bool checkReturnTarget = false)
    {
        foreach (var attributeListSyntax in attributeLists)
        {
            bool isReturnAttributeList = attributeListSyntax.Target?.Identifier.IsKind(SyntaxKind.ReturnKeyword) ?? false;
            if (checkReturnTarget && !isReturnAttributeList) continue;
            if (!checkReturnTarget && isReturnAttributeList) continue;

            foreach (var attributeSyntax in attributeListSyntax.Attributes)
            {
                var attributeName = attributeSyntax.Name.ToString();
                if (attributeName == "NativeTypeName")
                {
                    string nativeTypeNameValue = "N/A (no argument)";
                    if (attributeSyntax.ArgumentList != null && attributeSyntax.ArgumentList.Arguments.Any())
                    {
                        var firstArgumentExpression = attributeSyntax.ArgumentList.Arguments.First().Expression;
                        if (firstArgumentExpression is LiteralExpressionSyntax literalExp &&
                            literalExp.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            nativeTypeNameValue = literalExp.Token.ValueText;
                        }
                        else
                        {
                            nativeTypeNameValue = firstArgumentExpression.ToString();
                        }
                    }
                    return (true, nativeTypeNameValue);
                }
            }
        }
        return (false, null);
    }

    private string GetNamespaceForType(TypeSyntax typeSyntax, out ITypeSymbol typeSymbol)
    {
        typeSymbol = null;
        if (typeSyntax == null) return "N/A";

        typeSymbol = _semanticModel.GetTypeInfo(typeSyntax).Type;
        ITypeSymbol typeSymbol_Last = typeSymbol;
REFIND_NS:
        if (typeSymbol != null)
        {
            // For array T[], pointer T*, nullable T?, get the element/underlying type's namespace
            if (typeSymbol is IArrayTypeSymbol arraySymbol) typeSymbol = arraySymbol.ElementType;
            else if (typeSymbol is IPointerTypeSymbol pointerSymbol) typeSymbol = pointerSymbol.PointedAtType;
            else if (typeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                typeSymbol = (typeSymbol as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
            }

            if (typeSymbol?.ContainingNamespace != null && !typeSymbol.ContainingNamespace.IsGlobalNamespace)
            {
                return typeSymbol.ContainingNamespace.ToDisplayString();
            }
            if (typeSymbol != typeSymbol_Last)
            {
                typeSymbol_Last = typeSymbol;
                goto REFIND_NS;
            }
        }
        return "Windows.Win32.Foundation";
    }


    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        _currentStructName = node.Identifier.Text;
        base.VisitStructDeclaration(node);
        _currentStructName = null;
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (_currentStructName != null)
        {
            var details = GetNativeTypeNameDetails(node.AttributeLists);
            if (details.Found)
            {
                string fieldTypeNamespace = GetNamespaceForType(node.Declaration.Type, out var typeSymbol);
                foreach (var variable in node.Declaration.Variables)
                {
                    FoundElements.Add(new FoundElementInfo(
                        ElementType.StructField,
                        _currentStructName,
                        variable.Identifier.Text,
                        node.Declaration.Type.ToString(),
                        fieldTypeNamespace,
                        details.Value,
                        typeSymbol
                    ));
                }
            }
        }
        base.VisitFieldDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        _currentMethodName = node.Identifier.Text;

        // 1. 检查函数参数
        foreach (var parameter in node.ParameterList.Parameters)
        {
            var details = GetNativeTypeNameDetails(parameter.AttributeLists);
            if (details.Found)
            {
                string paramTypeNamespace = GetNamespaceForType(parameter.Type, out var typeSymbol);
                FoundElements.Add(new FoundElementInfo(
                    ElementType.FunctionParameter,
                    _currentMethodName,
                    parameter.Identifier.Text,
                    parameter.Type?.ToString() ?? "N/A",
                    paramTypeNamespace,
                    details.Value,
                    typeSymbol
                ));
            }
        }

        // 2. 检查函数返回值
        var returnDetails = GetNativeTypeNameDetails(node.AttributeLists, checkReturnTarget: true);
        if (returnDetails.Found)
        {
            string returnTypeNamespace = GetNamespaceForType(node.ReturnType, out var typeSymbol);
            FoundElements.Add(new FoundElementInfo(
                ElementType.FunctionReturnValue,
                _currentMethodName,
                "Return",
                node.ReturnType.ToString(),
                returnTypeNamespace,
                returnDetails.Value,
                typeSymbol
            ));
        }

        base.VisitMethodDeclaration(node);
        _currentMethodName = null;
    }
}

// 用于存储找到的元素信息的辅助类/记录
public enum ElementType
{
    StructField,
    FunctionParameter,
    FunctionReturnValue
}

public class FoundElementInfo
{
    public ElementType Type;
    public string ParentName; // 结构体名或方法名
    public string ElementName; // 字段名或参数名或 "Return"
    public string ElementManagedType; // 托管类型字符串 (原始文本)
    public string ElementManagedTypeNamespace; // 托管类型的命名空间
    public string NativeTypeNameValue; // NativeTypeNameAttribute 的值
    public ITypeSymbol TypeSymbol;
    public FoundElementInfo(ElementType type, string parentName, string elementName, string elementManagedType, string elementManagedTypeNamespace, string nativeTypeNameValue, ITypeSymbol typeSymbol)
    {
        Type = type;
        ParentName = parentName;
        ElementName = elementName;
        ElementManagedType = elementManagedType;
        ElementManagedTypeNamespace = elementManagedTypeNamespace;
        NativeTypeNameValue = nativeTypeNameValue;
        TypeSymbol = typeSymbol;
    }
    public override string ToString()
    {
        string namespaceInfo = string.IsNullOrEmpty(ElementManagedTypeNamespace) || ElementManagedTypeNamespace == "<global>"
            ? ""
            : $" (Namespace: {ElementManagedTypeNamespace})";

        return Type switch
        {
            ElementType.StructField =>
                $"Struct Field: {ParentName}.{ElementName} (Type: {ElementManagedType}{namespaceInfo}) -> NativeTypeName(\"{NativeTypeNameValue}\")",
            ElementType.FunctionParameter =>
                $"Function Parameter: {ParentName}(..., {ElementName} As {ElementManagedType}{namespaceInfo}, ...) -> NativeTypeName(\"{NativeTypeNameValue}\")",
            ElementType.FunctionReturnValue =>
                $"Function Return: {ParentName}() As {ElementManagedType}{namespaceInfo} -> NativeTypeName(\"{NativeTypeNameValue}\")",
            _ => base.ToString()
        };
    }
}
