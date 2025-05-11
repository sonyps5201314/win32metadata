// This adds the enclosing symbol name to the neseted type name.
// Can help disambiguate where a name came from when looking at a winmd
//#define MakeShortNameIncludeEnclosingName

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MetadataUtils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;

namespace ClangSharpSourceToWinmd
{
    public class ClangSharpSourceWinmdGenerator
    {
        public const string Win32WideStringType = "Windows.Win32.Foundation.PWSTR";
        public const string Win32StringType = "Windows.Win32.Foundation.PSTR";

        private const string ScannedSuffix = "__scanned__";
        private const string RemovePrefix = "__remove__";
        private const string ForceConstPrefix = "__forceconst__";

        private const string SystemAssemblyName = "netstandard";
        private const string Win32MetadataAssemblyName = "Windows.Win32.winmd";

        private static readonly Regex TypeImportRegex = new Regex(@"<(([^,]+),\s*Version=(\d+\.\d+\.\d+\.\d+),\s*Culture=([^,]+),\s*PublicKeyToken=([^>]+))>(\S+)");
        private static readonly Regex IsWcharRegex = new Regex(@"^(?:WCHAR|OLECHAR|wchar_t)");
        private static readonly Regex IsCharRegex = new Regex(@"^(?:CHAR|char)");
        private static readonly Regex ArrayMatcher = new Regex(@"\[\d*\]");
        private static readonly Regex NormalizeNativeTypeNameRegex = new Regex(@"((?:const )?(?:unsigned )?\S+)\s?(\*?(?:\[\d*\]))");

        private MetadataBuilder metadataBuilder = new MetadataBuilder();
        private CSharpCompilation compilation;
        private ModuleDefinitionHandle moduleRef;
        private Dictionary<string, TypeReferenceHandle> namesToTypeRefHandles = new Dictionary<string, TypeReferenceHandle>();
        private Dictionary<string, TypeDefinitionHandle> namesToTypeDefHandles = new Dictionary<string, TypeDefinitionHandle>();
        private Dictionary<string, MethodDefinitionHandle> namesToMethodDefHandles = new Dictionary<string, MethodDefinitionHandle>();
        private HashSet<TypeDeclarationSyntax> visitedPartialDefs = new HashSet<TypeDeclarationSyntax>();
        private HashSet<ISymbol> interfaceSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        private Dictionary<string, List<ISymbol>> namesToInterfaceSymbols = new Dictionary<string, List<ISymbol>>();
        private Dictionary<ITypeSymbol, FixedBufferInfo> fixedBufferTypeToInfo = new Dictionary<ITypeSymbol, FixedBufferInfo>(SymbolEqualityComparer.Default);
        private Dictionary<SyntaxTree, SemanticModel> treeToModels = new Dictionary<SyntaxTree, SemanticModel>();
        private HashSet<StructDeclarationSyntax> interfaceStructs = new HashSet<StructDeclarationSyntax>();
        private Dictionary<ISymbol, int> interfaceMethodCount = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        private Dictionary<string, EntityHandle> ctorNamesToRefs = new Dictionary<string, EntityHandle>();
        private Dictionary<string, ModuleReferenceHandle> moduleRefHandles = new Dictionary<string, ModuleReferenceHandle>();
        private List<GeneratorDiagnostic> diagnostics = new List<GeneratorDiagnostic>();
        private HashSet<string> typeImportInterfaces = new HashSet<string>();
        private Dictionary<string, string> typeImports = new Dictionary<string, string>();
        private HashSet<string> reducePointerLevels;
        private Dictionary<string, AssemblyReferenceHandle> assemblyNamesToRefHandles = new Dictionary<string, AssemblyReferenceHandle>();
        private Dictionary<string, ITypeSymbol> nameToSymbols = new Dictionary<string, ITypeSymbol>();
        private Dictionary<StructDeclarationSyntax, ISymbol> structNodesToInheritedSymbols = new Dictionary<StructDeclarationSyntax, ISymbol>();
        private Dictionary<string, FieldDeclarationSyntax> nameToGuidConstFields = new Dictionary<string, FieldDeclarationSyntax>();
        private HashSet<string> structNameWithGuids = new HashSet<string>();
        private Dictionary<string, List<INamedTypeSymbol>> namespaceToGuidOnlyStructSymbols = new Dictionary<string, List<INamedTypeSymbol>>();
        private HashSet<StructDeclarationSyntax> guidOnlyStructsConvertedToConsts = new HashSet<StructDeclarationSyntax>();
        private HashSet<string> forceGuidConsts;

        private ClangSharpSourceWinmdGenerator(
            CSharpCompilation compilation,
            Dictionary<string, string> typeImports,
            HashSet<string> reducePointerLevels,
            HashSet<string> forceGuidConsts,
            Version assemblyVersion,
            string assemblyName)
        {
            this.compilation = compilation;
            this.reducePointerLevels = reducePointerLevels;
            this.forceGuidConsts = forceGuidConsts;

            foreach (var pair in typeImports)
            {
                string name = pair.Key;
                if (name.EndsWith("(interface)"))
                {
                    name = pair.Key.Substring(0, pair.Key.Length - "(interface)".Length);
                    this.typeImportInterfaces.Add(name);
                }

                this.typeImports[name] = pair.Value;
            }

            VerifySymbolsLoadedByCompiler();
            InitReferences();
            InitAssembly(assemblyVersion, assemblyName);
            InitModule(assemblyName);

            void VerifySymbolsLoadedByCompiler()
            {
                string[] standardSymbolNames = new string[] { "System.Object", "System.Attribute" };

                foreach (var name in standardSymbolNames)
                {
                    var type = this.compilation.GetTypeByMetadataName(name);
                    if (type == null)
                    {
                        throw new InvalidOperationException($"The compiler failed to find the '{name}' type. Make sure the references are correct.");
                    }
                }
            }

            void InitAssembly(Version version, string assemblyName)
            {
                this.metadataBuilder.AddAssembly(
                    this.metadataBuilder.GetOrAddString(assemblyName),
                    version,
                    default,
                    default,
                    default,
                    hashAlgorithm: AssemblyHashAlgorithm.None);
            }

            void InitModule(string assemblyName)
            {
                this.moduleRef =
                    this.metadataBuilder.AddModule(
                        0,
                        this.metadataBuilder.GetOrAddString(assemblyName),
                        this.metadataBuilder.GetOrAddGuid(Guid.NewGuid()),
                        default,
                        default);

                this.metadataBuilder.AddTypeDefinition(
                    default,
                    default,
                    this.metadataBuilder.GetOrAddString("<Module>"),
                    baseType: default(EntityHandle),
                    fieldList: MetadataTokens.FieldDefinitionHandle(1),
                    methodList: MetadataTokens.MethodDefinitionHandle(1));

            }

            void InitReferences()
            {
                foreach (var assembly in this.compilation.ReferencedAssemblyNames)
                {
                    var assemblyRef =
                        this.metadataBuilder.AddAssemblyReference(
                            this.metadataBuilder.GetOrAddString(assembly.Name),
                            assembly.Version,
                            default,
                            this.metadataBuilder.GetOrAddBlob(assembly.PublicKeyToken),
                            default,
                            default);
                    this.assemblyNamesToRefHandles[assembly.Name] = assemblyRef;
                }
            }
        }

        public bool WroteWinmd { get; private set; }

        public static ClangSharpSourceWinmdGenerator GenerateWindmdForCompilation(
            ClangSharpSourceCompilation compilation,
            Dictionary<string, string> typeImports,
            HashSet<string> reducePointerLevels,
            HashSet<string> forceGuidConsts,
            Version version,
            string outputFileName)
        {
            ClangSharpSourceWinmdGenerator generator =
                new ClangSharpSourceWinmdGenerator(
                    compilation.CSharpCompilation,
                    typeImports,
                    reducePointerLevels,
                    forceGuidConsts,
                    version,
                    Path.GetFileName(outputFileName));

            generator.PopulateMetadataBuilder();

            if (!generator.diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                generator.WriteWinmd(outputFileName);
            }

            return generator;
        }

        public ReadOnlyCollection<GeneratorDiagnostic> GetDiagnostics()
        {
            return this.diagnostics.AsReadOnly();
        }

        private static string FixFinalName(string name)
        {
            if (name.StartsWith(RemovePrefix))
            {
                name = name.Substring(RemovePrefix.Length);
            }
            else if (name.StartsWith(ForceConstPrefix))
            {
                name = name.Substring(ForceConstPrefix.Length);
            }

            if (name.EndsWith(ScannedSuffix))
            {
                name = name.Substring(0, name.Length - ScannedSuffix.Length);
            }

            return name;
        }

        private static byte[] ConvertKeyToByteArray(string key)
        {
            byte[] ret = new byte[key.Length / 2];
            for (int i = 0; i < ret.Length; i++)
            {
                string item = key.Substring(i * 2, 2);
                ret[i] = byte.Parse(item, System.Globalization.NumberStyles.HexNumber);
            }

            return ret;
        }

        private static string GetQualifiedName(string @namespace, string name)
        {
            return $"{@namespace}.{name}";
        }

        private static bool HasGuidAttribute(SyntaxList<AttributeListSyntax> attributeLists)
        {
            bool ret = attributeLists.Any(list => list.Attributes.Any(attr => attr.Name.ToString() == EncodeHelpers.GuidAttributeName));
            return ret;
        }

        private static bool HasConstantAttribute(SyntaxList<AttributeListSyntax> attributeLists)
        {
            return attributeLists.Any(list => list.Attributes.Any(attr => attr.Name.ToString() == "Constant"));
        }

        private static string FixArchSpecificName(string name)
        {
            if (name.EndsWith("____1") || name.EndsWith("____2"))
            {
                name = name.Substring(0, name.Length - "____1".Length);
            }

            return name;
        }

        private static (int, int) GetSizeAndSignFromEnumType(INamedTypeSymbol underlyingType)
        {
            int enumSize;
            int enumSign;
            switch (underlyingType.SpecialType)
            {
                case SpecialType.System_Byte:
                    enumSize = 1;
                    enumSign = 1;
                    break;
                case SpecialType.System_SByte:
                    enumSize = 1;
                    enumSign = -1;
                    break;

                case SpecialType.System_UInt16:
                    enumSize = 2;
                    enumSign = 1;
                    break;
                case SpecialType.System_Int16:
                    enumSize = 2;
                    enumSign = -1;
                    break;

                case SpecialType.System_UInt32:
                    enumSize = 4;
                    enumSign = 1;
                    break;
                case SpecialType.System_Int32:
                    enumSize = 4;
                    enumSign = -1;
                    break;

                case SpecialType.System_UInt64:
                    enumSize = 8;
                    enumSign = 1;
                    break;
                case SpecialType.System_Int64:
                    enumSize = 8;
                    enumSign = -1;
                    break;

                default:
                    throw new InvalidOperationException($"Enum type {underlyingType} not handled in EnsureEnumSizeMatchesOriginalSize.");
            }

            return (enumSize, enumSign);
        }

        /// <summary>
        /// Makes sure that if the type has pointer info (e.g. * or [] or *[]), the type
        /// name is separated by a space
        /// </summary>
        /// <param name="ownerAttributes">Owner attributes</param>
        /// <returns>Normalized native type name</returns>
        private static string GetNormalizedNativeTypeName(ImmutableArray<AttributeData> ownerAttributes)
        {
            var nativeTypeNameAttr = ownerAttributes.FirstOrDefault(a => a.AttributeClass.Name == "NativeTypeNameAttribute");
            if (nativeTypeNameAttr == null)
            {
                return null;
            }

            var nativeType = nativeTypeNameAttr.ConstructorArguments[0].Value.ToString();
            var match = NormalizeNativeTypeNameRegex.Match(nativeType);
            if (match.Success && !string.IsNullOrEmpty(match.Groups[2].Value))
            {
                nativeType = match.Groups[1].Value + " " + match.Groups[2].Value;
            }

            return nativeType;
        }

        private void EnsureEnumSizeMatchesOriginalSize(string parent, string name, ITypeSymbol type, string nativeType, out bool wasEnum)
        {
            wasEnum = false;

            INamedTypeSymbol underlyingType;
            if (type is INamedTypeSymbol namedType &&
                (underlyingType = namedType.EnumUnderlyingType) != null)
            {
                wasEnum = true;

                if (nativeType != null)
                {
                    int nativeSize;
                    int nativeSign = nativeType.StartsWith("unsigned", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                    nativeType = nativeType.Replace("const ", string.Empty);
                    nativeType = nativeType.Replace("enum ", string.Empty);
                    nativeType = nativeType.Replace("unsigned ", string.Empty);
                    switch (nativeType.ToUpperInvariant())
                    {
                        case "UINT8":
                        case "CHAR":
                        case "BYTE":
                            nativeSize = 1;
                            nativeSign = 1;
                            break;

                        case "INT8":
                        case "SBYTE":
                            nativeSize = 1;
                            nativeSign = nativeSign == 0 ? -1 : nativeSign;
                            break;

                        case "UINT16":
                        case "USHORT":
                        case "WORD":
                            nativeSize = 2;
                            nativeSign = 1;
                            break;

                        case "INT16":
                        case "SHORT":
                            nativeSize = 2;
                            nativeSign = nativeSign == 0 ? -1 : nativeSign;
                            break;

                        case "UINT32":
                        case "UINT":
                        case "ULONG32":
                        case "ULONG":
                        case "DWORD32":
                        case "DWORD":
                            nativeSize = 4;
                            nativeSign = 1;
                            break;

                        case "LONG":
                        case "LONG32":
                        case "INT":
                        case "INT32":
                        case "BOOL":
                            nativeSize = 4;
                            nativeSign = nativeSign == 0 ? -1 : nativeSign;
                            break;

                        case "DWORDLONG":
                        case "DWORD64":
                        case "ULONG64":
                        case "UINT64":
                            nativeSize = 8;
                            nativeSign = 1;
                            break;

                        case "INT64":
                        case "__INT64":
                            nativeSize = 8;
                            nativeSign = nativeSign == 0 ? -1 : nativeSign;
                            break;

                        default:
                            var nativeTypeSymbol = this.GetTypeFromShortName(nativeType);
                            if (nativeTypeSymbol != null && nativeTypeSymbol is INamedTypeSymbol nativeNamedTypeSymbol && nativeNamedTypeSymbol.EnumUnderlyingType != null)
                            {
                                (nativeSize, nativeSign) = GetSizeAndSignFromEnumType(nativeNamedTypeSymbol.EnumUnderlyingType);
                            }
                            else
                            {
                                // TODO: If the generator had the original remappings, it could tell if this was just a rename and
                                // ignore it
                                System.Diagnostics.Debug.WriteLine($"{parent}.{name} using enum {type} but the size of its native type {nativeType} can't be determined.");
                                return;
                            }

                            break;
                    }

                    (int enumSize, int enumSign) = GetSizeAndSignFromEnumType(underlyingType);

                    if (enumSize != nativeSize || enumSign != nativeSign)
                    {
                        // Allow functions to return WIN32_ERROR even if the return type isn't uint. Otherwise, fail the build.
                        if (!(name == "return" && namedType.Name == "WIN32_ERROR"))
                        {
                            throw new InvalidOperationException(
                                $"{parent}.{name} was remapped to enum {namedType} (type {underlyingType}, size {enumSize}) but the original field was of type {nativeType} (size {nativeSize}). Either don't use an enum or make sure the enum is of the same size and sign.");
                        }
                    }
                }
            }
        }

        private void CacheGuidConst(FieldDeclarationSyntax guidFieldNode)
        {
            this.nameToGuidConstFields[guidFieldNode.Declaration.Variables.First().Identifier.ValueText] = guidFieldNode;
        }

        private void CacheInterfaceInfo(InterfaceInfo interfaceInfo)
        {
            string fullName = $"{interfaceInfo.Namespace}.{interfaceInfo.Name}";
            var symbol = this.compilation.GetTypeByMetadataName(fullName);
            if (symbol != null)
            {
                this.interfaceSymbols.Add(symbol);
                this.interfaceMethodCount[symbol] = interfaceInfo.ImplementedMethodCount;
                if (!this.namesToInterfaceSymbols.TryGetValue(symbol.Name, out var symbols))
                {
                    symbols = new List<ISymbol>();
                    this.namesToInterfaceSymbols[symbol.Name] = symbols;
                }

                symbols.Add(symbol);
            }
        }

        private void CacheGuidOnlyStruct(StructDeclarationSyntax structNode)
        {
            if (!this.forceGuidConsts.Contains(structNode.Identifier.ValueText))
            {
                return;
            }

            var model = this.GetModel(structNode);
            var symbol = model.GetDeclaredSymbol(structNode);

            var symbolNamespace = symbol.ContainingNamespace.ToString();
            if (!this.namespaceToGuidOnlyStructSymbols.TryGetValue(symbolNamespace, out var symbols))
            {
                symbols = new List<INamedTypeSymbol>();
                this.namespaceToGuidOnlyStructSymbols[symbolNamespace] = symbols;
            }

            symbols.Add(symbol);

            this.guidOnlyStructsConvertedToConsts.Add(structNode);
        }

        private void CacheInterfaceType(StructDeclarationSyntax interfaceNode)
        {
            this.interfaceStructs.Add(interfaceNode);

            var model = this.GetModel(interfaceNode);
            var symbol = model.GetDeclaredSymbol(interfaceNode);
            this.interfaceSymbols.Add(symbol);
            this.interfaceMethodCount[symbol] = interfaceNode.Members.Count(m => m is MethodDeclarationSyntax);

            if (!this.namesToInterfaceSymbols.TryGetValue(symbol.Name, out var symbols))
            {
                symbols = new List<ISymbol>();
                this.namesToInterfaceSymbols[symbol.Name] = symbols;
            }

            symbols.Add(symbol);
        }

        private void WriteWinmd(string outputFileName)
        {
            var peBuilder = new ManagedPEBuilder(
                PEHeaderBuilder.CreateLibraryHeader(),
                new MetadataRootBuilder(this.metadataBuilder),
                ilStream: new BlobBuilder());

            var peBlob = new BlobBuilder();
            peBuilder.Serialize(peBlob);

            string dir = Path.GetDirectoryName(outputFileName);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (FileStream fileStream = new FileStream(outputFileName, FileMode.Create, FileAccess.ReadWrite))
            {
                peBlob.WriteContentTo(fileStream);
            }

            this.WroteWinmd = true;
        }

        private void PopulateMetadataBuilder()
        {
            foreach (var tree in this.compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var model = this.GetModel(root);
                var collector = new NativeTypeNameCollector(model);
                collector.Visit(root);
                if (collector.FoundElements.Any())
                {
                    foreach (var info in collector.FoundElements)
                    {
                        this.RemapToMoreSpecificTypeIfPossible_Internal(false, info.ParentName, info.ElementName, info.NativeTypeNameValue, info.ElementManagedTypeNamespace, info.ElementManagedType, ref info.TypeSymbol);
                    }
                }
            }

            var code = dependTypedefStructsCode.ToString();
            var fileName = @"z:\dependTypedefStructsCode.cs";
            File.WriteAllText(fileName, code);
            var syntaxTree = CSharpSyntaxTree.ParseText(code, null, fileName);
            this.compilation = this.compilation.AddSyntaxTrees(syntaxTree);

            // 检查编译诊断信息 (可选，但有助于调试)
            var diagnostics = this.compilation.GetDiagnostics();
            foreach (var diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                string line = string.Empty;
                var pos = diag.Location.GetLineSpan();
                if (pos.Path != null)
                {
                    if (pos.Path == fileName)
                    {
                        line += pos.Path + " (" + (pos.StartLinePosition.Line + 1) + "," + (pos.StartLinePosition.Character + 1) + ")";
                        Debug.WriteLine($"{line}: {diag.GetMessage()}");
                    }
                }
            }

            GatherInterfaceTypes();

            VisitAllTypes();

            void VisitAllTypes()
            {
                SyntaxWalker walker = new SyntaxWalker(this);
                foreach (var tree in this.compilation.SyntaxTrees)
                {
                    walker.Visit(tree.GetRoot());
                }
            }

            void GatherInterfaceTypes()
            {
                InterfaceWalker interfaceWalker = new InterfaceWalker(this);
                foreach (var tree in this.compilation.SyntaxTrees)
                {
                    interfaceWalker.Visit(tree.GetRoot());
                }

                var allWinmds = this.compilation.References.Where(r => !r.Display.EndsWith("netstandard.dll")).Select(r => MetadataUtils.WinmdUtils.LoadFromFile(r.Display)).ToList();

                foreach (var winmd in allWinmds)
                {
                    foreach (var typeInfo in winmd.GetTypes(allWinmds))
                    {
                        if (typeInfo is MetadataUtils.InterfaceInfo interfaceInfo)
                        {
                            this.CacheInterfaceInfo(interfaceInfo);
                        }
                        else if (typeInfo is MetadataUtils.StructInfo)
                        {
                            var fullName = $"{typeInfo.Namespace}.{typeInfo.Name}";
                            var typeSymbol = this.compilation.GetTypeByMetadataName(fullName);

                            if (typeSymbol != null)
                            {
                                this.nameToSymbols[typeInfo.Name] = typeSymbol;
                            }
                        }
                        else if (typeInfo is MetadataUtils.DelegateTypeInfo)
                        {
                            var fullName = $"{typeInfo.Namespace}.{typeInfo.Name}";
                            var typeSymbol = this.compilation.GetTypeByMetadataName(fullName);

                            if (typeSymbol != null)
                            {
                                this.nameToSymbols[typeInfo.Name] = typeSymbol;
                            }
                        }
                    }
                }

                foreach (var winmd in allWinmds)
                {
                    winmd.Dispose();
                }
            }
        }

        private EntityHandle GetTypeReference(ISymbol symbol)
        {
            EntityHandle scopeRef;

            if (symbol.ContainingAssembly.Name == "?")
            {
                scopeRef = this.moduleRef;
            }
            else
            {
                this.assemblyNamesToRefHandles.TryGetValue(symbol.ContainingAssembly.Name, out var assemblyRef);

                scopeRef = assemblyRef;
            }

            // Change from namepace.parent.self => parent/self
            string name = symbol.ToString().Substring(symbol.ContainingNamespace.ToString().Length + 1).Replace('.', '/');

            return this.GetTypeReference(symbol.ContainingNamespace.ToString(), name, scopeRef);
        }

        private EntityHandle GetTypeReference(string @namespace, string name)
        {
            return this.GetTypeReference(@namespace, name, default);
        }

        private EntityHandle GetTypeReference(string @namespace, string name, EntityHandle scopeRef)
        {
            name = FixArchSpecificName(name);

            string fullname = GetQualifiedName(@namespace, name);
            if (!this.namesToTypeRefHandles.TryGetValue(fullname, out var typeRef))
            {
                if (scopeRef == default)
                {
                    if (@namespace.StartsWith("System"))
                    {
                        scopeRef = this.assemblyNamesToRefHandles[SystemAssemblyName];
                    }
                    else
                    {
                        scopeRef = this.moduleRef;
                    }
                }

                int lastPlus = name.LastIndexOf('/');
                if (lastPlus != -1)
                {
                    string parent = name.Substring(0, lastPlus);
                    name = name.Substring(lastPlus + 1);
                    scopeRef = this.GetTypeReference(@namespace, parent);

                    // No longer using the namespace name when a dervied ref
                    @namespace = null;
                }

                var nsHandle = @namespace != null ? this.metadataBuilder.GetOrAddString(@namespace) : default;
                typeRef = this.metadataBuilder.AddTypeReference(
                    scopeRef,
                    nsHandle,
                    this.metadataBuilder.GetOrAddString(name));
                this.namesToTypeRefHandles[fullname] = typeRef;
            }

            return typeRef;
        }

        private SemanticModel GetModel(SyntaxNode node)
        {
            if (!this.treeToModels.TryGetValue(node.SyntaxTree, out SemanticModel model))
            {
                model = this.compilation.GetSemanticModel(node.SyntaxTree, true);
                this.treeToModels[node.SyntaxTree] = model;
            }

            return model;
        }

        private void WriteEnumDef(EnumDeclarationSyntax node)
        {
            this.WriteEnumDef(node, default);
        }

        private void WriteEnumDef(EnumDeclarationSyntax node, TypeDefinitionHandle enclosingType)
        {
            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            string fullName = this.GetFullNameForSymbol(symbol);

            if (this.namesToTypeDefHandles.ContainsKey(fullName))
            {
                return;
            }

            // Add special value__ field for enums
            FieldAttributes valueFieldAttrs = FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName;
            var fieldSignature = new BlobBuilder();
            var encoder = new BlobEncoder(fieldSignature);
            encoder.FieldSignature().EncodeSpecialType(symbol.EnumUnderlyingType.SpecialType);

            var valueFieldDef =
                this.metadataBuilder.AddFieldDefinition(
                    valueFieldAttrs,
                    this.metadataBuilder.GetOrAddString("value__"),
                    this.metadataBuilder.GetOrAddBlob(fieldSignature));

            this.WriteEnumMembers(node);

            // Write type def
            TypeAttributes typeAttributes = TypeAttributes.Public | TypeAttributes.AnsiClass | TypeAttributes.Sealed | TypeAttributes.AutoLayout;
            var destTypeDefHandle =
                this.metadataBuilder.AddTypeDefinition(
                    typeAttributes,
                    this.metadataBuilder.GetOrAddString(symbol.ContainingNamespace.ToString()),
                    this.metadataBuilder.GetOrAddString(FixArchSpecificName(node.Identifier.ValueText)),
                    this.GetTypeReference("System", "Enum"),
                    fieldList: valueFieldDef,
                    methodList: MetadataTokens.MethodDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1));

            this.AddCustomAttributes(node, destTypeDefHandle);

            if (enclosingType != default)
            {
                this.metadataBuilder.AddNestedType(destTypeDefHandle, enclosingType);
            }

            this.namesToTypeDefHandles[fullName] = destTypeDefHandle;
        }

        private bool IsInterfaceDef(StructDeclarationSyntax node)
        {
            if (this.interfaceStructs.Contains(node))
            {
                return true;
            }

            bool hasVtbl = node.Members.Any(m => m.Kind() == SyntaxKind.FieldDeclaration && ((FieldDeclarationSyntax)m).Declaration.Variables.First().Identifier.Text == "lpVtbl");
            bool hasDelegate = node.Members.Any(m => m.Kind() == SyntaxKind.DelegateDeclaration);
            return hasVtbl && hasDelegate;
        }

        private void WriteInterfaceDef(StructDeclarationSyntax node)
        {
            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            var nsHandle = this.metadataBuilder.GetOrAddString(symbol.ContainingNamespace.ToString());
            var name = node.Identifier.ValueText;
            string fullName = symbol.ConstructedFrom.ToString();

            MethodDefinitionHandle firstMethod = this.WriteInterfaceMethods(node);
            if (firstMethod.IsNil)
            {
                firstMethod = MetadataTokens.MethodDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1);
            }

            TypeAttributes typeAttributes = TypeAttributes.Interface | TypeAttributes.Public | TypeAttributes.AnsiClass | TypeAttributes.Abstract;
            var destTypeDefHandle =
                this.metadataBuilder.AddTypeDefinition(
                    typeAttributes,
                    nsHandle,
                    this.metadataBuilder.GetOrAddString(name),
                    default,
                    fieldList: MetadataTokens.FieldDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.Field) + 1),
                    methodList: firstMethod);

            this.namesToTypeDefHandles[fullName] = destTypeDefHandle;

            var inheritsFromSymbol = this.GetInheritedInterfaceSymbol(node);

            // Can be null if it's a non-IUnknown interface
            if (inheritsFromSymbol != null)
            {
                var inheritsFromTypeDef = this.GetTypeReference(inheritsFromSymbol);
                this.metadataBuilder.AddInterfaceImplementation(destTypeDefHandle, inheritsFromTypeDef);
            }

            // If this interface node doesn't have a Guid attribute, see if we can 
            // match it to a Guid constant
            if (!HasGuidAttribute(node.AttributeLists))
            {
                string iidGuidConstName = $"IID_{name}";
                if (!this.nameToGuidConstFields.TryGetValue(iidGuidConstName, out var fieldDeclNode))
                {
                    iidGuidConstName += ScannedSuffix;
                    this.nameToGuidConstFields.TryGetValue(iidGuidConstName, out fieldDeclNode);
                }

                if (fieldDeclNode != null)
                {
                    ISymbol fieldSymbol = this.compilation.GetSymbolsWithName(iidGuidConstName).FirstOrDefault();

                    if (fieldSymbol != null)
                    {
                        this.AddCustomAttributes(fieldSymbol, destTypeDefHandle);
                    }
                }
            }

            this.AddCustomAttributes(node, destTypeDefHandle);
        }

        private void WriteStructDef(StructDeclarationSyntax node)
        {
            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            string fullName = this.GetFullNameForSymbol(symbol);

            if (this.namesToTypeDefHandles.ContainsKey(fullName))
            {
                return;
            }

            if (this.guidOnlyStructsConvertedToConsts.Contains(node))
            {
                return;
            }

            if (this.IsInterfaceDef(node))
            {
                this.WriteInterfaceDef(node);
                return;
            }

            this.WriteStructDef(node, default);
        }

        private void AddFixedBufferTypeInfoForStructNode(StructDeclarationSyntax node)
        {
            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            var fields = node.Members.Where(m => m is FieldDeclarationSyntax);
            var firstFieldNode = (FieldDeclarationSyntax)fields.First();
            var variable = firstFieldNode.Declaration.Variables.First();
            var type = model.GetTypeInfo(firstFieldNode.Declaration.Type).Type;
            this.fixedBufferTypeToInfo[symbol] = new FixedBufferInfo(type, fields.Count());
        }

        private void WriteStructDef(StructDeclarationSyntax node, TypeDefinitionHandle enclosingType)
        {
            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            string fullName = this.GetFullNameForSymbol(symbol);

            // Don't emit structs that end in _e__FixedBuffer. These are emitted by ClangSharp instead of arrays of structs
            // because C# can't do fixed length arrays of structs. Cache the type so when we emit the field it has the
            // type handy
            if (fullName.EndsWith("_e__FixedBuffer"))
            {
                this.AddFixedBufferTypeInfoForStructNode(node);
                return;
            }

            TypeAttributes typeAttributes = TypeAttributes.AnsiClass | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
            if (!enclosingType.IsNil)
            {
                typeAttributes |= TypeAttributes.NestedPublic;
            }
            else
            {
                typeAttributes |= TypeAttributes.Public;
            }

            int? packSize = null;
            var structLayoutAttr = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass.Name == "StructLayoutAttribute");
            if (structLayoutAttr != null)
            {
                var arg = (System.Runtime.InteropServices.LayoutKind)structLayoutAttr.ConstructorArguments[0].Value;
                typeAttributes |= arg == System.Runtime.InteropServices.LayoutKind.Explicit ? TypeAttributes.ExplicitLayout : TypeAttributes.SequentialLayout;

                foreach (var nameVal in structLayoutAttr.NamedArguments.Where(n => n.Key == "Pack"))
                {
                    packSize = (int)nameVal.Value.Value;
                    break;
                }
            }
            else
            {
                typeAttributes |= TypeAttributes.SequentialLayout;
            }

            var firstField = this.WriteStructFields(node);
            if (firstField.IsNil)
            {
                firstField = MetadataTokens.FieldDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.Field) + 1);
            }

            var nsHandle = enclosingType == default ? this.metadataBuilder.GetOrAddString(symbol.ContainingNamespace.ToString()) : default;
            var name = this.GetShortNameForSymbol(symbol);

            name = FixArchSpecificName(name);

            var destTypeDefHandle =
                this.metadataBuilder.AddTypeDefinition(
                    typeAttributes,
                    nsHandle,
                    this.metadataBuilder.GetOrAddString(name),
                    this.GetTypeReference("System", "ValueType"),
                    fieldList: firstField,
                    methodList: MetadataTokens.MethodDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1));

            this.namesToTypeDefHandles[fullName] = destTypeDefHandle;

            this.WriteStructEnums(node, destTypeDefHandle);

            if (packSize.HasValue)
            {
                this.metadataBuilder.AddTypeLayout(destTypeDefHandle, (ushort)packSize.Value, 0);
            }

            this.AddCustomAttributes(node, destTypeDefHandle);

            if (enclosingType != default)
            {
                this.metadataBuilder.AddNestedType(destTypeDefHandle, enclosingType);
            }

            foreach (StructDeclarationSyntax nested in node.Members.Where(m => m is StructDeclarationSyntax))
            {
                this.WriteStructDef(nested, destTypeDefHandle);
            }
        }

        private void EncodeReturnType(ITypeSymbol returnType, ReturnTypeEncoder returnTypeEncoder)
        {
            if (returnType == null || returnType.SpecialType == SpecialType.System_Void)
            {
                returnTypeEncoder.Void();
                return;
            }

            this.EncodeTypeSymbol(returnType, returnTypeEncoder.Type());
        }

        private void EncodeParameters(IEnumerable<Parameter> parameters, ParametersEncoder parametersEncoder)
        {
            foreach (var parameter in parameters)
            {
                var parameterTypeEncoder = parametersEncoder.AddParameter();
                this.EncodeTypeSymbol(parameter.Type, parameterTypeEncoder.Type());
            }
        }

        private ITypeSymbol GetTypeFromShortName(string name, bool returnDirectlyIfNotFound = false)
        {
            var origName = name;

            // If it's an array, treat it like a pointer
            var arrayMatch = ArrayMatcher.Match(name);
            if (arrayMatch.Success)
            {
                name = name.Substring(0, arrayMatch.Captures[0].Index) + "*";
            }

            if (!this.nameToSymbols.TryGetValue(name, out var ret))
            {
                string fixedName = origName;

                switch (name)
                {
                    case "PCHAR":
                    case "LPCH":
                    case "PCH":
                    case "LPCCH":
                    case "PCCH":
                    case "NPSTR":
                    case "LPSTR":
                    case "PSTR":
                    case "LPCSTR":
                    case "PCSTR":
                    case "PZZSTR":
                    case "CPZZSTR":
                    case "CHAR *":
                    case "char *":
                    case "const CHAR *":
                    case "const char *":
                    case "DEVINSTID_A":
                        fixedName = Win32StringType;
                        break;

                    case "PWCHAR":
                    case "LPWCH":
                    case "PWCH":
                    case "NWPSTR":
                    case "LPWSTR":
                    case "PWSTR":
                    case "LPOLESTR":
                    case "WCHAR *":
                    case "wchar_t *":
                    case "LPCWSTR":
                    case "PCWSTR":
                    case "LPCWCH":
                    case "LPCOLESTR":
                    case "OLECHAR *":
                    case "const WCHAR *":
                    case "const wchar_t *":
                    case "PZZWSTR":
                    case "PCZZWSTR":
                    case "PNZWCH":
                    case "PCNZWCH":
                    case "DEVINSTID_W":
                        fixedName = Win32WideStringType;
                        break;

                    default:
                        break;
                }

                if (!fixedName.Contains("."))
                {
                    foreach (string @namespace in new string[] { "System" })
                    {
                        var fullNameToCheck = GetQualifiedName(@namespace, fixedName);
                        ret = this.compilation.GetTypeByMetadataName(fullNameToCheck);
                        if (ret != null)
                        {
                            break;
                        }
                    }

                    if (ret == null)
                    {
                        ret = this.compilation.GetSymbolsWithName(fixedName, SymbolFilter.Type).FirstOrDefault() as ITypeSymbol;
                    }
                }
                else
                {
                    ret = this.compilation.GetTypeByMetadataName(fixedName);
                }

                if (ret == null)
                {
                    if (returnDirectlyIfNotFound)
                    {
                        return ret;
                    }
                }

                // Use the original name and not the fixed name to cache it
                this.nameToSymbols[name] = ret;
            }

            return ret;
        }

        StringBuilder dependTypedefStructsCode = new StringBuilder(
@$"using System;
using Windows.Win32.Foundation.Metadata;
");
        Dictionary<string, string> dependTypedefStructsMap = new Dictionary<string, string>();
        void CheckForOtherTypeCanBeMapped(string nativeType, string ns, string typeName, ITypeSymbol typeSymbol)
        {
            if (nativeType != typeName && nativeType != "GUID" && typeName != "void")
            {
                //if (nativeType == "PDISPATCHERQUEUECONTROLLER")
                //{
                //    int x = 0;
                //}
                var key = ns + "." + nativeType;
                var found = this.dependTypedefStructsMap.TryGetValue(key, out _) || this.GetTypeFromShortName(nativeType, true) != null;
                if (!found)
                {
                    var fullTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
                    // typeSymbol.ToDisplayString返回的类型名不带末尾的指针，所以我们像下面这样手动处理下
                    var dpos = fullTypeName.LastIndexOf('.');
                    if (dpos != -1)
                    {
                        fullTypeName = fullTypeName.Substring(0, dpos + 1) + typeName;
                    }
                    else
                    {
                        fullTypeName = typeName;
                    }
                    var code =
@$"namespace {ns}
{{
    [NativeTypedef]
    public unsafe struct {nativeType}
    {{
        public {fullTypeName} Value;
    }}
}}
";
                    this.dependTypedefStructsCode.Append(code);
                    this.dependTypedefStructsMap.Add(key, typeName);

                    //Console.WriteLine($"typedef {nativeType} {typeName};");
                }
            }
        }

        // 使用 HashSet 以获得 O(1) 的平均查找复杂度
        private static readonly HashSet<string> _basicCppTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            // C/C++ 标准类型关键字
            "void",
            "bool",

            "char",
            "signed char",
            "unsigned char",

            "short", // 等同于 signed short, short int, signed short int
            "short int",
            "signed short",
            "signed short int",
            "unsigned short",
            "unsigned short int",

            "int",   // 等同于 signed, signed int
            "signed", // 通常作为 signed int 的简写
            "signed int",
            "unsigned", // 通常作为 unsigned int 的简写
            "unsigned int",

            "long",  // 等同于 signed long, long int, signed long int
            "long int",
            "signed long",
            "signed long int",
            "unsigned long",
            "unsigned long int",

            "long long", // 等同于 signed long long, long long int, signed long long int
            "long long int",
            "signed long long",
            "signed long long int",
            "unsigned long long",
            "unsigned long long int",

            "float",
            "double",
            "long double",

            "wchar_t",

            // C++11 及更高版本的类型关键字
            "char16_t", // C++11
            "char32_t", // C++11
            "nullptr_t",// C++11
            "char8_t",  // C++20 (有些编译器可能更早支持作为扩展)

            // Microsoft (MSVC) 编译器内建类型关键字
            // 这些通常带有双下划线
            "__int8",
            "signed __int8", // __int8 默认为 signed
            "unsigned __int8",

            "__int16",
            "signed __int16", // __int16 默认为 signed
            "unsigned __int16",

            "__int32",
            "signed __int32", // __int32 默认为 signed
            "unsigned __int32",

            "__int64",
            "signed __int64", // __int64 默认为 signed
            "unsigned __int64",

            "__wchar_t" // MSVC有时也用这个，尽管wchar_t是标准的
            // 注意：有些编译器可能有更多特定的内建类型，这里列出的是常见的。
        };

        /// <summary>
        /// 判断给定的原生类型名称字符串是否 *不是* C++ 的基本类型关键字。
        /// 包括标准类型、特定编译器的内建类型（如 __int8）和较新C++版本的类型关键字。
        /// </summary>
        /// <param name="nativeType">要检查的C++类型名称 (例如 "int", "unsigned __int64", "MyClass")。</param>
        /// <returns>如果 nativeType *不是* C++ 基本类型关键字，则返回 true；如果是，则返回 false。</returns>
        public static bool IsNotBasicCppType(string nativeType)
        {
            return !_basicCppTypes.Contains(nativeType);
        }

        public static bool IsBasicCppTypePrefix(string nativeType)
        {
            string[] _basicCppTypePrefixs =
            {
                "unsigned",
                "signed",
                "long",
                "short"
            };
            return _basicCppTypePrefixs.Contains(nativeType);
        }

        public static string CleanNativeType(string nativeType)
        {
            string[] qualifierPrefixesToStrip =
            {
                "const ",
                "volatile ",
                "__unaligned "
            };

            foreach (var prefix in qualifierPrefixesToStrip)
            {
                if (nativeType.StartsWith(prefix))
                {
                    nativeType = nativeType.Substring(prefix.Length);
                }
            }

            string[] typePrefixesToStrip =
            {
                "struct ",
                "union ",
                "enum ",
            };

            foreach (var prefix in typePrefixesToStrip)
            {
                if (nativeType.StartsWith(prefix))
                {
                    nativeType = nativeType.Substring(prefix.Length);
                    break;
                }
            }

            var pos = nativeType.LastIndexOf("::");
            if (pos > -1)
            {
                nativeType = nativeType.Substring(pos + 2);
            }

            return nativeType;
        }

        private static readonly HashSet<string> _CsTypeNotCppTypes = new HashSet<string>(StringComparer.Ordinal)
        {
                "byte",
                "sbyte",
                "decimal",
                "uint",
                "ulong",
                "ushort",
                "object",
                "string",
                "dynamic",
                "nint",
                "nuint",
         };
        public static bool IsCsTypeNotCppType(string nativeType)
        {
            return _CsTypeNotCppTypes.Contains(nativeType);
        }

        private void RemapToMoreSpecificTypeIfPossible(
            string owner,
            string name,
            ImmutableArray<AttributeData> ownerAttributes,
            ref ITypeSymbol typeSymbol)
        {
            var nativeType = GetNormalizedNativeTypeName(ownerAttributes);

            // Can't do anything without a NativeTypeNameAttribute 
            if (nativeType == null)
            {
                return;
            }

            this.EnsureEnumSizeMatchesOriginalSize(owner, name, typeSymbol, nativeType, out var wasEnum);

            // Don't try to fix up an enum
            if (wasEnum)
            {
                return;
            }

            if (typeSymbol.SpecialType == SpecialType.System_SByte && IsCharRegex.IsMatch(nativeType))
            {
                typeSymbol = this.GetTypeFromShortName("CHAR");
                return;
            }

            if (typeSymbol.SpecialType == SpecialType.System_UInt16 && IsWcharRegex.IsMatch(nativeType))
            {
                typeSymbol = this.GetTypeFromShortName("System.Char");
                return;
            }

            var typeName = typeSymbol.ToString();
            RemapToMoreSpecificTypeIfPossible_Internal(true, owner, name, nativeType, null, typeName, ref typeSymbol);
        }

        private void RemapToMoreSpecificTypeIfPossible_Internal(
            bool forRemap,
            string owner,
            string name,
            string nativeType,
            string ns,
            string typeName,
            ref ITypeSymbol typeSymbol)
        {
            if (nativeType.Contains("(*)") || nativeType.Contains("(void *)"))
            {
                string fullName = $"{owner}.{name}";
                //this.diagnostics.Add(new GeneratorDiagnostic($"{fullName} is a pointer to a function: {nativeType}. To express this properly in metadata, define a delegate and map {fullName} to use it.", DiagnosticSeverity.Warning));
                return;
            }

            if (IsCsTypeNotCppType(nativeType))
            {
                return;
            }

            if (nativeType.StartsWith("__AnonymousRecord_") || typeName.EndsWith("_e__FixedBuffer") || typeName.Contains("_e__Struct"))
            {
                return;
            }


            /*
            bool IsSomeBasicCsType(ITypeSymbol typeSymbol, string typeName)
            {
                return typeSymbol.SpecialType == SpecialType.System_IntPtr ||
                    typeSymbol.SpecialType == SpecialType.System_UIntPtr ||
                    typeSymbol.SpecialType == SpecialType.System_Int16 ||
                    typeSymbol.SpecialType == SpecialType.System_Int32 ||
                    typeSymbol.SpecialType == SpecialType.System_UInt16 ||
                    typeSymbol.SpecialType == SpecialType.System_UInt32 ||
                    typeSymbol.SpecialType == SpecialType.System_Int64 ||
                    typeSymbol.SpecialType == SpecialType.System_UInt64 ||
                    typeSymbol.SpecialType == SpecialType.System_Byte ||
                    typeName.StartsWith("System.IntPtr*") ||
                    typeName.StartsWith("System.UIntPtr*") ||
                    typeName.StartsWith("ushort*") ||
                    typeName.StartsWith("sbyte*") ||
                    typeName.StartsWith("int*") ||
                    typeName.StartsWith("uint*") ||
                    typeName.StartsWith("long*") ||
                    typeName.StartsWith("ulong*") ||
                    typeName.StartsWith("void*");
            }
            */
            // See if we can map from some generic types to a more specific type
            if (true)
            {
                nativeType = CleanNativeType(nativeType);

                ITypeSymbol newTypeSymbol;
                if (!forRemap)
                {
                    bool isNotBasicCppType = IsNotBasicCppType(nativeType);
                    if (isNotBasicCppType)
                    {
                        if (!nativeType.Contains(' ') && !nativeType.Contains('*') && !nativeType.Contains('[') && !nativeType.Contains('&'))
                        {
                            this.CheckForOtherTypeCanBeMapped(nativeType, ns, typeName, typeSymbol);
                            return;
                        }
                    }
                }
                else
                {
                    newTypeSymbol = this.GetTypeFromShortName(nativeType);
                    if (newTypeSymbol != null)
                    {
                        typeSymbol = newTypeSymbol;
                        return;
                    }
                }

                string nameOnly;
                int star_count = 0;
                var kpos = nativeType.IndexOf('*');
                if (kpos == -1)
                {
                    kpos = nativeType.IndexOf('[');
                    if (kpos == -1)
                    {
                        kpos = nativeType.IndexOf('&');
                        if (kpos != -1)
                        {
                            star_count++;
                        }
                    }
                    else
                    {
                        star_count++;
                    }
                }
                if (kpos > -1)
                {
                    nameOnly = nativeType.Substring(0, kpos);
                    nameOnly = nameOnly.TrimEnd();
                    var star = nativeType.Substring(kpos);
                    star_count = star_count + star.Count(c => c == '*');
                }
                else
                {
                    nameOnly = nativeType;
                }

                // 下面的代码不兼容
                // public static extern D3D12_RESOURCE_ALLOCATION_INFO ID3D12Device12_GetResourceAllocationInfo3_Proxy(ID3D12Device12* This, uint visibleMask, uint numResourceDescs, [NativeTypeName("const D3D12_RESOURCE_DESC1 *")] D3D12_RESOURCE_DESC1* pResourceDescs, [NativeTypeName("const UINT32 *")] uint* pNumCastableFormats, [NativeTypeName("const DXGI_FORMAT *const *")] DXGI_FORMAT** ppCastableFormats, D3D12_RESOURCE_ALLOCATION_INFO1* pResourceAllocationInfo1);
                // 中的[NativeTypeName("const DXGI_FORMAT *const *")] DXGI_FORMAT** ppCastableFormats这个参数
                /*
                var parts = nativeType.Split(' ');
                var nameOnly = parts[0];
                var star = parts.Length > 1 ? parts[1] : null;
                */

                if (star_count != 0)
                {
                    // 下面的代码不兼容
                    // public static extern int CryptSignMessage([NativeTypeName("PCRYPT_SIGN_MESSAGE_PARA")] CRYPT_SIGN_MESSAGE_PARA* pSignPara, [NativeTypeName("BOOL")] int fDetachedSignature, [NativeTypeName("DWORD")] uint cToBeSigned, [NativeTypeName("const BYTE *[]")] [IncompleteArray] byte** rgpbToBeSigned, [NativeTypeName("DWORD[]")] uint* rgcbToBeSigned, byte* pbSignedBlob, [NativeTypeName("DWORD *")] uint* pcbSignedBlob);
                    // 中的[NativeTypeName("const BYTE *[]")] [IncompleteArray] byte** rgpbToBeSigned这个参数
                    /*
                    // If this is an array...
                    if (star[0] == '[')
                    {
                        // Treat it like a pointer if the array doesn't have a size
                        if (star == "[]")
                        {
                            star = "*";
                        }
                        // Otherwise don't treat it like an array. The encoder will encode
                        // the array size
                        else
                        {
                            star = null;
                        }
                    }
                    // Make sure it's actually a pointer. If it's not, we can't do anything more
                    else if (star[0] != '*')
                    {
                        return;
                    }
                    */
                }

                var typeNameOnly = typeName;
                if (star_count != 0)
                {
                    for (int i = 0; i < star_count; i++)
                    {
                        if (typeNameOnly.EndsWith('*'))
                        {
                            typeNameOnly = typeNameOnly.Remove(typeNameOnly.Length - 1);
                        }
                        else
                        {
                            if (i == star_count - 1)
                            {
                                if (forRemap)
                                {
                                    var pos = typeNameOnly.LastIndexOf('.');
                                    if (pos > -1)
                                    {
                                        ns = typeNameOnly.Substring(0, pos);
                                        typeNameOnly = typeNameOnly.Substring(pos + 1);
                                    }
                                }
                                if (typeNameOnly == "IntPtr" || typeNameOnly == "UIntPtr")
                                {

                                }
                                else if (nameOnly == typeNameOnly)
                                {
                                    break;
                                }
                                else
                                {
                                    //Debug.Assert(false);
                                    //Debug.WriteLine($"{owner}.{name} nativeType:{nativeType} does not match typeName:{typeName}!");
                                    return;
                                }
                            }
                            else
                            {
                                Debug.Assert(false);
                                Debug.WriteLine($"{owner}.{name} nativeType:{nativeType} does not match typeName:{typeName}!");
                                return;
                            }
                        }
                    }
                }
                if (!forRemap)
                {
                    bool isNotBasicCppTypeOnly = IsNotBasicCppType(nameOnly);
                    if (isNotBasicCppTypeOnly)
                    {
                        this.CheckForOtherTypeCanBeMapped(nameOnly, ns, typeNameOnly, typeSymbol);
                    }
                }
                else
                {
                    newTypeSymbol = this.GetTypeFromShortName(nameOnly);

                    if (newTypeSymbol != null)
                    {
                        if (star_count != 0)
                        {
                            var currentName = nameOnly;
                            for (int i = 0; i < star_count; i++)
                            {
                                currentName += "*";
                                var pointerSymbol = this.GetTypeFromShortName(currentName);
                                if (pointerSymbol == null)
                                {
                                    pointerSymbol = this.compilation.CreatePointerTypeSymbol(newTypeSymbol);
                                    this.nameToSymbols[currentName] = pointerSymbol;
                                }

                                newTypeSymbol = pointerSymbol;
                            }
                        }

                        typeSymbol = newTypeSymbol;
                    }
                }
            }
        }

        private MethodDefinitionHandle AddMethodViaParams(
            IMethodSymbol methodSymbol,
            string methodName,
            ITypeSymbol returnType,
            IEnumerable<Parameter> parameters,
            MethodAttributes methodAttrs,
            MethodImplAttributes methodImplAttributes,
            bool instanceMethod,
            bool thisCall)
        {
            methodName = FixArchSpecificName(methodName);

            if (methodSymbol != null && this.IsSymbolInterface(methodSymbol.ContainingType))
            {
                if (methodName.StartsWith("get_") &&
                    parameters.Count() == 1 &&
                    (parameters.First().Attrs & ParameterAttributes.Out) == ParameterAttributes.Out)
                {
                    methodAttrs |= MethodAttributes.SpecialName;
                }
                else if ((methodName.StartsWith("set_") || methodName.StartsWith("put_")) &&
                    parameters.Count() == 1 &&
                    (parameters.First().Attrs & ParameterAttributes.In) == ParameterAttributes.In)
                {
                    methodAttrs |= MethodAttributes.SpecialName;
                }
            }

            SignatureCallingConvention signatureCallingConvention = SignatureCallingConvention.Default;

            // If we see the special varargs name at the end, remove it and mark the method as using varargs
            if (parameters.Count() > 0 && parameters.Last().Name == MetadataSyntaxTreeCleaner.ArgListVarName)
            {
                signatureCallingConvention = SignatureCallingConvention.VarArgs;
                parameters = parameters.Take(parameters.Count() - 1);
            }

            if (thisCall)
            {
                signatureCallingConvention = SignatureCallingConvention.ThisCall;
            }

            var methodSignature = new BlobBuilder();
            new BlobEncoder(methodSignature)
                .MethodSignature(
                    signatureCallingConvention,
                    0,
                    instanceMethod)
                .Parameters(
                    parameters.Count(),
                    returnTypeEncoder => this.EncodeReturnType(returnType, returnTypeEncoder),
                    parametersEncoder => this.EncodeParameters(parameters, parametersEncoder)
                );
            var newMethod =
                this.metadataBuilder.AddMethodDefinition(
                    methodAttrs,
                    methodImplAttributes,
                    this.metadataBuilder.GetOrAddString(methodName),
                    this.metadataBuilder.GetOrAddBlob(methodSignature),
                    -1,
                    MetadataTokens.ParameterHandle(this.metadataBuilder.GetRowCount(TableIndex.Param) + 1));

            if (methodSymbol != null)
            {
                var returnAttrs = methodSymbol.GetReturnTypeAttributes();
                if (returnAttrs.Length != 0)
                {
                    var returnParamHandle = this.metadataBuilder.AddParameter(ParameterAttributes.None, default, 0);
                    this.AddCustomAttributes(returnAttrs, returnParamHandle);
                }
            }

            int index = 1;
            foreach (var p in parameters)
            {
                var paramHandle = this.metadataBuilder.AddParameter(p.Attrs, this.metadataBuilder.GetOrAddString(p.Name), index++);
                if (p.Symbol != null)
                {
                    this.AddCustomAttributes(p.Symbol.GetAttributes(), paramHandle);
                }
            }

            return newMethod;
        }

        private MethodDefinitionHandle AddMethodViaSymbol(
            IMethodSymbol methodSymbol,
            MethodAttributes methodAttrs,
            MethodImplAttributes methodImplAttrs,
            bool instanceMethod,
            bool thisCall)
        {
            var returnType = methodSymbol.ReturnType;
            this.RemapToMoreSpecificTypeIfPossible(methodSymbol.Name, "return", methodSymbol.GetReturnTypeAttributes(), ref returnType);

            List<Parameter> parameters = new List<Parameter>();
            foreach (var p in methodSymbol.Parameters)
            {
                parameters.Add(new Parameter(this, methodSymbol, p));
            }

            return this.AddMethodViaParams(methodSymbol, methodSymbol.Name, returnType, parameters, methodAttrs, methodImplAttrs, instanceMethod, thisCall);
        }

        private INamedTypeSymbol GetTypeByMetadataName(string typeName)
        {
            return this.compilation.GetTypeByMetadataName(typeName);
        }

        private ModuleReferenceHandle GetModuleRef(string name)
        {
            if (!this.moduleRefHandles.TryGetValue(name, out ModuleReferenceHandle handle))
            {
                handle = this.metadataBuilder.AddModuleReference(this.metadataBuilder.GetOrAddString(name));
                this.moduleRefHandles[name] = handle;
            }

            return handle;
        }

        private FieldDefinitionHandle WriteClassFields(ClassDeclarationSyntax node)
        {
            var className = node.Identifier.ValueText;
            var model = this.GetModel(node);
            var classSymbol = model.GetDeclaredSymbol(node);
            FieldDefinitionHandle firstField = default;
            foreach (FieldDeclarationSyntax field in node.Members.Where(m => m is FieldDeclarationSyntax))
            {
                var fieldVariable = field.Declaration.Variables.First();
                IFieldSymbol fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(fieldVariable);
                string name = FixFinalName(fieldSymbol.Name);

                FieldAttributes fieldAttributes;

                if (!fieldSymbol.IsConst)
                {
                    fieldAttributes = FieldAttributes.Public;

                    if (node.BaseList?.Types[0]?.ToString() != "Attribute")
                    {
                        fieldAttributes |= FieldAttributes.Static;

                        if (!HasGuidAttribute(field.AttributeLists) && !HasConstantAttribute(field.AttributeLists))
                        {
                            continue;
                        }
                    }

                    if (fieldSymbol.Name.StartsWith("IID_"))
                    {
                        continue;
                    }

                    if (name.StartsWith("CLSID_"))
                    {
                        string testClassName = name.Substring("CLSID_".Length);
                        if (this.structNameWithGuids.Contains(testClassName))
                        {
                            continue;
                        }
                    }
                }
                else
                {
                    fieldAttributes = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
                }

                var fieldSignature = this.EncodeFieldSignature(className, model, field, out _);
                var fieldDefinitionHandle =
                    this.metadataBuilder.AddFieldDefinition(
                        fieldAttributes,
                        this.metadataBuilder.GetOrAddString(name),
                        this.metadataBuilder.GetOrAddBlob(fieldSignature));
                if (fieldSymbol.HasConstantValue)
                {
                    this.metadataBuilder.AddConstant(fieldDefinitionHandle, fieldSymbol.ConstantValue);
                }

                if (firstField.IsNil)
                {
                    firstField = fieldDefinitionHandle;
                }

                this.AddCustomAttributes(fieldVariable, fieldDefinitionHandle);
            }

            // Add guid structs as guid constants
            var classNamespace = classSymbol.ContainingNamespace.ToString();
            if (className == "Apis" && this.namespaceToGuidOnlyStructSymbols.TryGetValue(classNamespace, out var symbols))
            {
                // Remove this as we may have partials defs that would add these multiple times
                this.namespaceToGuidOnlyStructSymbols.Remove(classNamespace);

                var guidTypeSymbol = this.compilation.GetTypeByMetadataName("System.Guid");
                var fieldAttributes = FieldAttributes.Public | FieldAttributes.Static;
                foreach (var symbol in symbols)
                {
                    var name = symbol.Name;
                    var fieldSignature = new BlobBuilder();
                    var encoder = new BlobEncoder(fieldSignature);
                    var signatureEncoder = encoder.FieldSignature();

                    this.EncodeTypeSymbol(guidTypeSymbol, signatureEncoder);

                    var fieldDefinitionHandle =
                        this.metadataBuilder.AddFieldDefinition(
                            fieldAttributes,
                            this.metadataBuilder.GetOrAddString(name),
                            this.metadataBuilder.GetOrAddBlob(fieldSignature));

                    if (firstField.IsNil)
                    {
                        firstField = fieldDefinitionHandle;
                    }

                    this.AddCustomAttributes(symbol, fieldDefinitionHandle);
                }
            }

            return firstField;
        }

        private MethodDefinitionHandle WriteClassMethods(ClassDeclarationSyntax node)
        {
            var className = node.Identifier.ValueText;
            var model = this.GetModel(node);
            var classSymbol = model.GetDeclaredSymbol(node);
            MethodDefinitionHandle firstMethod = default;
            string classFullName = this.GetFullNameForSymbol(classSymbol);

            foreach (ConstructorDeclarationSyntax constructor in node.Members.Where(m => m is ConstructorDeclarationSyntax))
            {
                var symbol = model.GetDeclaredSymbol(constructor);

                MethodImplAttributes methodImplAttributes = MethodImplAttributes.Managed;
                var methodDef =
                    this.AddMethodViaSymbol(
                        symbol,
                        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                        methodImplAttributes,
                        false,
                        false);

                if (firstMethod.IsNil)
                {
                    firstMethod = methodDef;
                }
            }

            foreach (MethodDeclarationSyntax method in node.Members.Where(m => m is MethodDeclarationSyntax))
            {
                var symbol = model.GetDeclaredSymbol(method);
                var methodName = method.Identifier.ValueText;
                string fullName = $"{classFullName}+{methodName}";

                if (this.namesToMethodDefHandles.ContainsKey(fullName))
                {
                    continue;
                }

                if (!symbol.IsExtern)
                {
                    continue;
                }

                methodName = FixArchSpecificName(methodName);

                MethodImplAttributes methodImplAttributes = MethodImplAttributes.Managed;
                if (symbol.GetAttributes().Any(a => a.AttributeClass.Name == "PreserveSigAttribute"))
                {
                    methodImplAttributes |= MethodImplAttributes.PreserveSig;
                }

                var methodDef =
                    this.AddMethodViaSymbol(
                        symbol,
                        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PinvokeImpl,
                        methodImplAttributes,
                        false,
                        false);
                if (firstMethod.IsNil)
                {
                    firstMethod = methodDef;
                }

                this.namesToMethodDefHandles[fullName] = methodDef;

                MethodImportAttributes importCallingConvention = MethodImportAttributes.CallingConventionWinApi;
                MethodImportAttributes methodImportAttributes = MethodImportAttributes.None;
                var dllImportAttr = symbol.GetAttributes().First(a => a.AttributeClass.Name == "DllImportAttribute");
                string moduleName = (string)dllImportAttr.ConstructorArguments[0].Value;
                StringHandle importedMethodName = this.metadataBuilder.GetOrAddString(methodName);
                foreach (var arg in dllImportAttr.NamedArguments)
                {
                    if (arg.Key == "ExactSpelling" && (bool)arg.Value.Value)
                    {
                        methodImportAttributes |= MethodImportAttributes.ExactSpelling;
                    }
                    else if (arg.Key == "SetLastError" && (bool)arg.Value.Value)
                    {
                        methodImportAttributes |= MethodImportAttributes.SetLastError;
                    }
                    else if (arg.Key == "EntryPoint")
                    {
                        importedMethodName = this.metadataBuilder.GetOrAddString((string)arg.Value.Value);
                    }
                    else if (arg.Key == "CallingConvention")
                    {
                        var callingConvention = (CallingConvention)arg.Value.Value;

                        switch (callingConvention)
                        {
                            case CallingConvention.Cdecl:
                                importCallingConvention = MethodImportAttributes.CallingConventionCDecl;
                                break;

                            case CallingConvention.StdCall:
                                importCallingConvention = MethodImportAttributes.CallingConventionWinApi;
                                break;

                            default:
                                break;
                        }
                    }
                }

                methodImportAttributes |= importCallingConvention;

                this.AddCustomAttributes(method, methodDef);

                this.metadataBuilder.AddMethodImport(
                    methodDef,
                    methodImportAttributes,
                    importedMethodName,
                    this.GetModuleRef(moduleName));
            }

            return firstMethod;
        }

        private void WriteClassDef(ClassDeclarationSyntax node)
        {
            var partialDefs = this.GetUnvistedPartialDefs(node);
            if (partialDefs.Count == 0)
            {
                return;
            }

            MethodDefinitionHandle methodDefinition = default;
            FieldDefinitionHandle fieldDefinition = default;
            foreach (var partialDef in partialDefs)
            {
                var firstMethod =
                    this.WriteClassMethods(partialDef);

                if (methodDefinition.IsNil)
                {
                    methodDefinition = firstMethod;
                }

                var firstField = this.WriteClassFields(partialDef);
                if (fieldDefinition.IsNil)
                {
                    fieldDefinition = firstField;
                }
            }

            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            var nsHandle = this.metadataBuilder.GetOrAddString(symbol.ContainingNamespace.ToString());
            var name = node.Identifier.ValueText;
            string fullName = symbol.ConstructedFrom.ToString();

            TypeAttributes typeAttributes = TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AutoClass | TypeAttributes.BeforeFieldInit;
            typeAttributes |= node.BaseList is null ? TypeAttributes.Abstract : TypeAttributes.Class;

            if (methodDefinition.IsNil)
            {
                methodDefinition = MetadataTokens.MethodDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1);
            }

            if (fieldDefinition.IsNil)
            {
                fieldDefinition = MetadataTokens.FieldDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.Field) + 1);
            }

            var destTypeDefHandle =
                this.metadataBuilder.AddTypeDefinition(
                    typeAttributes,
                    nsHandle,
                    this.metadataBuilder.GetOrAddString(name),
                    this.GetTypeReference("System", node.BaseList is null ? "Object" : node.BaseList.Types[0].ToString()),
                    fieldList: fieldDefinition,
                    methodList: methodDefinition);

            this.AddCustomAttributes(symbol.GetAttributes(), destTypeDefHandle);

            this.namesToTypeDefHandles[fullName] = destTypeDefHandle;
        }

        private MethodDefinitionHandle WriteDelegateMethods(DelegateDeclarationSyntax node)
        {
            MethodDefinitionHandle firstMethod = default;

            if (node.Parent is StructDeclarationSyntax)
            {
                return firstMethod;
            }

            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);

            var ctorMethodDef =
                this.AddMethodViaParams(
                    null,
                    ".ctor",
                    returnType: null,
                    new Parameter[]
                    {
                        new Parameter("object", this.GetTypeByMetadataName("System.Object"), ParameterAttributes.None),
                        new Parameter("method", this.GetTypeByMetadataName("System.IntPtr"), ParameterAttributes.None)
                    },
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    MethodImplAttributes.Managed | MethodImplAttributes.Runtime,
                    true,
                    false);
            firstMethod = ctorMethodDef;

            var invokeMethodSymbol = symbol.GetMembers().First(m => m.Name == "Invoke");

            this.AddMethodViaSymbol(
                (IMethodSymbol)invokeMethodSymbol,
                MethodAttributes.Public | MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.Virtual,
                MethodImplAttributes.Managed | MethodImplAttributes.Runtime,
                true,
                false);

            return firstMethod;
        }

        private void WriteDelegateDef(DelegateDeclarationSyntax node)
        {
            // We only want top-level delegates
            if (node.Parent is StructDeclarationSyntax)
            {
                return;
            }

            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);
            string fullName = symbol.ConstructedFrom.ToString();
            if (this.namesToTypeDefHandles.ContainsKey(fullName))
            {
                return;
            }

            var nsHandle = this.metadataBuilder.GetOrAddString(symbol.ContainingNamespace.ToString());
            var name = node.Identifier.ValueText;

            name = FixArchSpecificName(name);

            TypeAttributes delegateTypeAttributes = TypeAttributes.Public | TypeAttributes.AutoClass | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;

            MethodDefinitionHandle firstMethod = this.WriteDelegateMethods(node);
            if (firstMethod.IsNil)
            {
                firstMethod = MetadataTokens.MethodDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1);
            }

            var delegateDefHandle =
                this.metadataBuilder.AddTypeDefinition(
                    delegateTypeAttributes,
                    nsHandle,
                    this.metadataBuilder.GetOrAddString(name),
                    this.GetTypeReference("System", "MulticastDelegate"),
                    fieldList: MetadataTokens.FieldDefinitionHandle(this.metadataBuilder.GetRowCount(TableIndex.Field) + 1),
                    methodList: firstMethod);
            this.AddCustomAttributes(node, delegateDefHandle);

            this.namesToTypeDefHandles[fullName] = delegateDefHandle;
        }

        private string GetShortNameForSymbol(ITypeSymbol typeSymbol)
        {
#if MakeShortNameIncludeEnclosingName
            if (typeSymbol.ContainingType == null)
            {
                return typeSymbol.Name;
            }

            return $"{this.GetShortNameForSymbol(typeSymbol.ContainingType)}_{typeSymbol.Name}";
#else
            string ret = typeSymbol.Name;
            if (typeSymbol.ContainingSymbol is ITypeSymbol containingType && this.GetShortNameForSymbol(containingType) == ret)
            {
                ret += "_1";
            }

            return ret;
#endif
        }

        private string GetFullNameForSymbol(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.ContainingType == null)
            {
                return typeSymbol.ToString();
            }

            return this.GetFullNameForSymbol(typeSymbol.ContainingType) + "/" + this.GetShortNameForSymbol(typeSymbol);
        }

        private EntityHandle GetTypeRefOrDef(ITypeSymbol typeSymbol)
        {
            string fullName = this.GetFullNameForSymbol(typeSymbol);
            if (this.namesToTypeRefHandles.TryGetValue(fullName, out TypeReferenceHandle typeRef))
            {
                return typeRef;
            }

            if (this.namesToTypeDefHandles.TryGetValue(fullName, out TypeDefinitionHandle typeDef))
            {
                return typeDef;
            }

            string @namespace = typeSymbol.ContainingNamespace.ToString();
            if (@namespace == "<global namespace>")
            {
                if (this.ConvertTypeToImportedType(fullName, out typeRef))
                {
                    return typeRef;
                }

                throw new InvalidOperationException(
                    $"\"{fullName}\" could not be resolved. Make sure this symbol is defined in the metadata source files or in a referenced assembly.");
            }

            return this.GetTypeReference(typeSymbol);
        }

        /// <summary>
        /// Tries to map a type name to an imported type. The format comes from remap entries:
        /// IPropertyValue(interface)=&lt;Windows.Foundation.FoundationContract, Version=4.0.0.0, Culture=neutral, PublicKeyToken=null&gt;Windows.Foundation.IPropertyValue
        /// </summary>
        /// <param name="fullName">Symbol name</param>
        /// <param name="typeRef">Type ref to symbol</param>
        /// <returns></returns>
        private bool ConvertTypeToImportedType(string fullName, out TypeReferenceHandle typeRef)
        {
            typeRef = default;

            if (this.typeImports.TryGetValue(fullName, out var importInfoText))
            {
                var match = TypeImportRegex.Match(importInfoText);
                if (!match.Success)
                {
                    this.diagnostics.Add(new GeneratorDiagnostic($"Failed to convert imported type info for {fullName}: {importInfoText}", DiagnosticSeverity.Error));
                    return false;
                }

                var strongName = match.Groups[1].Value;
                if (!this.assemblyNamesToRefHandles.TryGetValue(strongName, out var assemblyRef))
                {
                    string assemblyName = match.Groups[2].Value;
                    Version assemblyVersion = Version.Parse(match.Groups[3].Value);
                    string publicKeyToken = match.Groups[5].Value;
                    BlobHandle publicKeyTokenBlobHandle = publicKeyToken == "null" ? default : this.metadataBuilder.GetOrAddBlob(ConvertKeyToByteArray(publicKeyToken));

                    assemblyRef =
                        this.metadataBuilder.AddAssemblyReference(
                            this.metadataBuilder.GetOrAddString(assemblyName),
                            assemblyVersion,
                            default,
                            publicKeyTokenBlobHandle,
                            default,
                            default);

                    this.assemblyNamesToRefHandles[strongName] = assemblyRef;
                }

                string newFullName = match.Groups[6].Value;
                int lastDot = newFullName.LastIndexOf('.');
                string newNamespace = newFullName.Substring(0, lastDot);
                string newName = newFullName.Substring(lastDot + 1);

                var nsHandle = this.metadataBuilder.GetOrAddString(newNamespace);
                typeRef = this.metadataBuilder.AddTypeReference(
                    assemblyRef,
                    nsHandle,
                    this.metadataBuilder.GetOrAddString(newName));
                this.namesToTypeRefHandles[fullName] = typeRef;

                return true;
            }

            return false;
        }

        private bool IsSymbolInterface(ISymbol symbol)
        {
            return this.interfaceSymbols.Contains(symbol) || this.typeImportInterfaces.Contains(symbol.Name);
        }

        private bool NeedsPointerReduction(ISymbol symbol)
        {
            if (this.IsSymbolInterface(symbol))
            {
                return true;
            }

            return this.reducePointerLevels.Contains(symbol.Name);
        }

        private void EncodeTypeSymbol(ITypeSymbol typeSymbol, SignatureTypeEncoder typeEncoder)
        {
            if (typeSymbol.SpecialType != SpecialType.None)
            {
                typeEncoder.EncodeSpecialType(typeSymbol.SpecialType);
            }
            else
            {
                if (typeSymbol.ToString() == "void*")
                {
                    typeEncoder.VoidPointer();
                    return;
                }

                // ClangSharp mistakenly emits WCHAR foo[] as string[] instead of string
                if (typeSymbol.ToString() == "string[]")
                {
                    typeEncoder.EncodeSpecialType(SpecialType.System_String);
                    return;
                }

                if (typeSymbol is IPointerTypeSymbol pointer)
                {
                    if (this.NeedsPointerReduction(pointer.PointedAtType))
                    {
                        this.EncodeTypeSymbol(pointer.PointedAtType, typeEncoder);
                    }
                    else
                    {
                        typeEncoder.Pointer();
                        this.EncodeTypeSymbol(pointer.PointedAtType, typeEncoder);
                    }

                    return;
                }

                bool isTypeInterface = this.IsSymbolInterface(typeSymbol);
                EntityHandle refHandle = this.GetTypeRefOrDef(typeSymbol);

                if (refHandle != default)
                {
                    typeEncoder.Type(
                        refHandle,
                        !isTypeInterface && (typeSymbol.TypeKind == TypeKind.Enum || typeSymbol.TypeKind == TypeKind.Struct));
                }
            }
        }

        private void WriteEnumMembers(EnumDeclarationSyntax node)
        {
            foreach (EnumMemberDeclarationSyntax member in node.Members)
            {
                var enumFieldAttributes =
                    FieldAttributes.Public |
                    FieldAttributes.Static |
                    FieldAttributes.Literal |
                    FieldAttributes.HasDefault;

                var model = this.GetModel(member);
                var symbol = model.GetDeclaredSymbol(member);
                var type = symbol.ContainingType;
                var fieldSignature = new BlobBuilder();
                var encoder = new BlobEncoder(fieldSignature);
                var signatureEncoder = encoder.FieldSignature();

                this.EncodeTypeSymbol(type, signatureEncoder);

                var memberName = FixFinalName(member.Identifier.Text);
                if (string.IsNullOrEmpty(memberName))
                {
                    throw new InvalidOperationException($"Enum {node.Identifier.Text} has a member with no name.");
                }

                if (Regex.IsMatch(memberName, @"_FORCE_(DWORD|UINT(\d\d)?|ULONG(\d\d)?)$"))
                {
                    return;
                }

                var fieldDefinitionHandle = this.metadataBuilder.AddFieldDefinition(
                    enumFieldAttributes,
                    this.metadataBuilder.GetOrAddString(memberName),
                    this.metadataBuilder.GetOrAddBlob(fieldSignature));

                this.AddCustomAttributes(symbol, fieldDefinitionHandle);

                if (symbol.HasConstantValue)
                {
                    this.metadataBuilder.AddConstant(fieldDefinitionHandle, symbol.ConstantValue);
                }
            }
        }

        private EntityHandle GetCtorRef(string typeName, string[] argTypes)
        {
            string ctorKey = typeName + "::" + string.Join(',', argTypes);
            if (!this.ctorNamesToRefs.TryGetValue(ctorKey, out var ctorRef))
            {
                var interfaceTypeAttr = this.GetTypeByMetadataName(typeName);
                List<ITypeSymbol> typeSymbols = new List<ITypeSymbol>();
                List<Parameter> parameters = new List<Parameter>();
                foreach (var a in argTypes)
                {
                    var argType = this.GetTypeByMetadataName(a);
                    typeSymbols.Add(argType);
                    Parameter p = new Parameter(string.Empty, argType, ParameterAttributes.None);
                    parameters.Add(p);
                }

                var ctorFound =
                    interfaceTypeAttr
                        .Constructors
                        .First(c => c.Parameters.Length == argTypes.Length && c.Parameters.Select(param => param.Type)
                        .SequenceEqual(typeSymbols, SymbolEqualityComparer.Default));

                var @namespace = interfaceTypeAttr.ContainingNamespace.ToString();
                var name = interfaceTypeAttr.Name;

                var typeRef = this.GetTypeReference(@namespace, name);

                ctorRef = this.CreateAndCacheCtorRefForTypeRef(typeRef, parameters, ctorKey);
            }

            return ctorRef;
        }

        private EntityHandle CreateAndCacheCtorRefForTypeRef(EntityHandle typeRef, List<Parameter> parameters, string ctorKey)
        {
            var methodSignature = new BlobBuilder();
            new BlobEncoder(methodSignature)
                .MethodSignature(
                    SignatureCallingConvention.Default,
                    0,
                    true)
                .Parameters(
                    parameters.Count(),
                    returnTypeEncoder => this.EncodeReturnType(null, returnTypeEncoder),
                    parametersEncoder => this.EncodeParameters(parameters, parametersEncoder)
                );
            var ctorRef =
                this.metadataBuilder.AddMemberReference(
                    typeRef,
                    this.metadataBuilder.GetOrAddString(".ctor"),
                    this.metadataBuilder.GetOrAddBlob(methodSignature));
            this.ctorNamesToRefs[ctorKey] = ctorRef;
            return ctorRef;
        }

        private EntityHandle GetAttributeCtorRef(AttributeData attributeData)
        {
            if (attributeData.AttributeConstructor == null)
            {
                throw new InvalidOperationException($"Failed to find attribute ctor for {attributeData}. Make sure the using statement for the attribute's assembly is in the source file.");
            }

            string ctorKey = attributeData.AttributeConstructor.ToDisplayString();
            if (!this.ctorNamesToRefs.TryGetValue(ctorKey, out var ctorRef))
            {
                var @namespace = attributeData.AttributeClass.ContainingNamespace.ToString();
                var name = attributeData.AttributeClass.Name;

                var typeRef = this.GetTypeReference(@namespace, name);

                List<Parameter> parameters = new List<Parameter>();
                foreach (var arg in attributeData.ConstructorArguments)
                {
                    Parameter p = new Parameter(string.Empty, arg.Type, ParameterAttributes.None);
                    parameters.Add(p);
                }

                ctorRef = this.CreateAndCacheCtorRefForTypeRef(typeRef, parameters, ctorKey);
            }

            return ctorRef;
        }

        private void AddCustomAttribute(string typeName, string[] ctorArgTypes, object[] ctorConstArgValues, EntityHandle entityHandle)
        {
            var ctorRef = this.GetCtorRef(typeName, ctorArgTypes);
            var attributeSignature = new BlobBuilder();
            new BlobEncoder(attributeSignature)
                .CustomAttributeSignature(
                    fixedArguments => fixedArguments.ConstArguments(ctorConstArgValues),
                    namedArguments => namedArguments.Count(0)
                );

            this.metadataBuilder.AddCustomAttribute(
                entityHandle,
                ctorRef,
                this.metadataBuilder.GetOrAddBlob(attributeSignature));
        }

        private void AddCustomAttribute(AttributeData attributeData, EntityHandle entityHandle)
        {
            var ctorRef = this.GetAttributeCtorRef(attributeData);

            var attributeSignature = new BlobBuilder();
            new BlobEncoder(attributeSignature)
                .CustomAttributeSignature(
                    fixedArguments => fixedArguments.FixedArguments(attributeData.ConstructorArguments),
                    namedArguments => namedArguments.NamedArguments(attributeData.AttributeClass, attributeData.NamedArguments)
                );

            this.metadataBuilder.AddCustomAttribute(
                entityHandle,
                ctorRef,
                this.metadataBuilder.GetOrAddBlob(attributeSignature));
        }

        private void AddCustomAttributes(IEnumerable<AttributeData> attrs, EntityHandle entityHandle)
        {
            foreach (var attributeData in attrs)
            {
                switch (attributeData.AttributeClass.Name)
                {
                    case "NativeTypeNameAttribute": // We may want to preserve this for some cases, like to disambiguate handle types

                    // These are all attributes that get converted into flags or settings for different nodes.
                    // If we keep these then when it gets decompiled it looks like there are two of everything
                    case "StructLayoutAttribute":
                    case "FieldOffsetAttribute":
                    case "InAttribute":
                    case "OutAttribute":
                    case "OptionalAttribute":
                    case "DllImportAttribute":
                    case "NativeInheritanceAttribute":
                    case "PreserveSigAttribute":
                        continue;
                }

                this.AddCustomAttribute(attributeData, entityHandle);
            }
        }

        private void AddCustomAttributes(ISymbol symbol, EntityHandle entityHandle)
        {
            this.AddCustomAttributes(symbol.GetAttributes(), entityHandle);
        }

        private void AddCustomAttributes(SyntaxNode node, EntityHandle entityHandle)
        {
            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);

            this.AddCustomAttributes(symbol, entityHandle);
        }

        private ISymbol GetInheritedInterfaceSymbol(StructDeclarationSyntax node)
        {
            if (this.structNodesToInheritedSymbols.TryGetValue(node, out var retSymbol))
            {
                return retSymbol;
            }

            foreach (var attrList in node.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    if (attr.Name.ToString() == "NativeInheritance")
                    {
                        var name = EncodeHelpers.RemoveQuotes(attr.ArgumentList.Arguments[0].ToString());
                        if (this.namesToInterfaceSymbols.TryGetValue(name, out var symbols))
                        {
                            // Could short circuit if there's only one, but this ensures the methods
                            // truly match up

                            // Get a map of all methods on this interface
                            var model = this.GetModel(node);
                            HashSet<string> methodNames = new HashSet<string>();
                            foreach (var method in node.Members.Where(m => m is MethodDeclarationSyntax))
                            {
                                var methodSymbol = model.GetDeclaredSymbol(method);
                                var methodName = methodSymbol.Name;

                                methodNames.Add(methodName);
                            }

                            // See if all the methods of the candidate symbol are also
                            // in the node's methods
                            foreach (INamedTypeSymbol symbol in symbols)
                            {
                                bool hasBaseSubobject = node.Members
                                    .OfType<FieldDeclarationSyntax>()
                                    .Any(field => SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(field.Declaration.Type).Type, symbol));

                                bool allFound = true;
                                foreach (var member in symbol.GetMembers().Where(m => m is IMethodSymbol && m.Name != ".ctor"))
                                {
                                    if (!methodNames.Contains(member.Name))
                                    {
                                        allFound = false;
                                        break;
                                    }
                                }

                                if (allFound || hasBaseSubobject)
                                {
                                    this.structNodesToInheritedSymbols[node] = symbol;
                                    return symbol;
                                }
                            }

                            throw new InvalidOperationException($"Failed to find suitable inhertited interface {name} for interace {node.Identifier.ValueText}");
                        }
                        else
                        {
                            throw new InvalidOperationException($"Failed to find inhertited interface {name} for interace {node.Identifier.ValueText}");
                        }
                    }
                }
            }

            return null;
        }

        private int GetInheritedMethodCount(StructDeclarationSyntax node)
        {
            var inheritedFrom = this.GetInheritedInterfaceSymbol(node);
            if (inheritedFrom == null)
            {
                return 0;
            }

            return this.interfaceMethodCount[inheritedFrom];
        }

        private MethodDefinitionHandle WriteInterfaceMethods(StructDeclarationSyntax node)
        {
            MethodDefinitionHandle firstMethod = default;

            var model = this.GetModel(node);
            var symbol = model.GetDeclaredSymbol(node);

            var inheritedMethodCount = this.GetInheritedMethodCount(node);

            // Find the distinct methods ordered by vtable slot, skipping
            // the methods that came from inherited interfaces
            foreach (MethodDeclarationSyntax method in node.Members
                .Where(m => m is MethodDeclarationSyntax)
                .Select(m => new MethodAndSlot((MethodDeclarationSyntax)m))
                .Distinct(new SlotComparer())
                .OrderBy(m => m.Slot)
                .Select(m => m.Method)
                .Skip(inheritedMethodCount))
            {
                var methodSymbol = model.GetDeclaredSymbol(method);
                var methodName = methodSymbol.Name;
                bool thisCall = false;
                MethodAttributes methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.Virtual;

                var delegateDef =
                    node.Members.FirstOrDefault(
                        m => m is DelegateDeclarationSyntax &&
                        ((DelegateDeclarationSyntax)m).Identifier.Text == $"_{methodName}");
                if (delegateDef != null)
                {
                    var attr = SyntaxUtils.GetAttribute(delegateDef.AttributeLists, "UnmanagedFunctionPointer");
                    if (attr != null && attr.ToString().Contains("ThisCall"))
                    {
                        thisCall = true;
                    }
                }

                MethodImplAttributes methodImplAttributes = MethodImplAttributes.Managed;

                if (methodSymbol.GetAttributes().Any(a => a.AttributeClass.Name == "PreserveSigAttribute"))
                {
                    methodImplAttributes |= MethodImplAttributes.PreserveSig;
                }

                var methodDef =
                    this.AddMethodViaSymbol(
                        methodSymbol,
                        methodAttributes,
                        methodImplAttributes,
                        true,
                        thisCall);

                if (firstMethod.IsNil)
                {
                    firstMethod = methodDef;
                }

                this.AddCustomAttributes(method, methodDef);
            }

            return firstMethod;
        }

        private BlobBuilder EncodeFieldSignature(string structName, SemanticModel model, FieldDeclarationSyntax field, out string name)
        {
            var fieldVariable = field.Declaration.Variables.First();
            IFieldSymbol fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(fieldVariable);
            var type = model.GetTypeInfo(field.Declaration.Type).Type;
            var fieldSignature = new BlobBuilder();
            var encoder = new BlobEncoder(fieldSignature);
            var signatureEncoder = encoder.FieldSignature();

            name = fieldSymbol.Name;

            this.RemapToMoreSpecificTypeIfPossible(structName, name, fieldSymbol.GetAttributes(), ref type);

            if (type.Name.EndsWith("_e__FixedBuffer"))
            {
                // Convert fixed buffer type into an array
                if (!this.fixedBufferTypeToInfo.TryGetValue(type, out FixedBufferInfo fixedBufferInfo))
                {
                    // If we haven't visited the node that defines the fixed buffer type, do it now
                    StructDeclarationSyntax structNode = (StructDeclarationSyntax)type.DeclaringSyntaxReferences[0].GetSyntax();
                    this.AddFixedBufferTypeInfoForStructNode(structNode);
                    fixedBufferInfo = this.fixedBufferTypeToInfo[type];
                }

                var fieldType = fixedBufferInfo.Type;
                this.RemapToMoreSpecificTypeIfPossible(structName, name, fieldSymbol.GetAttributes(), ref fieldType);

                bool symbolsChanged = !SymbolEqualityComparer.Default.Equals(fieldType, fixedBufferInfo.Type);
                if (symbolsChanged && fieldType is IPointerTypeSymbol pointer)
                {
                    fieldType = pointer.PointedAtType;
                }

                signatureEncoder.Array(
                    s => this.EncodeTypeSymbol(fieldType, signatureEncoder),
                    h => h.Shape(1, new int[1] { fixedBufferInfo.Count }.ToImmutableArray(), new int[1] { 0 }.ToImmutableArray()));
            }
            // Arrays
            else if (fieldVariable.ArgumentList is BracketedArgumentListSyntax bracketedArgList)
            {
                if (bracketedArgList.Arguments[0].Expression is LiteralExpressionSyntax literalExp)
                {
                    int size = int.Parse(literalExp.Token.Text);
                    signatureEncoder.Array(s => this.EncodeTypeSymbol(type, signatureEncoder), h => h.Shape(1, new int[1] { size }.ToImmutableArray(), new int[1] { 0 }.ToImmutableArray()));
                }
                else if (bracketedArgList.Arguments[0].Expression is BinaryExpressionSyntax binaryExp)
                {
                    var litLeft = (LiteralExpressionSyntax)binaryExp.Left;
                    var litRight = (LiteralExpressionSyntax)binaryExp.Right;
                    int size = int.Parse(litLeft.Token.Text) * int.Parse(litRight.Token.Text);
                    signatureEncoder.Array(s => this.EncodeTypeSymbol(type, signatureEncoder), h => h.Shape(1, new int[1] { size }.ToImmutableArray(), new int[1] { 0 }.ToImmutableArray()));
                }
            }
            else
            {
                var nativeType =
                    SyntaxUtils.GetNativeTypeNameFromAttributesLists(field.AttributeLists);

                // See if this is a type that ends with an array that needs to be emitted as a 1-length array
                // which means a variable-length array
                if (nativeType != null && nativeType.EndsWith("[]") && !(type is IPointerTypeSymbol))
                {
                    signatureEncoder.Array(s => this.EncodeTypeSymbol(type, signatureEncoder), h => h.Shape(1, new int[1] { 1 }.ToImmutableArray(), new int[1] { 0 }.ToImmutableArray()));
                }
                else
                {
                    this.EncodeTypeSymbol(type, signatureEncoder);
                }
            }

            return fieldSignature;
        }

        private void WriteStructEnums(StructDeclarationSyntax node, TypeDefinitionHandle structTypeDefHandle)
        {
            foreach (EnumDeclarationSyntax enumNode in node.Members.Where(m => m is EnumDeclarationSyntax))
            {
                this.WriteEnumDef(enumNode, structTypeDefHandle);
            }
        }

        private FieldDefinitionHandle WriteStructFields(StructDeclarationSyntax node)
        {
            FieldDefinitionHandle firstField = default;

            if (this.IsInterfaceDef(node))
            {
                throw new InvalidOperationException();
            }

            var structName = node.Identifier.ValueText;
            if (structName.EndsWith("_e__FixedBuffer"))
            {
                return firstField;
            }

            var model = this.GetModel(node);
            var structSymbol = model.GetDeclaredSymbol(node);

            // Write fields
            foreach (FieldDeclarationSyntax field in node.Members.Where(m => m is FieldDeclarationSyntax))
            {
                var fieldSignature = this.EncodeFieldSignature(structName, model, field, out string name);
                var fieldVariable = field.Declaration.Variables.First();
                var fieldAttributes = FieldAttributes.Public;
                IFieldSymbol fieldSymbol = (IFieldSymbol)model.GetDeclaredSymbol(fieldVariable);
                bool isConst = fieldSymbol.IsStatic && fieldSymbol.HasConstantValue;

                if (isConst)
                {
                    fieldAttributes |= FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
                }

                var fieldDefinitionHandle =
                    this.metadataBuilder.AddFieldDefinition(
                        fieldAttributes,
                        this.metadataBuilder.GetOrAddString(name),
                        this.metadataBuilder.GetOrAddBlob(fieldSignature));
                if (firstField.IsNil)
                {
                    firstField = fieldDefinitionHandle;
                }

                if (isConst)
                {
                    this.metadataBuilder.AddConstant(fieldDefinitionHandle, fieldSymbol.ConstantValue);
                }

                var fieldOffsetAttr = fieldSymbol.GetAttributes().FirstOrDefault(a => a.AttributeClass.Name == "FieldOffsetAttribute");
                if (fieldOffsetAttr != null)
                {
                    int offset = (int)fieldOffsetAttr.ConstructorArguments[0].Value;
                    this.metadataBuilder.AddFieldLayout(fieldDefinitionHandle, offset);
                }

                this.AddCustomAttributes(fieldVariable, fieldDefinitionHandle);
            }

            return firstField;
        }

        private List<T> GetUnvistedPartialDefs<T>(T node) where T : TypeDeclarationSyntax
        {
            List<T> nodes = new List<T>();

            if (this.visitedPartialDefs.Contains(node))
            {
                return nodes;
            }

            bool isPartial = node.Modifiers.Any(m => m.ValueText == "partial");
            nodes.Add(node);
            this.visitedPartialDefs.Add(node);

            if (isPartial)
            {
                var model = this.GetModel(node);
                var symbol = model.GetDeclaredSymbol(node);
                string fullName = symbol.ConstructedFrom.ToString();
                var symbolsWithSameName = this.compilation.GetSymbolsWithName(symbol.Name, SymbolFilter.Type);
                foreach (INamedTypeSymbol foundSymbol in symbolsWithSameName)
                {
                    if (foundSymbol.ConstructedFrom.ToString() != fullName)
                    {
                        continue;
                    }

                    foreach (var declaringSyntaxNode in foundSymbol.DeclaringSyntaxReferences)
                    {
                        var foundNode = declaringSyntaxNode.GetSyntax() as T;
                        if (foundNode == null || foundNode == node || this.visitedPartialDefs.Contains(foundNode))
                        {
                            continue;
                        }

                        nodes.Add(foundNode);
                        this.visitedPartialDefs.Add(foundNode);
                    }
                }
            }

            return nodes;
        }

        private class SyntaxWalker : CSharpSyntaxWalker
        {
            private ClangSharpSourceWinmdGenerator parent;

            public SyntaxWalker(ClangSharpSourceWinmdGenerator parent)
            {
                this.parent = parent;
            }

            public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
            {
                try
                {
                    this.parent.WriteEnumDef(node);
                }
                catch (Exception e)
                {
                    string message = $"Failed to emit {node.Identifier.ValueText} in {node.SyntaxTree.FilePath}: {e.Message}";
                    this.parent.diagnostics.Add(new GeneratorDiagnostic(message, DiagnosticSeverity.Error));
                }
            }

            public override void VisitStructDeclaration(StructDeclarationSyntax node)
            {
                try
                {
                    this.parent.WriteStructDef(node);
                }
                catch (Exception e)
                {
                    string message = $"Failed to emit {node.Identifier.ValueText} in {node.SyntaxTree.FilePath}: {e.Message}";
                    this.parent.diagnostics.Add(new GeneratorDiagnostic(message, DiagnosticSeverity.Error));
                }
            }

            public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
            {
                try
                {
                    this.parent.WriteDelegateDef(node);
                }
                catch (Exception e)
                {
                    string message = $"Failed to emit {node.Identifier.ValueText} in {node.SyntaxTree.FilePath}: {e.Message}";
                    this.parent.diagnostics.Add(new GeneratorDiagnostic(message, DiagnosticSeverity.Error));
                }
            }

            public override void VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                try
                {
                    this.parent.WriteClassDef(node);
                }
                catch (Exception e)
                {
                    string message = $"Failed to emit {node.Identifier.ValueText} in {node.SyntaxTree.FilePath}: {e.Message}";
                    this.parent.diagnostics.Add(new GeneratorDiagnostic(message, DiagnosticSeverity.Error));
                }
            }
        }

        private class InterfaceWalker : CSharpSyntaxWalker
        {
            private ClangSharpSourceWinmdGenerator parent;

            public InterfaceWalker(ClangSharpSourceWinmdGenerator parent)
            {
                this.parent = parent;
            }

            public override void VisitStructDeclaration(StructDeclarationSyntax node)
            {
                base.VisitStructDeclaration(node);

                if (this.parent.IsInterfaceDef(node))
                {
                    this.parent.CacheInterfaceType(node);
                }

                if (HasGuidAttribute(node.AttributeLists))
                {
                    this.parent.structNameWithGuids.Add(node.Identifier.ValueText);

                    if (node.Members.Count == 0)
                    {
                        this.parent.CacheGuidOnlyStruct(node);
                    }
                }
            }

            public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
            {
                if (node.Modifiers.ToString() == "public static readonly" && node.Declaration.Type.ToString() == "Guid")
                {
                    this.parent.CacheGuidConst(node);
                }
            }
        }

        private class FixedBufferInfo
        {
            public FixedBufferInfo(ITypeSymbol type, int count)
            {
                this.Type = type;
                this.Count = count;
            }

            public ITypeSymbol Type { get; }
            public int Count { get; }
        }

        private class Parameter
        {
            public Parameter(string name, ITypeSymbol type, ParameterAttributes attrs)
            {
                this.Name = name;
                this.Type = type;
                this.Attrs = attrs;
            }

            public Parameter(ClangSharpSourceWinmdGenerator generator, IMethodSymbol methodSymbol, IParameterSymbol parameterSymbol)
            {
                this.Symbol = parameterSymbol;

                var paramType = parameterSymbol.Type;
                var paramName = parameterSymbol.Name;

                string methodName;
                if (methodSymbol.ContainingType != null && methodSymbol.ContainingType.Name != "Apis")
                {
                    methodName = $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";
                }
                else
                {
                    methodName = methodSymbol.Name;
                }

                generator.RemapToMoreSpecificTypeIfPossible(methodName, paramName, parameterSymbol.GetAttributes(), ref paramType);
                ParameterAttributes parameterAttributes = ParameterAttributes.None;
                var symbolAttrs = parameterSymbol.GetAttributes();
                if (symbolAttrs.Any(a => a.AttributeClass.Name == "InAttribute"))
                {
                    parameterAttributes |= ParameterAttributes.In;
                }

                // Figure out if the final type is a pointer or not.
                IPointerTypeSymbol pointerTypeSymbol = paramType as IPointerTypeSymbol;
                bool needsPointerReduction = pointerTypeSymbol != null && generator.NeedsPointerReduction(pointerTypeSymbol.PointedAtType);

                if (needsPointerReduction)
                {
                    pointerTypeSymbol = null;
                }

                bool isPointer = pointerTypeSymbol != null;

                // If it's not a pointer, see if it's a native typedef that has a pointer type as its value (like for PWSTR)
                if (pointerTypeSymbol == null && !needsPointerReduction)
                {
                    if (paramType is INamedTypeSymbol namedType)
                    {
                        if (namedType.GetAttributes().Any(a => a.AttributeClass.Name == "NativeTypedefAttribute" || a.AttributeClass.Name == "MetadataTypedefAttribute"))
                        {
                            isPointer = (paramType.GetMembers("Value").First() as IFieldSymbol).Type is IPointerTypeSymbol;

                            // If we set pointerTypeSymbol to this, we gets lots of PWSTR/BSTRs that have no
                            // SAL attribution get set In/Out. Seems like most of them should remain just In
                        }
                    }
                }

                // Make sure we have a pointer before we add the Out attribute
                if (isPointer && symbolAttrs.Any(a => a.AttributeClass.Name == "OutAttribute" || a.AttributeClass.Name == "ComOutPtrAttribute"))
                {
                    parameterAttributes |= ParameterAttributes.Out;
                }

                if (symbolAttrs.Any(a => a.AttributeClass.Name == "OptionalAttribute"))
                {
                    parameterAttributes |= ParameterAttributes.Optional;
                }

                // If we don't have any in/out attributes, figure out some sensible values
                if (parameterAttributes == ParameterAttributes.None)
                {
                    // https://github.com/microsoft/win32metadata/issues/63
                    // * If it's a primitive or a COM pointer, it's In.
                    // * If it's a pointer it's In, Out, unless it's marked Const, then only In.
                    // * If it's a COM double pointer (e.g. IUnknown**), it's Out.
                    parameterAttributes |= ParameterAttributes.In;
                    if (pointerTypeSymbol != null)
                    {
                        bool isConst = symbolAttrs.Any(a => a.AttributeClass.Name == "ConstAttribute");

                        // Only add Out if it's not const
                        if (!isConst)
                        {
                            parameterAttributes |= ParameterAttributes.Out;
                        }

                        // If it's a double pointer...
                        if (pointerTypeSymbol.PointedAtType is IPointerTypeSymbol doublePointedAtType)
                        {
                            // Only use "Out" if it's a double COM pointer
                            if (generator.IsSymbolInterface(doublePointedAtType.PointedAtType))
                            {
                                parameterAttributes = ParameterAttributes.Out;
                            }
                        }
                    }
                }

                this.Name = paramName;
                this.Attrs = parameterAttributes;
                this.Type = paramType;
            }

            public string Name { get; }
            public ITypeSymbol Type { get; }
            public ParameterAttributes Attrs { get; }
            public IParameterSymbol Symbol { get; }
        }

        private class MethodAndSlot
        {
            public MethodAndSlot(MethodDeclarationSyntax method)
            {
                this.Method = method;
                this.Slot = SyntaxUtils.GetVtableSlotFromMethodBody(method);
            }

            public MethodDeclarationSyntax Method { get; }
            public int Slot { get; }
        }

        private class SlotComparer : IEqualityComparer<MethodAndSlot>
        {
            public bool Equals(MethodAndSlot x, MethodAndSlot y) => x.Slot == y.Slot;

            public int GetHashCode(MethodAndSlot obj) => obj.Slot;
        }
    }
}
