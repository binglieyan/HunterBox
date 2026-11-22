using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HunterBox.Tests;

/// <summary>
/// 分层源码守卫：用源码形状锁定运行时单测覆盖不到的边界
/// （拖拽事件数据生命周期、命名空间/API 使用、项目引用方向）。
/// </summary>
public class LayerSourceGuardTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        ["HunterBox.Domain"] = [],
        ["HunterBox.Application"] = ["HunterBox.Domain"],
        ["HunterBox.Infrastructure"] = ["HunterBox.Domain", "HunterBox.Application"],
        ["HunterBox.App"] = ["HunterBox.Domain", "HunterBox.Application", "HunterBox.Infrastructure"],
        ["HunterBox.Tests"] = ["HunterBox.Domain", "HunterBox.Application", "HunterBox.Infrastructure"],
    };

    /// <summary>四个通用分析器包：按条件求值后必须接入全部项目，不能被 Remove/Update/ExcludeAssets 关闭。</summary>
    private static readonly string[] AnalyzerPackages =
    [
        "Roslynator.Analyzers", "StyleCop.Analyzers", "SonarAnalyzer.CSharp", "Meziantou.Analyzer",
    ];

    /// <summary>拖拽数据只在事件处理期间有效，deferral 必须在进入互斥门前同步获取。</summary>
    /// <remarks>
    /// 需要源码树；发布/打包布局下会跳过（测试覆盖取舍）。
    /// 拖拽本身依赖 WinUI <c>DragEventArgs</c>，运行期无法在单元测试里构造，UI 行为由手动验收覆盖。
    /// </remarks>
    [SourceTreeFact]
    public void DragDropEntry_TakesDeferralBeforeEnteringUiGate()
    {
        string source = File.ReadAllText(Path.Combine(
            SourceTreeLocator.RootOrThrow(), "src", "HunterBox.App", "MainWindow.DragDropCommands.cs"));

        int deferral = source.IndexOf("e.GetDeferral()", StringComparison.Ordinal);
        int enter = source.IndexOf("RunUiAction(\"无法通过拖拽安装\"", StringComparison.Ordinal);

        Assert.True(deferral >= 0, "拖拽入口必须先取 deferral，延长 DataView 的生命周期");
        Assert.True(enter > deferral, "deferral 必须在进入 RunUiAction 之前同步获取");
        Assert.Contains("deferral.Complete", source, StringComparison.Ordinal);
    }

    /// <summary>Application 只依赖端口，JSON 序列化实现必须留在 Infrastructure。</summary>
    [Fact]
    public void ApplicationAssembly_DoesNotReferenceSystemTextJson()
    {
        // 直接检查编译后的程序集引用，不受注释里出现 "System.Text.Json" 干扰。
        Assert.DoesNotContain(
            typeof(HunterBox.Application.Saves.SaveBackupService).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "System.Text.Json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Domain 只允许 BCL 与自身命名空间：禁止文件 IO、JSON 与跨层引用。</summary>
    [SourceTreeFact]
    public void DomainSource_DoesNotUseFileIoJsonOrOuterLayers()
    {
        List<string> offenders = ScanProject("HunterBox.Domain", (root, relative, semantic) =>
        {
            List<string> issues = [];
            foreach (UsingDirectiveSyntax usage in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                string? name = usage.Name?.ToString();
                if (name is null)
                {
                    continue;
                }

                if (IsNamespace(name, "System.Text.Json")
                    || IsNamespace(name, "HunterBox.Application")
                    || IsNamespace(name, "HunterBox.Infrastructure")
                    || IsNamespace(name, "HunterBox.App"))
                {
                    issues.Add($"{relative}:{Line(usage)}: 禁止 using {name}");
                }
            }

            issues.AddRange(ScanForbiddenApis(
                root,
                relative,
                ["File", "Directory"],
                ["FileStream", "FileInfo", "DirectoryInfo", "StreamReader", "StreamWriter"],
                [],
                semantic,
                forbidJsonApi: true));
            return issues;
        });

        AssertNoViolations("Domain 层禁止文件 IO/JSON/跨层引用", offenders);
    }

    /// <summary>Application 只通过端口访问实现：禁止 Infrastructure/App、SharpCompress、JSON 与 File/Directory 静态调用。</summary>
    [SourceTreeFact]
    public void ApplicationSource_DoesNotUseInfrastructureOrImplementationDetails()
    {
        List<string> offenders = ScanProject("HunterBox.Application", (root, relative, semantic) =>
        {
            List<string> issues = [];
            foreach (UsingDirectiveSyntax usage in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                string? name = usage.Name?.ToString();
                if (name is null)
                {
                    continue;
                }

                if (IsNamespace(name, "HunterBox.Infrastructure")
                    || IsNamespace(name, "HunterBox.App")
                    || IsNamespace(name, "SharpCompress")
                    || IsNamespace(name, "System.Text.Json"))
                {
                    issues.Add($"{relative}:{Line(usage)}: 禁止 using {name}");
                }
            }

            issues.AddRange(ScanForbiddenApis(
                root,
                relative,
                ["File", "Directory"],
                ["FileStream", "FileInfo", "DirectoryInfo"],
                ["IniSettings", "SystemFileSystem", "ModRepository"],
                semantic,
                forbidJsonApi: true));
            return issues;
        });

        AssertNoViolations("Application 层禁止直接依赖 Infrastructure/JSON/文件 IO", offenders);
    }

    /// <summary>Infrastructure 只做适配，不得反向引用 App。</summary>
    [SourceTreeFact]
    public void InfrastructureSource_DoesNotUseApp()
    {
        List<string> offenders = ScanProject("HunterBox.Infrastructure", (root, relative, _) =>
            root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(usage => usage.Name is not null && IsNamespace(usage.Name.ToString(), "HunterBox.App"))
                .Select(usage => $"{relative}:{Line(usage)}: 禁止 using {usage.Name}")
                .ToList());

        AssertNoViolations("Infrastructure 层禁止引用 App", offenders);
    }

    /// <summary>App 不直接使用解压/JSON 实现或静态文件 IO，必须走 Application/Infrastructure 端口。</summary>
    [SourceTreeFact]
    public void AppSource_DoesNotUseSharpCompressOrJsonDirectly()
    {
        List<string> offenders = ScanProject("HunterBox.App", (root, relative, semantic) =>
        {
            List<string> issues = [];
            foreach (UsingDirectiveSyntax usage in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                string? name = usage.Name?.ToString();
                if (name is not null
                    && (IsNamespace(name, "SharpCompress")
                        || IsNamespace(name, "System.Text.Json")))
                {
                    issues.Add($"{relative}:{Line(usage)}: 禁止 using {name}");
                }
            }

            if (root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier => identifier.Identifier.ValueText == "JsonSerializer"))
            {
                issues.Add($"{relative}: 禁止直接使用 JsonSerializer");
            }

            // App 不自行做业务性文件 IO，存在性/读写一律走 IFileSystem 端口。
            issues.AddRange(ScanForbiddenApis(
                root,
                relative,
                ["File", "Directory"],
                ["FileStream", "FileInfo", "DirectoryInfo"],
                [],
                semantic,
                forbidJsonApi: true));
            return issues;
        });

        AssertNoViolations("App 层禁止直接依赖 SharpCompress/JSON 与静态文件 IO", offenders);
    }

    /// <summary>JSON 门禁覆盖类型调用与 source-generator 特性式用法。</summary>
    [Fact]
    public void ScanForbiddenApis_DetectsJsonTypes()
    {
        const string Source = """
            using System.Text.Json;
            using System.Text.Json.Nodes;
            using System.Text.Json.Serialization;
            class C
            {
                void M()
                {
                    using JsonDocument document = JsonDocument.Parse("{}");
                    JsonNode? node = JsonNode.Parse("{}");
                    _ = node;
                    Utf8JsonWriter writer = null!;
                    Utf8JsonReader reader = default;
                    ReferenceHandler? handler = null;
                    ReferenceResolver? resolver = null;
                    IJsonTypeInfoResolver? typeInfo = null;
                    _ = typeof(IJsonTypeInfoResolver);
                    _ = writer;
                    _ = reader;
                    _ = handler;
                    _ = resolver;
                    _ = typeInfo;
                }
            }

            [JsonSerializable(typeof(C))]
            internal partial class CContext : JsonSerializerContext
            {
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(
            tree.GetRoot(),
            "inline.cs",
            [],
            [],
            [],
            forbidJsonApi: true);

        Assert.Contains(issues, issue => issue.Contains("JsonDocument", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("JsonNode", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("JsonSerializable", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("JsonSerializerContext", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("Utf8JsonWriter", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("Utf8JsonReader", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("ReferenceHandler", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("ReferenceResolver", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("IJsonTypeInfoResolver", StringComparison.Ordinal));
    }

    /// <summary>JSON 类型的 using static 与类型别名同样被拦截。</summary>
    [Fact]
    public void ScanForbiddenApis_DetectsJsonAliasAndUsingStatic()
    {
        const string Source = """
            using JsonDoc = System.Text.Json.JsonDocument;
            using static System.Text.Json.JsonSerializer;
            class C
            {
                void M()
                {
                    using JsonDoc document = JsonDoc.Parse("{}");
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(
            tree.GetRoot(),
            "inline.cs",
            [],
            [],
            [],
            forbidJsonApi: true);

        Assert.Contains(issues, issue => issue.Contains("using static", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("JsonDoc", StringComparison.Ordinal));
    }

    /// <summary>using static 引入的 JSON 静态成员调用（成员名不在 Json* 范围）同样被拦截。</summary>
    [Fact]
    public void ScanForbiddenApis_DetectsJsonStaticMemberInvocation()
    {
        const string Source = """
            using static System.Text.Json.JsonSerializer;
            class C
            {
                void M() => Serialize(1);
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(
            tree.GetRoot(),
            "inline.cs",
            [],
            [],
            [],
            forbidJsonApi: true);

        Assert.Contains(issues, issue => issue.Contains("JSON 成员 Serialize", StringComparison.Ordinal));
    }

    /// <summary>自有同名类型不按 JSON 特例名误报。</summary>
    [Fact]
    public void ScanForbiddenApis_LocalSameNameType_IsNotJson()
    {
        const string Source = """
            using LocalHandler = ReferenceHandler;
            class ReferenceHandler { }
            class C
            {
                ReferenceHandler? Handler;
                LocalHandler? Alias;
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(
            tree.GetRoot(),
            "inline.cs",
            [],
            [],
            [],
            forbidJsonApi: true);

        Assert.DoesNotContain(issues, issue => issue.Contains("ReferenceHandler", StringComparison.Ordinal));
    }

    /// <summary>别名与 using static 也必须被语法门禁识别。</summary>
    [Fact]
    public void ScanForbiddenApis_DetectsAliasAndUsingStatic()
    {
        const string Source = """
            using F = System.IO.File;
            using static System.IO.Directory;
            class C
            {
                void M() => F.ReadAllText("x");
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(tree.GetRoot(), "inline.cs", ["File", "Directory"], [], []);

        Assert.Contains(issues, issue => issue.Contains("F.ReadAllText", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("using static System.IO.Directory", StringComparison.Ordinal));
    }

    /// <summary>目标类型 new(...)、类型声明与 typeof 必须被语法门禁识别。</summary>
    [Fact]
    public void ScanForbiddenApis_DetectsTargetTypedCreationAndDeclaration()
    {
        const string Source = """
            using System.IO;
            class C
            {
                void M()
                {
                    FileStream stream = new("x", FileMode.Open);
                    FileInfo info = Create();
                    _ = typeof(Directory);
                }

                FileInfo Create() => new("y");

                static FileStream Open() => new("z", FileMode.Open);

                FileInfo Info { get; } = new("w");
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(
            tree.GetRoot(),
            "inline.cs",
            ["File", "Directory"],
            ["FileStream", "FileInfo", "DirectoryInfo"],
            []);

        Assert.Contains(issues, issue => issue.Contains("禁止创建 FileStream", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("禁止声明 FileInfo", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("typeof(Directory)", StringComparison.Ordinal));
        // return new(...) 与属性初始化也必须由语义模型解析出 FileStream（至少 2 处：局部变量 + 返回）。
        Assert.True(
            issues.Count(issue => issue.Contains("禁止创建 FileStream", StringComparison.Ordinal)) >= 2,
            "return new(...) 未被门禁识别：" + string.Join("；", issues));
        // 属性初始化 FileInfo Info { get; } = new("w") 只能靠语义模型解析。
        Assert.True(
            issues.Any(issue => issue.Contains("禁止创建 FileInfo", StringComparison.Ordinal)),
            "属性初始化的 new(...) 未被门禁识别：" + string.Join("；", issues));
    }

    /// <summary>门禁本身必须被执行：源码树缺失时不能把整组检查静默跳过。</summary>
    [Fact]
    public void SourceTree_IsAvailableForGuards() =>
        Assert.NotNull(SourceTreeLocator.TryFind());

    /// <summary>项目引用方向必须向内：Domain ← Application ← Infrastructure ← App。</summary>
    [SourceTreeFact]
    public void ProjectReferences_FollowLayerDirection()
    {
        string root = SourceTreeLocator.RootOrThrow();
        List<string> offenders = [];
        foreach (string csproj in SourceTreeLocator.EnumerateRepositoryFiles()
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Path.GetRelativePath(root, csproj);
            if (IsIgnoredPath(relative))
            {
                continue;
            }

            string projectName = Path.GetFileNameWithoutExtension(csproj);
            if (!AllowedReferences.TryGetValue(projectName, out string[]? allowed))
            {
                offenders.Add($"{relative}: 未登记的项目 {projectName}，请显式加入允许表");
                continue;
            }

            XDocument document = XDocument.Load(csproj);
            if (projectName is "HunterBox.Domain" or "HunterBox.Application")
            {
                // 分层核心只允许 PrivateAssets=all 的分析器包；运行时第三方依赖必须在 Infrastructure/App。
                foreach (XElement package in document.Descendants("PackageReference"))
                {
                    string? privateAssets = (string?)package.Attribute("PrivateAssets");
                    if (!string.Equals(privateAssets, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add(
                            $"{relative}: {projectName} 不允许运行时第三方包 {package.Attribute("Include")?.Value}");
                    }
                }
            }

            foreach (XElement reference in document.Descendants("ProjectReference"))
            {
                string? include = (string?)reference.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                string referenced = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
                if (!allowed.Contains(referenced, StringComparer.Ordinal))
                {
                    offenders.Add($"{relative}: 非法引用 {referenced}（允许：{string.Join("、", allowed.DefaultIfEmpty("无"))}）");
                }
            }
        }

        AssertNoViolations("项目引用方向违规", offenders);
    }

    private static List<string> ScanProject(
        string projectName,
        Func<SyntaxNode, string, SemanticModel?, List<string>> inspect)
    {
        string root = SourceTreeLocator.RootOrThrow();
        ProjectSource source = SourceTreeCompilationCache.Get(projectName);
        List<string> offenders = [];
        foreach (SyntaxTree tree in source.Trees)
        {
            string relative = Path.GetRelativePath(root, tree.FilePath);
            offenders.AddRange(inspect(tree.GetRoot(), relative, source.Compilation.GetSemanticModel(tree)));
        }

        return offenders;
    }

    private static List<string> ScanForbiddenApis(
        SyntaxNode root,
        string relative,
        string[] forbiddenStaticTypes,
        string[] forbiddenCreatedTypes,
        string[] forbiddenIdentifiers,
        SemanticModel? semantic = null,
        bool forbidJsonApi = false)
    {
        // 三类禁用清单都为空时无需遍历语法树。
        if (forbiddenStaticTypes.Length == 0
            && forbiddenCreatedTypes.Length == 0
            && forbiddenIdentifiers.Length == 0
            && !forbidJsonApi)
        {
            return [];
        }

        List<string> issues = [];
        CSharpCompilation? localCompilation = null;
        SemanticModel ResolveModel(SyntaxNode node)
        {
            if (semantic is not null)
            {
                return semantic;
            }

            // 独立门禁用例（只解析一段内联源码）：同一棵树只建一次编译。
            localCompilation ??= CSharpCompilation.Create(
                "Guard-inline",
                [node.SyntaxTree],
                SourceTreeCompilationCache.References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return localCompilation.GetSemanticModel(node.SyntaxTree);
        }

        // 整棵树只遍历一次，按节点类型分派，避免为每种禁用类型重复 DescendantNodes()。
        List<UsingDirectiveSyntax> usings = [];
        List<MemberAccessExpressionSyntax> accesses = [];
        List<BaseObjectCreationExpressionSyntax> creations = [];
        List<VariableDeclaratorSyntax> declarators = [];
        List<TypeOfExpressionSyntax> typeOfs = [];
        List<IdentifierNameSyntax> identifiers = [];
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case UsingDirectiveSyntax usage:
                    usings.Add(usage);
                    break;
                case MemberAccessExpressionSyntax access:
                    accesses.Add(access);
                    break;
                case BaseObjectCreationExpressionSyntax creation:
                    creations.Add(creation);
                    break;
                case VariableDeclaratorSyntax declarator:
                    declarators.Add(declarator);
                    break;
                case TypeOfExpressionSyntax typeOf:
                    typeOfs.Add(typeOf);
                    break;
                case IdentifierNameSyntax identifier:
                    identifiers.Add(identifier);
                    break;
            }
        }

        // using 别名 / using static：语法级扫描看不到语义类型，先把别名映射回禁止类型。
        HashSet<string> aliases = new(StringComparer.Ordinal);
        HashSet<string> jsonAliases = new(StringComparer.Ordinal);
        bool hasJsonStaticUsing = false;
        foreach (UsingDirectiveSyntax usage in usings)
        {
            if (usage.Name is null)
            {
                continue;
            }

            string target = usage.Name.ToString();
            string last = target[(target.LastIndexOf('.') + 1)..];
            if (forbidJsonApi && IsJsonCandidateName(last))
            {
                // 别名/using static 必须解析到 System.Text.Json 目标，
                // 不能仅凭名字把本地自有 Json* 类型误报为 JSON API。
                bool jsonTarget = IsJsonUsingTarget(usage, semantic);
                if (usage.Alias is not null && jsonTarget)
                {
                    jsonAliases.Add(usage.Alias.Name.Identifier.ValueText);
                }
                else if (jsonTarget && usage.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                {
                    hasJsonStaticUsing = true;
                    issues.Add($"{relative}:{Line(usage)}: 禁止 using static {target}");
                }

                continue;
            }

            if (!forbiddenStaticTypes.Contains(last, StringComparer.Ordinal))
            {
                continue;
            }

            if (usage.Alias is not null)
            {
                aliases.Add(usage.Alias.Name.Identifier.ValueText);
            }
            else if (usage.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            {
                issues.Add($"{relative}:{Line(usage)}: 禁止 using static {target}");
            }
        }

        foreach (MemberAccessExpressionSyntax access in accesses)
        {
            // 取表达式最后一段：File、System.IO.File、IO.File 与 using 别名都会命中。
            string expression = access.Expression.ToString();
            string lastSegment = expression[(expression.LastIndexOf('.') + 1)..];
            if (forbiddenStaticTypes.Contains(lastSegment, StringComparer.Ordinal)
                || aliases.Contains(lastSegment)
                || jsonAliases.Contains(lastSegment))
            {
                issues.Add($"{relative}:{Line(access)}: 禁止调用 {expression}.{access.Name}");
            }
        }

        foreach (BaseObjectCreationExpressionSyntax creation in creations)
        {
            // 优先用语义模型解析真实类型（支持别名、return、属性初始化），
            // 无法解析时退回语法形状；整个项目只建一次编译。
            string typeName = creation switch
            {
                ObjectCreationExpressionSyntax => SemanticTypeName(ResolveModel(creation), creation)
                    ?? TypeName(((ObjectCreationExpressionSyntax)creation).Type),
                ImplicitObjectCreationExpressionSyntax => SemanticTypeName(ResolveModel(creation), creation)
                    ?? SyntacticImplicitCreationTypeName((ImplicitObjectCreationExpressionSyntax)creation),
                _ => string.Empty,
            };
            if (forbiddenCreatedTypes.Contains(typeName, StringComparer.Ordinal))
            {
                issues.Add($"{relative}:{Line(creation)}: 禁止创建 {typeName}");
            }
        }

        // 类型声明同样是门禁覆盖范围：FileStream s = factory(); 不出现 new 也不能放行。
        foreach (VariableDeclaratorSyntax declarator in declarators)
        {
            if (declarator.Parent is not VariableDeclarationSyntax declaration
                || !forbiddenCreatedTypes.Contains(TypeName(declaration.Type), StringComparer.Ordinal))
            {
                continue;
            }

            // 显式 new 已在上面报过，避免同一行重复。
            if (declarator.Initializer?.Value is ObjectCreationExpressionSyntax)
            {
                continue;
            }

            issues.Add($"{relative}:{Line(declarator)}: 禁止声明 {TypeName(declaration.Type)}");
        }

        // typeof(File) / typeof(Directory) 绕过成员调用的写法也要拦。
        foreach (TypeOfExpressionSyntax typeOf in typeOfs)
        {
            string typeName = TypeName(typeOf.Type);
            if (forbiddenStaticTypes.Contains(typeName, StringComparer.Ordinal))
            {
                issues.Add($"{relative}:{Line(typeOf)}: 禁止使用 typeof({typeName})");
            }
        }

        foreach (string forbidden in forbiddenIdentifiers)
        {
            if (identifiers.Any(identifier => identifier.Identifier.ValueText == forbidden))
            {
                issues.Add($"{relative}: 禁止使用 {forbidden}");
            }
        }

        // JSON 门禁按 System.Text.Json 命名空间家族识别；
        // 候选名 = Json/Utf8Json 前缀 + 序列化特殊类型（不枚举全部 JSON 类型）。
        if (forbidJsonApi)
        {
            HashSet<string> reported = new(StringComparer.Ordinal);
            foreach (IdentifierNameSyntax identifier in identifiers)
            {
                string name = identifier.Identifier.ValueText;
                if (!IsJsonCandidateName(name))
                {
                    // 只有文件真的引入 JSON using static 时才做成员级语义查询，
                    // 避免对每个普通调用都解析符号（仓内生产代码没有这种用法）。
                    if (hasJsonStaticUsing
                        && identifier.Parent is InvocationExpressionSyntax invocation
                        && ReferenceEquals(invocation.Expression, identifier)
                        && ResolveJsonApiSymbol(ResolveModel(identifier), identifier) == true
                        && reported.Add(name))
                    {
                        issues.Add($"{relative}:{Line(identifier)}: 禁止调用 JSON 成员 {name}");
                    }

                    continue;
                }

                bool knownName = ExtraJsonTypeNames.Contains(name, StringComparer.Ordinal);
                bool? isJson = ResolveJsonApiSymbol(ResolveModel(identifier), identifier);
                // 特例名仍优先走语义校验；仅解析失败时按已知 JSON 类型 fail-closed，
                // 自有同名类型（解析为本地类型）不会被误报。
                if ((isJson == true || (isJson is null && knownName)) && reported.Add(name))
                {
                    issues.Add($"{relative}: 禁止使用 JSON API {name}");
                }
            }
        }

        return issues;
    }

    /// <summary>JSON 门禁的候选标识符：<c>Json*</c>/<c>Utf8Json*</c> 前缀与序列化特殊类型。</summary>
    private static readonly string[] ExtraJsonTypeNames =
    [
        "ReferenceHandler", "ReferenceResolver", "IJsonTypeInfoResolver",
    ];

    /// <summary>标识符是否可能是 <c>System.Text.Json</c> 类型（先按名字过滤，再做语义解析）。</summary>
    /// <param name="name">标识符文本。</param>
    /// <returns>可能属于 JSON API 时返回 <see langword="true"/>。</returns>
    private static bool IsJsonCandidateName(string name) =>
        name.StartsWith("Json", StringComparison.Ordinal)
        || name.StartsWith("Utf8Json", StringComparison.Ordinal)
        || ExtraJsonTypeNames.Contains(name, StringComparer.Ordinal);

    /// <summary>判断 using 目标是否真的是 <c>System.Text.Json</c> 类型（文本前缀 + 语义解析）。</summary>
    /// <param name="usage">using 指令。</param>
    /// <param name="semantic">语义模型；为 <see langword="null"/> 时只能按文本前缀判断。</param>
    /// <returns>目标是 JSON 类型时返回 <see langword="true"/>。</returns>
    private static bool IsJsonUsingTarget(UsingDirectiveSyntax usage, SemanticModel? semantic)
    {
        string target = usage.Name?.ToString() ?? string.Empty;
        if (target.StartsWith("System.Text.Json", StringComparison.Ordinal))
        {
            return string.Equals(target, "System.Text.Json", StringComparison.Ordinal)
                || target.StartsWith("System.Text.Json.", StringComparison.Ordinal);
        }

        if (semantic is null || usage.Name is null)
        {
            return false;
        }

        try
        {
            ISymbol? symbol = semantic.GetSymbolInfo(usage.Name).Symbol;
            ITypeSymbol? type = symbol switch
            {
                ITypeSymbol direct => direct,
                IAliasSymbol alias => alias.Target as ITypeSymbol,
                _ => null,
            };
            if (type is null)
            {
                return false;
            }

            string? ns = type.ContainingNamespace?.ToDisplayString();
            return ns is not null
                && (string.Equals(ns, "System.Text.Json", StringComparison.Ordinal)
                    || ns.StartsWith("System.Text.Json.", StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>标识符是否指向 <c>System.Text.Json</c> 命名空间下的类型/特性。</summary>
    /// <param name="semantic">语义模型。</param>
    /// <param name="identifier">标识符。</param>
    /// <returns>是 JSON API 时为 <see langword="true"/>，明确是其它类型时为 <see langword="false"/>，无法解析时为 <see langword="null"/>。</returns>
    private static bool? ResolveJsonApiSymbol(SemanticModel semantic, IdentifierNameSyntax identifier)
    {
        try
        {
            ISymbol? symbol = semantic.GetSymbolInfo(identifier).Symbol;
            ITypeSymbol? type = symbol switch
            {
                IMethodSymbol method => method.ContainingType,
                ITypeSymbol direct => direct,
                _ => semantic.GetTypeInfo(identifier).Type,
            };
            if (type is null or IErrorTypeSymbol)
            {
                return null;
            }

            string? ns = type.ContainingNamespace?.ToDisplayString();
            return ns is not null
                && (string.Equals(ns, "System.Text.Json", StringComparison.Ordinal)
                    || ns.StartsWith("System.Text.Json.", StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>取类型语法的最后一段名称（支持 Identifier / Qualified / AliasQualified）。</summary>
    /// <param name="type">类型语法；为 <see langword="null"/> 时返回空串。</param>
    /// <returns>类型短名；无法识别时为空串。</returns>
    private static string TypeName(TypeSyntax? type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    /// <summary>App 与 Infrastructure 的 P/Invoke 必须带 <c>DefaultDllImportSearchPaths(System32)</c>。</summary>
    [SourceTreeFact]
    public void PInvokes_HaveSystem32SearchPath()
    {
        string root = SourceTreeLocator.RootOrThrow();
        List<string> offenders = [];
        string[] projects = ["HunterBox.Domain", "HunterBox.Application", "HunterBox.Infrastructure", "HunterBox.App"];
        foreach (string project in projects)
        {
            // 复用项目缓存的语法树，不再为 P/Invoke 门禁重复解析整棵源码树。
            foreach (SyntaxTree tree in SourceTreeCompilationCache.Get(project).Trees)
            {
                foreach (MethodDeclarationSyntax method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    List<AttributeSyntax> attributes = [.. method.AttributeLists.SelectMany(list => list.Attributes)];
                    if (!IsPInvokeImport(attributes))
                    {
                        continue;
                    }

                    AttributeSyntax? searchPath = attributes.FirstOrDefault(IsSystem32SearchPath);
                    if (searchPath is null)
                    {
                        offenders.Add($"{Path.GetRelativePath(root, tree.FilePath)}:{Line(method)}: {method.Identifier.ValueText} 缺少 DefaultDllImportSearchPaths");
                        continue;
                    }

                    if (!HasSystem32Argument(searchPath))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, tree.FilePath)}:{Line(method)}: {method.Identifier.ValueText} 未指定 System32");
                    }
                }
            }
        }

        AssertNoViolations("P/Invoke 缺少 DefaultDllImportSearchPaths(System32)", offenders);
    }

    /// <summary>用语义模型取对象创建表达式的真实类型短名（支持类型别名链）。</summary>
    /// <param name="semantic">语义模型。</param>
    /// <param name="creation">对象创建表达式。</param>
    /// <returns>类型短名；解析失败时为空串。</returns>
    private static string? SemanticTypeName(SemanticModel semantic, SyntaxNode creation)
    {
        try
        {
            ITypeSymbol? type = semantic.GetTypeInfo(creation).Type;
            return type is null or IErrorTypeSymbol ? null : type.Name;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>隐式 new(...) 的语法兜底：只识别局部变量声明的目标类型。</summary>
    /// <param name="creation">隐式对象创建表达式。</param>
    /// <returns>类型短名；无法识别时为空串。</returns>
    private static string SyntacticImplicitCreationTypeName(ImplicitObjectCreationExpressionSyntax creation)
    {
        if (creation.Parent is EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax
                {
                    Parent: VariableDeclarationSyntax declaration,
                },
            })
        {
            return TypeName(declaration.Type);
        }

        return string.Empty;
    }

    /// <summary>枚举仓库内参与构建的文件（props/targets/csproj/globalconfig），排除构建产物与本地目录。</summary>
    /// <remarks>复用仓库级一次性枚举（已剪枝 <c>.git</c>/<c>obj</c>/<c>bin</c> 等），不再重复全仓遍历。</remarks>
    /// <param name="root">仓库根目录。</param>
    /// <returns>构建文件路径。</returns>
    private static IEnumerable<string> EnumerateBuildFiles(string root)
    {
        string[] patterns =
        [
            "Directory.Build.props", "Directory.Build.targets", "*.csproj", "*.targets", "*.props", "*.ruleset",
            ".globalconfig",
        ];
        foreach (string file in SourceTreeLocator.EnumerateRepositoryFiles())
        {
            string relative = Path.GetRelativePath(root, file);
            if (IsIgnoredPath(relative))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (patterns.Any(pattern => name.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                || (pattern.StartsWith("*.", StringComparison.Ordinal)
                    && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))))
            {
                yield return file;
            }
        }
    }

    /// <summary>门禁忽略的目录：构建产物与本地评审/发布目录。</summary>
    private static bool IsIgnoredPath(string relative) =>
        relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => segment is "obj" or "bin" or "dist" or ".local");

    /// <summary>App 不得直接写设置（路径键写入统一走 SettingsCoordinator/GameDiscoveryService）。</summary>
    [SourceTreeFact]
    public void App_DoesNotWriteSettingsDirectly()
    {
        string root = SourceTreeLocator.RootOrThrow();
        ProjectSource source = SourceTreeCompilationCache.Get("HunterBox.App");
        List<string> offenders = [];
        foreach (SyntaxTree tree in source.Trees)
        {
            string relative = Path.GetRelativePath(root, tree.FilePath);
            offenders.AddRange(FindSettingsWrites(tree.GetRoot(), source.Compilation.GetSemanticModel(tree))
                .Select(issue => $"{relative}:{issue}"));
        }

        AssertNoViolations("App 直接写入设置", offenders);
    }

    /// <summary>接口变量/别名接收者同样会被设置写入门禁识别。</summary>
    [Fact]
    public void SettingsWriteGate_DetectsInterfaceVariableAlias()
    {
        const string Source = """
            using HunterBox.Application.Settings;
            class C
            {
                void M(ISettings s) => s.Set("k", "v");
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Guard-SettingsAlias",
            [tree],
            SourceTreeCompilationCache.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        List<string> issues = FindSettingsWrites(tree.GetRoot(), compilation.GetSemanticModel(tree));

        Assert.Contains(issues, issue => issue.Contains("s.Set", StringComparison.Ordinal));
    }

    /// <summary>找出对 ISettings 的写调用（按接收者类型而不是变量名判定）。</summary>
    /// <param name="root">语法根。</param>
    /// <param name="semantic">语义模型。</param>
    /// <returns>违规描述（行号: 调用）。</returns>
    private static List<string> FindSettingsWrites(SyntaxNode root, SemanticModel semantic)
    {
        string[] writeMethods = ["Set", "SetBool", "UpdateBatch", "Remove"];
        List<string> issues = [];
        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access
                || !writeMethods.Contains(access.Name.Identifier.ValueText, StringComparer.Ordinal))
            {
                continue;
            }

            ITypeSymbol? receiver = semantic.GetTypeInfo(access.Expression).Type;
            if (IsSettingsSymbol(receiver))
            {
                issues.Add($"{Line(invocation)}: {access}");
            }
        }

        return issues;
    }

    /// <summary>接收者类型是否为 <c>HunterBox.Application.Settings.ISettings</c>（含实现类型与错误符号兜底）。</summary>
    /// <param name="type">接收者类型符号。</param>
    /// <returns>是设置端口时返回 <see langword="true"/>。</returns>
    private static bool IsSettingsSymbol(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (string.Equals(type.Name, "ISettings", StringComparison.Ordinal)
            && string.Equals(
                type.ContainingNamespace?.ToDisplayString(),
                "HunterBox.Application.Settings",
                StringComparison.Ordinal))
        {
            return true;
        }

        return type.AllInterfaces.Any(@interface =>
            string.Equals(@interface.Name, "ISettings", StringComparison.Ordinal)
            && string.Equals(
                @interface.ContainingNamespace?.ToDisplayString(),
                "HunterBox.Application.Settings",
                StringComparison.Ordinal));
    }

    /// <summary>三份 BannedSymbols 按行精确覆盖关键通道，分析器接线不能被移除。</summary>
    [SourceTreeFact]
    public void BannedSymbols_CoverKeyChannelsAndAnalyzerWiring()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string[] common =
        [
            "T:Microsoft.Win32.Registry",
            "T:Microsoft.Win32.RegistryKey",
            "T:System.IO.Directory",
            "T:System.IO.DirectoryInfo",
            "T:System.IO.File",
            "T:System.IO.FileInfo",
            "T:System.IO.FileStream",
            "T:System.IO.FileSystemInfo",
            "T:System.IO.FileSystemWatcher",
            "T:System.IO.StreamReader",
            "T:System.IO.StreamWriter",
            "T:System.Net.Http.HttpClient",
            "T:System.Text.Json.JsonDocument",
            "T:System.Text.Json.JsonElement",
            "T:System.Text.Json.JsonSerializer",
            "T:System.Text.Json.JsonSerializerOptions",
            "T:System.Text.Json.Nodes.JsonNode",
            "T:System.Text.Json.Utf8JsonWriter",
            "T:System.Text.Json.Utf8JsonReader",
            "T:System.Text.Json.Serialization.ReferenceHandler",
            "T:System.Text.Json.Serialization.ReferenceResolver",
            "T:System.Text.Json.Serialization.IJsonTypeInfoResolver",
        ];
        Dictionary<string, string[]> required = new(StringComparer.Ordinal)
        {
            ["HunterBox.App"] = [.. common, "T:Windows.Storage.FileIO", "T:Windows.Storage.PathIO"],
            ["HunterBox.Application"] = [.. common, "T:System.Diagnostics.Process", "T:System.Text.Json.Utf8JsonWriter"],
            ["HunterBox.Domain"] = [.. common, "T:System.Diagnostics.Process", "M:System.Xml.Linq.XDocument.Load(System.String)"],
        };

        List<string> offenders = [];
        Dictionary<string, HashSet<string>> symbolSets = new(StringComparer.Ordinal);
        foreach ((string project, string[] symbols) in required)
        {
            string path = Path.Combine(root, "src", project, "BannedSymbols.txt");
            Assert.True(File.Exists(path), $"{project} 缺少 BannedSymbols.txt");
            string[] lines =
            [
                .. File.ReadAllLines(path)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
                    .Select(line => line.Split(';')[0].Trim())
                    .Where(symbol => symbol.Length > 0),
            ];
            // 按行精确比较符号名，不用子串包含（File 不再被 FileStream 满足）。
            HashSet<string> set = new(lines, StringComparer.Ordinal);
            symbolSets[project] = set;
            if (set.Count != lines.Length)
            {
                offenders.Add($"{project}: BannedSymbols.txt 存在重复符号行");
            }

            offenders.AddRange(symbols
                .Where(symbol => !set.Contains(symbol))
                .Select(symbol => $"{project}: 缺少 {symbol}"));
        }

        // 公共集合由三份清单的交集精确断言，删除任一共有符号都会失败。
        HashSet<string> domain = symbolSets["HunterBox.Domain"];
        HashSet<string> application = symbolSets["HunterBox.Application"];
        HashSet<string> app = symbolSets["HunterBox.App"];
        HashSet<string> intersection = [.. domain.Where(application.Contains).Where(app.Contains)];
        HashSet<string> expectedCommon = new(common, StringComparer.Ordinal);
        if (!intersection.SetEquals(expectedCommon))
        {
            offenders.Add(
                "三份 BannedSymbols 的交集与预期公共集合不一致：缺少 "
                + string.Join("、", expectedCommon.Except(intersection))
                + "；多出 "
                + string.Join("、", intersection.Except(expectedCommon)));
        }

        foreach (string symbol in common)
        {
            if (!domain.Contains(symbol) || !application.Contains(symbol) || !app.Contains(symbol))
            {
                offenders.Add($"三份清单的公共集合不一致：{symbol}");
            }
        }

        string propsPath = Path.Combine(root, "Directory.Build.props");
        string props = File.ReadAllText(propsPath);
        if (!props.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", StringComparison.Ordinal))
        {
            offenders.Add("Directory.Build.props: 未接入 Microsoft.CodeAnalysis.BannedApiAnalyzers");
        }

        if (!props.Contains("<AdditionalFiles Include=\"BannedSymbols.txt\" />", StringComparison.Ordinal))
        {
            offenders.Add("Directory.Build.props: 未把 BannedSymbols.txt 注册为 AdditionalFiles");
        }

        // 按 MSBuild 条件求值断言每个目标项目实际拿到分析器与 BannedSymbols；
        // 把 Or 改成 And 或增加 PackageReference Remove 都会让门禁失败，不再只做子串包含。
        XDocument propsDocument = XDocument.Load(propsPath);
        foreach (string project in required.Keys)
        {
            bool wired = propsDocument.Descendants("ItemGroup").Any(group =>
            {
                // 无 Condition 的 ItemGroup 对所有项目生效，同样算接线；
                // 条件按统一求值器在全部代表性配置下判断。
                if (!ConditionAllows(group, project))
                {
                    return false;
                }

                bool analyzer = group.Elements("PackageReference").Any(element =>
                    string.Equals((string?)element.Attribute("Include"), "Microsoft.CodeAnalysis.BannedApiAnalyzers", StringComparison.Ordinal)
                    && element.Attribute("Remove") is null
                    && element.Attribute("Update") is null
                    && ConditionAllows(element, project)
                    && !ExcludesAnalyzers(element));
                bool banned = group.Elements("AdditionalFiles").Any(element =>
                    string.Equals((string?)element.Attribute("Include"), "BannedSymbols.txt", StringComparison.Ordinal)
                    && element.Attribute("Remove") is null
                    && element.Attribute("Update") is null
                    && ConditionAllows(element, project));
                return analyzer && banned;
            });

            if (!wired)
            {
                offenders.Add($"Directory.Build.props: 分析器条件最终未覆盖 {project}");
            }
        }

        // A-02：分析器开关与严重级别不能被降级为关闭/默认模式。
        AssertBuildPropertyNotDowngraded(props, "Directory.Build.props", offenders);
        if (!Regex.IsMatch(props, @"<EnableNETAnalyzers>\s*true\s*</EnableNETAnalyzers>", RegexOptions.IgnoreCase))
        {
            offenders.Add("Directory.Build.props: 必须显式开启 EnableNETAnalyzers");
        }

        if (!Regex.IsMatch(props, @"<AnalysisMode>\s*AllEnabledByDefault\s*</AnalysisMode>", RegexOptions.IgnoreCase))
        {
            offenders.Add("Directory.Build.props: AnalysisMode 必须为 AllEnabledByDefault");
        }

        // 按 EditorConfig 段匹配求具体文件的最终生效值（后置匹配段覆盖前值）。
        string editorConfig = File.ReadAllText(Path.Combine(root, ".editorconfig"));
        string[] probePaths =
        [
            "src/HunterBox.App/Probe.cs",
            "src/HunterBox.Application/Probe.cs",
            "src/HunterBox.Domain/Probe.cs",
            "tests/HunterBox.Tests/Probe.cs",
        ];
        foreach (string probePath in probePaths)
        {
            Dictionary<string, string> effective = EditorConfigEvaluator.Evaluate(editorConfig, probePath);
            foreach (string rule in new[] { "RS0030", "RS0031" })
            {
                string key = $"dotnet_diagnostic.{rule}.severity";
                if (!effective.TryGetValue(key, out string? severity)
                    || !string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($".editorconfig: {probePath} 的 {rule} 最终生效值必须为 warning（当前：{severity ?? "未设置"}）");
                }
            }

            if (effective.TryGetValue("NoWarn", out string? noWarn)
                && (noWarn.Contains("RS0030", StringComparison.OrdinalIgnoreCase)
                    || noWarn.Contains("RS0031", StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add($".editorconfig: {probePath} 的 NoWarn 不能包含 RS0030/RS0031");
            }
        }

        foreach (string line in props.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("NoWarn", StringComparison.Ordinal)
                && (line.Contains("RS0030", StringComparison.Ordinal) || line.Contains("RS0031", StringComparison.Ordinal)))
            {
                offenders.Add("Directory.Build.props: NoWarn 不能包含 RS0030/RS0031");
            }
        }

        // 仓库内任意 .editorconfig（含子目录/文件级）都不能把关键规则降级或加入 NoWarn。
        // 复用仓库级一次性枚举，不再单独全仓遍历。
        // .globalconfig 的优先级高于 .editorconfig，同样按无段落的全局配置校验。
        List<string> analyzerConfigs = [.. SourceTreeLocator.EnumerateRepositoryFiles()
            .Where(file => Path.GetFileName(file) is ".editorconfig" or ".globalconfig")];
        foreach (string configPath in analyzerConfigs)
        {
            string relative = Path.GetRelativePath(root, configPath);
            if (IsIgnoredPath(relative))
            {
                continue;
            }

            foreach (string raw in File.ReadAllLines(configPath))
            {
                string line = raw.Trim();
                int equals = line.IndexOf('=');
                if (equals <= 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                string key = line[..equals].Trim();
                string value = line[(equals + 1)..].Trim();
                if (IsCriticalSeverityKey(key))
                {
                    // 键名忽略大小写；warning/warn/error 都是有效（非降级）值。
                    if (IsDowngradedSeverity(value))
                    {
                        offenders.Add($"{relative}: {key} 不能设为 {value}");
                    }
                }

                if (string.Equals(key, "dotnet_analyzer_diagnostic.severity", StringComparison.OrdinalIgnoreCase)
                    && IsDisabledSeverity(value))
                {
                    offenders.Add($"{relative}: dotnet_analyzer_diagnostic.severity 不能设为 {value}（会关闭 BannedApi）");
                }

                if (string.Equals(key, "NoWarn", StringComparison.OrdinalIgnoreCase)
                    && (value.Contains("RS0030", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("RS0031", StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{relative}: NoWarn 不能包含 RS0030/RS0031");
                }
            }
        }

        // csproj/targets 的 NoWarn、Remove、TreatWarningsAsErrors 降级与源码级抑制也要拦截。
        foreach (string buildFile in EnumerateBuildFiles(root))
        {
            string relative = Path.GetRelativePath(root, buildFile);
            string text = File.ReadAllText(buildFile);
            foreach (Match match in Regex.Matches(text, @"<NoWarn[^>]*>(?<value>[^<]*)</NoWarn>", RegexOptions.IgnoreCase))
            {
                string value = match.Groups["value"].Value;
                if (value.Contains("RS0030", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("RS0031", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{relative}: NoWarn 不能包含 RS0030/RS0031");
                }
            }

            if (Regex.IsMatch(text, @"<AdditionalFiles[^>]*Remove[^>]*BannedSymbols\.txt", RegexOptions.IgnoreCase))
            {
                offenders.Add($"{relative}: 不能 Remove BannedSymbols.txt");
            }

            if (Regex.IsMatch(text, @"<PackageReference[^>]*Remove[^>]*BannedApiAnalyzers", RegexOptions.IgnoreCase))
            {
                offenders.Add($"{relative}: 不能 Remove Microsoft.CodeAnalysis.BannedApiAnalyzers");
            }

            if (Regex.IsMatch(text, @"<PackageReference[^>]*Update[^>]*BannedApiAnalyzers", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"<AdditionalFiles[^>]*Update[^>]*BannedSymbols\.txt", RegexOptions.IgnoreCase))
            {
                offenders.Add($"{relative}: 不能 Update BannedApiAnalyzers/BannedSymbols.txt");
            }

            if (Regex.IsMatch(text, @"<TreatWarningsAsErrors>\s*false\s*</TreatWarningsAsErrors>", RegexOptions.IgnoreCase))
            {
                offenders.Add($"{relative}: TreatWarningsAsErrors 不能被降级为 false");
            }

            // WarningsNotAsErrors / 关闭分析器 / 降级 AnalysisMode 同样会让 BannedApi 失效。
            AssertBuildPropertyNotDowngraded(text, relative, offenders);
        }

        foreach (string project in new[] { "HunterBox.Domain", "HunterBox.Application", "HunterBox.App" })
        {
            foreach (SyntaxTree tree in SourceTreeCompilationCache.Get(project).Trees)
            {
                string relative = Path.GetRelativePath(root, tree.FilePath);
                foreach (string line in File.ReadAllText(tree.FilePath).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.TrimStart().StartsWith("#pragma warning disable", StringComparison.Ordinal)
                        && (line.Contains("RS0030", StringComparison.Ordinal) || line.Contains("RS0031", StringComparison.Ordinal)))
                    {
                        offenders.Add($"{relative}: 不能 #pragma 抑制 RS0030/RS0031");
                    }
                }

                foreach (AttributeSyntax attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
                {
                    string text = attribute.ToString();
                    if (text.Contains("SuppressMessage", StringComparison.Ordinal)
                        && (text.Contains("RS0030", StringComparison.Ordinal) || text.Contains("RS0031", StringComparison.Ordinal)))
                    {
                        offenders.Add($"{relative}: 不能 SuppressMessage 抑制 RS0030/RS0031");
                    }
                }
            }
        }

        if (props.Contains("<AdditionalFiles Remove=\"BannedSymbols.txt\"", StringComparison.Ordinal))
        {
            offenders.Add("Directory.Build.props: 不能 Remove BannedSymbols.txt");
        }

        Assert.True(
            offenders.Count == 0,
            "BannedSymbols 门禁不完整：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 四个通用分析器包按条件求值后必须接入全部项目；
    /// Remove/Update/ExcludeAssets/IncludeAssets 任一包级降级都必须让门禁失败。
    /// </summary>
    [SourceTreeFact]
    public void AnalyzerPackages_AreWiredForAllProjects()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string propsPath = Path.Combine(root, "Directory.Build.props");
        XDocument props = XDocument.Load(propsPath);
        List<string> offenders = [];

        foreach (string project in GuardedProjects)
        {
            foreach (string package in AnalyzerPackages)
            {
                bool wired = props.Descendants("ItemGroup").Any(group =>
                    ConditionAllows(group, project)
                    && group.Elements("PackageReference").Any(element =>
                        string.Equals((string?)element.Attribute("Include"), package, StringComparison.Ordinal)
                        && element.Attribute("Remove") is null
                        && element.Attribute("Update") is null
                        && ConditionAllows(element, project)
                        && !ExcludesAnalyzers(element)));
                if (!wired)
                {
                    offenders.Add($"Directory.Build.props: {package} 未接入 {project}");
                }
            }
        }

        foreach (string buildFile in EnumerateBuildFiles(root))
        {
            // .globalconfig 是 INI 形式，不参与 XML 解析；其余构建文件都是 XML。
            string extension = Path.GetExtension(buildFile);
            if (extension is not (".csproj" or ".props" or ".targets" or ".ruleset"))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, buildFile);
            XElement build = XElement.Parse(File.ReadAllText(buildFile));
            foreach (XElement element in build.Descendants("PackageReference"))
            {
                foreach (string attribute in new[] { "Remove", "Update" })
                {
                    string? value = (string?)element.Attribute(attribute);
                    if (value is not null && AnalyzerPackages.Contains(value, StringComparer.Ordinal))
                    {
                        offenders.Add($"{relative}: 不能 {attribute} 分析器包 {value}");
                    }
                }

                string? include = (string?)element.Attribute("Include");
                if (include is not null
                    && AnalyzerPackages.Contains(include, StringComparer.Ordinal)
                    && ExcludesAnalyzers(element))
                {
                    offenders.Add($"{relative}: {include} 不能排除 analyzers 资产");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "通用分析器包门禁失败：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>类型别名（using F = FileStream）创建的禁止类型同样会被识别。</summary>
    [Fact]
    public void ScanForbiddenApis_DetectsTypeAliasCreation()
    {
        const string Source = """
            using F = System.IO.FileStream;
            class C
            {
                F Open() => new("x", System.IO.FileMode.Open);
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.Latest));

        List<string> issues = ScanForbiddenApis(tree.GetRoot(), "inline.cs", [], ["FileStream"], []);

        Assert.Contains(issues, issue => issue.Contains("禁止创建 FileStream", StringComparison.Ordinal));
    }

    /// <summary>测试地图必须精确覆盖所有测试类，且不能残留已删除的类名。</summary>
    [SourceTreeFact]
    public void TestMap_CoversAllTestClasses()
    {
        string root = SourceTreeLocator.RootOrThrow();
        HashSet<string> classes = CollectTestClassNames();
        string map = File.ReadAllText(Path.Combine(root, "docs", "wiki", "10-test-map.md"));
        // 只解析表格行/清单行，正文段落里同名类不能掩盖「删除表格行」。
        HashSet<string> listed =
        [
            .. map.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart())
                .Where(line => line.StartsWith('|') || line.StartsWith('-'))
                .SelectMany(line => Regex.Matches(line, "`([A-Za-z0-9_]+Tests)`")
                    .Select(match => match.Groups[1].Value)),
        ];

        List<string> missing = [.. classes
            .Where(name => !listed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)];
        List<string> stale = [.. listed
            .Where(name => !classes.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)];

        Assert.True(
            missing.Count == 0,
            "测试地图缺少以下测试类：" + Environment.NewLine + string.Join(Environment.NewLine, missing));
        Assert.True(
            stale.Count == 0,
            "测试地图残留不存在的测试类：" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>收集包含测试特性的测试类名（复用共享测试语法树）。</summary>
    /// <returns>测试类名集合。</returns>
    private static HashSet<string> CollectTestClassNames()
    {
        HashSet<string> classes = new(StringComparer.Ordinal);
        foreach (SyntaxTree tree in TestSourceIndex.Trees)
        {
            foreach (ClassDeclarationSyntax classNode in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                bool hasTest = classNode.Members
                    .OfType<MethodDeclarationSyntax>()
                    .SelectMany(method => method.AttributeLists)
                    .SelectMany(list => list.Attributes)
                    .Any(attribute => TestSourceIndex.TestAttributeNames.Contains(
                        TestSourceIndex.AttributeLastName(attribute),
                        StringComparer.Ordinal));
                if (hasTest)
                {
                    classes.Add(classNode.Identifier.ValueText);
                }
            }
        }

        return classes;
    }

    /// <summary>后置的具体路径段覆盖必须在最终生效值中生效。</summary>
    [Fact]
    public void EditorConfig_Evaluator_UsesLastMatchingSection()
    {
        const string Config = """
            [*.cs]
            dotnet_diagnostic.RS0030.severity = warning

            [src/HunterBox.App/**/*.cs]
            dotnet_diagnostic.RS0030.severity = none
            """;

        Assert.Equal(
            "none",
            EditorConfigEvaluator.Evaluate(Config, "src/HunterBox.App/ModelPanel.cs")["dotnet_diagnostic.RS0030.severity"]);
        Assert.Equal(
            "warning",
            EditorConfigEvaluator.Evaluate(Config, "src/HunterBox.Application/ModelPanel.cs")["dotnet_diagnostic.RS0030.severity"]);
    }

    /// <summary>Or→And 必须让条件求值失败，未知语法保守判为不满足。</summary>
    [Fact]
    public void MsBuildCondition_AndOrSemantics_AreEvaluated()
    {
        const string OrCondition =
            "'$(MSBuildProjectName)' == 'HunterBox.Domain' Or '$(MSBuildProjectName)' == 'HunterBox.App'";
        Assert.True(EvaluateMsBuildCondition(OrCondition, "HunterBox.App"));
        Assert.False(EvaluateMsBuildCondition(OrCondition, "HunterBox.Infrastructure"));

        const string AndCondition =
            "'$(MSBuildProjectName)' == 'HunterBox.Domain' And '$(MSBuildProjectName)' == 'HunterBox.App'";
        Assert.False(EvaluateMsBuildCondition(AndCondition, "HunterBox.App"));
        Assert.False(EvaluateMsBuildCondition("garbage", "HunterBox.App"));
        Assert.True(EvaluateMsBuildCondition("'$(MSBuildProjectName)' != 'HunterBox.Tests'", "HunterBox.App"));
    }

    /// <summary>未建模属性 fail-closed，AnalysisLevel 接受 10.0 数值版本。</summary>
    [Fact]
    public void MsBuildCondition_UnknownPropertiesFailClosedAndNumericAnalysisLevel()
    {
        using TempDir temp = TempDir.Create();
        temp.File("present.props", "x");
        MsBuildContext context = new("HunterBox.App", temp.Path);

        // 仓库构建文件没有使用 Configuration/Platform/TargetFramework 等属性，
        // 求值器不再维护仅测试使用的映射；未定义属性一律返回 null（调用方按 fail-closed 扫描）。
        Assert.Null(EvaluateMsBuildCondition("'$(Configuration)' == 'Release'", context));
        Assert.Null(EvaluateMsBuildCondition("'$(Platform)' == 'x64'", context));
        Assert.Null(EvaluateMsBuildCondition("'$(UnknownProperty)' == 'x'", context));
        Assert.Null(EvaluateMsBuildCondition("'$(UnknownProperty)' == ''", context));
        Assert.Null(EvaluateMsBuildCondition(
            "('$(MSBuildProjectName)' == 'HunterBox.App' Or '$(MSBuildProjectName)' == 'HunterBox.Domain')"
            + " And '$(Configuration)' == 'Release'",
            context));

        Assert.True(EvaluateMsBuildCondition("Exists('present.props')", context));
        Assert.False(EvaluateMsBuildCondition("Exists('missing.props')", context));
        Assert.False(EvaluateMsBuildCondition("Exists('')", context));
        Assert.True(EvaluateMsBuildCondition("!Exists('missing.props')", context));
        // MSBuildProjectDirectory 只出现在属性值而非 Condition，求值器不再建模。
        Assert.Null(EvaluateMsBuildCondition("Exists('$(MSBuildProjectDirectory)')", context));
        Assert.Null(EvaluateMsBuildCondition("'$(TargetFramework)' == 'net10.0'", context));

        Assert.True(IsRecentAnalysisLevel("10.0"));
        Assert.True(IsRecentAnalysisLevel("latest-recommended"));
        Assert.False(IsRecentAnalysisLevel("9.0"));
    }

    /// <summary>降级判定只认 none/silent/suggestion/hidden，error/warn 不能误报。</summary>
    [Fact]
    public void SeverityDowngrade_OnlyTreatsOffValuesAsDowngrade()
    {
        Assert.True(IsCriticalSeverityKey("DOTNET_DIAGNOSTIC.rs0030.Severity"));
        Assert.False(IsCriticalSeverityKey("dotnet_diagnostic.RS0032.severity"));

        Assert.True(IsDowngradedSeverity("none"));
        Assert.True(IsDowngradedSeverity("SILENT"));
        Assert.True(IsDowngradedSeverity("suggestion"));
        Assert.True(IsDowngradedSeverity("hidden"));
        Assert.False(IsDowngradedSeverity("error"));
        Assert.False(IsDowngradedSeverity("warning"));
        Assert.False(IsDowngradedSeverity("warn"));

        Assert.True(IsDisabledSeverity("none"));
        Assert.True(IsDisabledSeverity("silent"));
        Assert.False(IsDisabledSeverity("suggestion"));
        Assert.False(IsDisabledSeverity("warning"));
    }

    /// <summary>EditorConfig 段支持字符类的匹配分支可被覆盖。</summary>
    [Fact]
    public void EditorConfigSectionMatches_SupportsCharacterClass()
    {
        Assert.True(EditorConfigEvaluator.SectionMatches("src/**/[A-Z]*.cs", "src/HunterBox.App/ModelPanel.cs"));
        Assert.False(EditorConfigEvaluator.SectionMatches("src/**/[0-9]*.cs", "src/HunterBox.App/ModelPanel.cs"));

        // 无路径段按文件名匹配，单星不跨目录；app.manifest 例外依赖这一语义。
        Assert.True(EditorConfigEvaluator.SectionMatches("app.manifest", "src/HunterBox.App/app.manifest"));
        Assert.True(EditorConfigEvaluator.SectionMatches("*.json", "global.json"));
        Assert.False(EditorConfigEvaluator.SectionMatches("*.json", "config/global.json5"));
    }

    /// <summary>P/Invoke 门禁同时识别 LibraryImport 与 DllImport，并检查 System32 参数。</summary>
    [Fact]
    public void PInvokeGate_RecognizesLibraryImportAndChecksSystem32()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
            using System.Runtime.InteropServices;
            static class C
            {
                [LibraryImport("a")]
                private static partial void First();

                [DllImport("b")]
                [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
                private static extern void Second();

                [DllImport("c")]
                [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
                private static extern void Third();
            }
            """);
        List<MethodDeclarationSyntax> methods = [.. tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()];
        List<AttributeSyntax> first = [.. methods[0].AttributeLists.SelectMany(list => list.Attributes)];
        List<AttributeSyntax> second = [.. methods[1].AttributeLists.SelectMany(list => list.Attributes)];
        List<AttributeSyntax> third = [.. methods[2].AttributeLists.SelectMany(list => list.Attributes)];

        Assert.True(IsPInvokeImport(first));
        Assert.True(IsPInvokeImport(second));
        Assert.True(HasSystem32Argument(Assert.Single(second.Where(IsSystem32SearchPath))));
        Assert.False(HasSystem32Argument(Assert.Single(third.Where(IsSystem32SearchPath))));
    }

    /// <summary>元素级 Condition/ExcludeAssets/Update 任一绕过都必须让接线判定失败。</summary>
    [Fact]
    public void AnalyzerWiring_ElementLevelBypasses_AreRejected()
    {
        const string Condition = "'$(MSBuildProjectName)' == 'HunterBox.App'";
        XElement normal = XElement.Parse(
            "<PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" />");
        XElement elementCondition = XElement.Parse(
            $"<PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" Condition=\"'$(MSBuildProjectName)' == 'HunterBox.Domain'\" />");
        XElement excluded = XElement.Parse(
            "<PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" ExcludeAssets=\"analyzers\" />");
        XElement excludedAll = XElement.Parse(
            "<PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" ExcludeAssets=\"compile;all\" />");
        XElement includeWithoutAnalyzers = XElement.Parse(
            "<PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\" IncludeAssets=\"runtime;compile\" />");
        XElement childExclude = XElement.Parse(
            "<PackageReference Include=\"Microsoft.CodeAnalysis.BannedApiAnalyzers\"><ExcludeAssets>analyzers</ExcludeAssets></PackageReference>");

        Assert.True(ConditionAllows(normal, "HunterBox.App"));
        Assert.True(ConditionAllows(elementCondition, "HunterBox.Domain"));
        Assert.False(ConditionAllows(elementCondition, "HunterBox.App"));
        Assert.False(ExcludesAnalyzers(normal));
        Assert.True(ExcludesAnalyzers(excluded));
        Assert.True(ExcludesAnalyzers(excludedAll));
        Assert.True(ExcludesAnalyzers(includeWithoutAnalyzers));
        Assert.True(ExcludesAnalyzers(childExclude));
        Assert.True(EvaluateMsBuildCondition(Condition, "HunterBox.App"));
    }

    /// <summary>带 Condition 的降级标签与新增的开关/规则集属性都必须被识别。</summary>
    [Fact]
    public void BuildPropertyDowngrade_DetectsConditionalAndAdditionalSwitches()
    {
        List<string> offenders = [];
        AssertBuildPropertyNotDowngraded(
            """
            <Project>
              <PropertyGroup>
                <TreatWarningsAsErrors Condition="'$(Configuration)' == 'Release'">false</TreatWarningsAsErrors>
                <RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild>
                <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
                <AnalysisLevel>5</AnalysisLevel>
              </PropertyGroup>
            </Project>
            """,
            "Probe.props",
            offenders);

        Assert.Contains(offenders, issue => issue.Contains("TreatWarningsAsErrors", StringComparison.Ordinal));
        Assert.Contains(offenders, issue => issue.Contains("RunAnalyzersDuringBuild", StringComparison.Ordinal));
        Assert.Contains(offenders, issue => issue.Contains("EnforceCodeStyleInBuild", StringComparison.Ordinal));
        Assert.Contains(offenders, issue => issue.Contains("AnalysisLevel", StringComparison.Ordinal));
    }

    /// <summary>RunAnalyzers=false 与后置 AnalysisMode 降级都必须被识别。</summary>
    [Fact]
    public void BuildPropertyDowngrade_DetectsRunAnalyzersAndLastAnalysisMode()
    {
        List<string> offenders = [];
        AssertBuildPropertyNotDowngraded(
            "<Project><PropertyGroup><RunAnalyzers>false</RunAnalyzers><AnalysisMode>AllEnabledByDefault</AnalysisMode></PropertyGroup></Project>",
            "Probe.props",
            offenders);
        Assert.Contains(offenders, issue => issue.Contains("RunAnalyzers", StringComparison.Ordinal));

        offenders.Clear();
        AssertBuildPropertyNotDowngraded(
            "<Project><PropertyGroup><AnalysisMode>AllEnabledByDefault</AnalysisMode><AnalysisMode>Default</AnalysisMode></PropertyGroup></Project>",
            "Probe.props",
            offenders);
        Assert.Contains(offenders, issue => issue.Contains("AnalysisMode", StringComparison.Ordinal));

        offenders.Clear();
        AssertBuildPropertyNotDowngraded(
            "<Project><PropertyGroup><AnalysisMode>Default</AnalysisMode><AnalysisMode>AllEnabledByDefault</AnalysisMode></PropertyGroup></Project>",
            "Probe.props",
            offenders);
        Assert.Empty(offenders);
    }

    /// <summary>.globalconfig 不是 XML，属性降级检查应跳过而不是抛 XmlException。</summary>
    [Fact]
    public void BuildPropertyDowngrade_GlobalConfig_IsSkippedWithoutThrowing()
    {
        List<string> offenders = [];
        AssertBuildPropertyNotDowngraded(
            "is_global = true\ndotnet_diagnostic.RS0030.severity = none",
            ".globalconfig",
            offenders);
        Assert.Empty(offenders);
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>门禁求值上下文：项目名 + 项目目录。</summary>
    /// <param name="ProjectName">项目名（<c>$(MSBuildProjectName)</c>）。</param>
    /// <param name="ProjectDirectory">项目目录（<c>Exists('...')</c> 的相对路径基准）。</param>
    private readonly record struct MsBuildContext(
        string ProjectName,
        string ProjectDirectory);

    /// <summary>门禁覆盖的全部项目（属性降级在任一项目下生效都要被拦截）。</summary>
    private static readonly string[] GuardedProjects =
    [
        "HunterBox.Domain", "HunterBox.Application", "HunterBox.Infrastructure", "HunterBox.App", "HunterBox.Tests",
    ];

    private static IEnumerable<MsBuildContext> ContextsFor(string projectName)
    {
        string root = SourceTreeLocator.TryFind() ?? AppContext.BaseDirectory;
        string directory = projectName.StartsWith("HunterBox.Tests", StringComparison.Ordinal)
            ? Path.Combine(root, "tests", projectName)
            : Path.Combine(root, "src", projectName);
        yield return new MsBuildContext(projectName, directory);
    }

    /// <summary>
    /// 求值 MSBuild 条件：支持 And/Or/!/括号、属性展开与 <c>Exists(...)</c>。
    /// </summary>
    /// <remarks>
    /// 括号与一元 <c>!</c> 当前仓库构建文件未使用，由单元测试锁定语法，
    /// 用于避免未来合法的 MSBuild 写法被误报为「未接线」。未定义属性按无法判定处理（fail-closed）。
    /// </remarks>
    /// <param name="condition">条件表达式；为 <see langword="null"/> 或空白时视为成立。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>条件成立/不成立/无法识别（<see langword="null"/>）。</returns>
    private static bool? EvaluateMsBuildCondition(string? condition, MsBuildContext context)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        int index = 0;
        bool? result = ParseConditionOr(condition, ref index, context);
        SkipWhitespace(condition, ref index);
        return index == condition.Length ? result : null;
    }

    /// <summary>把条件求值成单个布尔（任一代表性配置不成立即为 <see langword="false"/>；无法识别也返回 false）。</summary>
    /// <param name="condition">条件表达式。</param>
    /// <param name="projectName">项目名。</param>
    /// <returns>在全部代表性配置下都成立时返回 <see langword="true"/>。</returns>
    private static bool EvaluateMsBuildCondition(string condition, string projectName)
    {
        bool? result = true;
        foreach (MsBuildContext context in ContextsFor(projectName))
        {
            result = And(result, EvaluateMsBuildCondition(condition, context));
        }

        return result == true;
    }

    /// <summary>三值逻辑的 And。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>结果；任一为 false 时为 false，任一未知且无 false 时为 null。</returns>
    private static bool? And(bool? left, bool? right) =>
        left == false || right == false ? false : left is null || right is null ? null : true;

    /// <summary>三值逻辑的 Or。</summary>
    /// <param name="left">左值。</param>
    /// <param name="right">右值。</param>
    /// <returns>结果；任一为 true 时为 true，任一未知且无 true 时为 null。</returns>
    private static bool? Or(bool? left, bool? right) =>
        left == true || right == true ? true : left is null || right is null ? null : false;

    /// <summary>三值逻辑的 Not。</summary>
    /// <param name="value">操作数。</param>
    /// <returns>取反结果；未知仍为未知。</returns>
    private static bool? Not(bool? value) => value is null ? null : !value;

    /// <summary>解析 Or 表达式（最低优先级）。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>求值结果。</returns>
    private static bool? ParseConditionOr(string text, ref int index, MsBuildContext context)
    {
        bool? result = ParseConditionAnd(text, ref index, context);
        while (TryReadKeyword(text, ref index, "Or"))
        {
            result = Or(result, ParseConditionAnd(text, ref index, context));
        }

        return result;
    }

    /// <summary>解析 And 表达式（高于 Or）。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>求值结果。</returns>
    private static bool? ParseConditionAnd(string text, ref int index, MsBuildContext context)
    {
        bool? result = ParseConditionUnary(text, ref index, context);
        while (TryReadKeyword(text, ref index, "And"))
        {
            result = And(result, ParseConditionUnary(text, ref index, context));
        }

        return result;
    }

    /// <summary>解析一元表达式：<c>!</c>、括号或单个条件项。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>求值结果。</returns>
    private static bool? ParseConditionUnary(string text, ref int index, MsBuildContext context)
    {
        SkipWhitespace(text, ref index);
        if (index < text.Length && text[index] == '!')
        {
            index++;
            return Not(ParseConditionUnary(text, ref index, context));
        }

        if (index < text.Length && text[index] == '(')
        {
            index++;
            bool? inner = ParseConditionOr(text, ref index, context);
            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != ')')
            {
                return null;
            }

            index++;
            return inner;
        }

        return ParseConditionTerm(text, ref index, context);
    }

    /// <summary>解析单个条件项：<c>Exists('...')</c> 或 <c>左 ==/!= 右</c>。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>求值结果；语法无法识别或属性未定义时为 <see langword="null"/>。</returns>
    private static bool? ParseConditionTerm(string text, ref int index, MsBuildContext context)
    {
        SkipWhitespace(text, ref index);
        if (TryReadExists(text, ref index, context, out bool? exists))
        {
            return exists;
        }

        ConditionOperand left = ReadConditionOperand(text, ref index, context);
        if (!left.Parsed)
        {
            return null;
        }

        SkipWhitespace(text, ref index);
        string? op = ReadConditionOperator(text, ref index);
        if (op is null)
        {
            return null;
        }

        ConditionOperand right = ReadConditionOperand(text, ref index, context);
        if (!right.Parsed || left.Value is null || right.Value is null)
        {
            // 未定义属性不等于空串，按「无法判定」处理（接线门禁 fail-closed）。
            return null;
        }

        bool equal = string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
        return string.Equals(op, "==", StringComparison.Ordinal) ? equal : !equal;
    }

    /// <summary>读取 <c>Exists('相对/绝对路径')</c>；不是该函数时返回 <see langword="false"/> 并回退位置。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="context">求值上下文（相对路径以项目目录为基准）。</param>
    /// <param name="result">求值结果。</param>
    /// <returns>识别为 Exists 调用时返回 <see langword="true"/>。</returns>
    private static bool TryReadExists(
        string text,
        ref int index,
        MsBuildContext context,
        out bool? result)
    {
        result = null;
        int save = index;
        SkipWhitespace(text, ref index);
        const string Name = "Exists";
        if (index + Name.Length > text.Length
            || string.Compare(text, index, Name, 0, Name.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            index = save;
            return false;
        }

        index += Name.Length;
        SkipWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '(')
        {
            index = save;
            return false;
        }

        index++;
        ConditionOperand argument = ReadConditionOperand(text, ref index, context);
        SkipWhitespace(text, ref index);
        if (!argument.Parsed || index >= text.Length || text[index] != ')')
        {
            // 语法错误：按无法识别处理，避免误判为「不成立」。
            return true;
        }

        index++;
        if (argument.Value is null)
        {
            // 属性未定义：无法判定，交给调用方按 fail-closed 处理。
            return true;
        }

        if (argument.Value.Length == 0)
        {
            // 空路径恒不存在，不能让 Exists('') 把门禁变成恒真。
            result = false;
            return true;
        }

        string target = Path.IsPathFullyQualified(argument.Value)
            ? argument.Value
            : Path.Combine(context.ProjectDirectory, argument.Value);
        result = File.Exists(target) || Directory.Exists(target);
        return true;
    }

    /// <summary>条件项一侧的读取结果：<see langword="false"/> 表示语法错误，<c>Value</c> 为 <see langword="null"/> 表示属性未定义。</summary>
    /// <param name="Parsed">是否成功读取语法。</param>
    /// <param name="Value">展开后的值；属性未定义时为 <see langword="null"/>。</param>
    private readonly record struct ConditionOperand(bool Parsed, string? Value);

    /// <summary>读取条件项一侧（单引号字符串或裸值），并展开常用属性。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>读取结果；语法错误时 <c>Parsed</c> 为 <see langword="false"/>。</returns>
    private static ConditionOperand ReadConditionOperand(string text, ref int index, MsBuildContext context)
    {
        SkipWhitespace(text, ref index);
        if (index >= text.Length)
        {
            return new ConditionOperand(false, null);
        }

        string raw;
        if (text[index] == '\'')
        {
            int end = text.IndexOf('\'', index + 1);
            if (end < 0)
            {
                return new ConditionOperand(false, null);
            }

            raw = text[(index + 1)..end];
            index = end + 1;
        }
        else
        {
            int start = index;
            while (index < text.Length
                && !char.IsWhiteSpace(text[index])
                && text[index] is not ('(' or ')' or '!' or '='))
            {
                index++;
            }

            if (index == start)
            {
                return new ConditionOperand(false, null);
            }

            raw = text[start..index];
        }

        return new ConditionOperand(true, ExpandProperties(raw, context));
    }

    /// <summary>展开 <c>$(属性)</c>：未定义属性返回 <see langword="null"/>，与已知空值区分。</summary>
    /// <param name="value">原始值。</param>
    /// <param name="context">求值上下文。</param>
    /// <returns>展开后的值；含未定义属性时为 <see langword="null"/>。</returns>
    private static string? ExpandProperties(string value, MsBuildContext context)
    {
        string? expanded = value;
        foreach (Match match in Regex.Matches(value, @"\$\((?<name>[A-Za-z0-9_]+)\)"))
        {
            string? property = match.Groups["name"].Value switch
            {
                "MSBuildProjectName" => context.ProjectName,
                // 只建模仓库 Condition 中真实使用的属性；
                // MSBuildProjectDirectory 仅出现在属性值里，求值器不解析属性值，因此不再建模。
                _ => null,
            };
            if (property is null)
            {
                return null;
            }

            expanded = expanded!.Replace(match.Value, property, StringComparison.Ordinal);
        }

        return expanded;
    }

    /// <summary>读取 <c>==</c>/<c>!=</c> 运算符。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <returns>运算符；不是比较运算时为 <see langword="null"/>。</returns>
    private static string? ReadConditionOperator(string text, ref int index)
    {
        if (index + 1 < text.Length
            && (text[index] == '=' || text[index] == '!')
            && text[index + 1] == '=')
        {
            string op = text.Substring(index, 2);
            index += 2;
            return op;
        }

        return null;
    }

    /// <summary>读取关键字（And/Or），要求前后是词边界；不是关键字时回退位置。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    /// <param name="keyword">关键字。</param>
    /// <returns>读到关键字时返回 <see langword="true"/>。</returns>
    private static bool TryReadKeyword(string text, ref int index, string keyword)
    {
        int save = index;
        SkipWhitespace(text, ref index);
        if (index + keyword.Length > text.Length
            || string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            index = save;
            return false;
        }

        int after = index + keyword.Length;
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
        {
            index = save;
            return false;
        }

        index = after;
        return true;
    }

    /// <summary>跳过空白。</summary>
    /// <param name="text">条件文本。</param>
    /// <param name="index">当前读取位置。</param>
    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    /// <summary>严重级别键名忽略大小写（EditorConfig 规范）。</summary>
    /// <param name="key">配置键。</param>
    /// <returns>是 RS0030/RS0031 的严重级别键时返回 <see langword="true"/>。</returns>
    private static bool IsCriticalSeverityKey(string key) =>
        string.Equals(key, "dotnet_diagnostic.RS0030.severity", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "dotnet_diagnostic.RS0031.severity", StringComparison.OrdinalIgnoreCase);

    /// <summary>只有 none/silent/suggestion/hidden 算降级；warn/warning/error 都是有效值。</summary>
    /// <param name="value">严重级别值。</param>
    /// <returns>属于降级值时返回 <see langword="true"/>。</returns>
    private static bool IsDowngradedSeverity(string value) =>
        string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "silent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "suggestion", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "hidden", StringComparison.OrdinalIgnoreCase);

    /// <summary>只有 none/silent 算「关闭分析器」；suggestion 仍会运行规则。</summary>
    /// <param name="value">严重级别值。</param>
    /// <returns>属于关闭值时返回 <see langword="true"/>。</returns>
    private static bool IsDisabledSeverity(string value) =>
        string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "silent", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析构建文件的属性元素，按统一条件求值拦截分析器降级。</summary>
    /// <param name="text">构建文件内容。</param>
    /// <param name="relative">相对路径（报告用）。</param>
    /// <param name="offenders">违规清单。</param>
    private static void AssertBuildPropertyNotDowngraded(string text, string relative, List<string> offenders)
    {
        // .globalconfig 是 INI 形式，由行级严重级别检查覆盖，不参与 XML 解析。
        if (string.Equals(Path.GetFileName(relative), ".globalconfig", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        XElement root = XElement.Parse(text);
        foreach (XElement element in root.Descendants())
        {
            // 条件按统一求值器判断是否可能生效；非 PropertyGroup 内的属性只忽略父级条件。
            if (!MayApply(element))
            {
                continue;
            }

            string name = element.Name.LocalName;
            string value = element.Value.Trim();
            bool isFalse = string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            switch (name)
            {
                case "TreatWarningsAsErrors" when isFalse:
                    offenders.Add($"{relative}: TreatWarningsAsErrors 不能被降级为 false");
                    break;
                case "EnableNETAnalyzers" when isFalse:
                    offenders.Add($"{relative}: EnableNETAnalyzers 不能被关闭");
                    break;
                case "RunAnalyzers" when isFalse:
                case "RunAnalyzersDuringBuild" when isFalse:
                    offenders.Add($"{relative}: {name} 不能被关闭");
                    break;
                case "EnforceCodeStyleInBuild" when isFalse:
                    offenders.Add($"{relative}: EnforceCodeStyleInBuild 不能被关闭");
                    break;
                case "NoWarn" when ContainsRs0030Or0031(value):
                case "WarningsNotAsErrors" when ContainsRs0030Or0031(value):
                    offenders.Add($"{relative}: {name} 不能包含 RS0030/RS0031");
                    break;
                case "AnalysisLevel" when !IsRecentAnalysisLevel(value):
                    offenders.Add($"{relative}: AnalysisLevel 不能降级为 {value}");
                    break;
            }
        }

        // 同一文件可能出现多个 AnalysisMode，按最后生效值判断。
        XElement? lastMode = root.Descendants()
            .LastOrDefault(element => element.Name.LocalName == "AnalysisMode" && MayApply(element));
        if (lastMode is not null && !IsAllAnalyzerMode(lastMode.Value.Trim()))
        {
            offenders.Add($"{relative}: AnalysisMode 不能降级为 {lastMode.Value.Trim()}");
        }
    }

    /// <summary>
    /// 属性是否可能在任一门禁项目/代表性配置下生效；条件无法求值时保守视为生效
    /// （降级写法一律拦截）。
    /// </summary>
    /// <param name="element">属性元素。</param>
    /// <returns>可能生效时返回 <see langword="true"/>。</returns>
    private static bool MayApply(XElement element)
    {
        string? groupCondition = element.Parent?.Name.LocalName == "PropertyGroup"
            ? element.Parent.Attribute("Condition")?.Value
            : null;
        string? elementCondition = element.Attribute("Condition")?.Value;
        foreach (string project in GuardedProjects)
        {
            foreach (MsBuildContext context in ContextsFor(project))
            {
                bool? group = EvaluateMsBuildCondition(groupCondition, context);
                bool? own = EvaluateMsBuildCondition(elementCondition, context);
                if (group is null || own is null || (group == true && own == true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>内容是否包含 RS0030/RS0031。</summary>
    /// <param name="value">属性值。</param>
    /// <returns>包含时返回 <see langword="true"/>。</returns>
    private static bool ContainsRs0030Or0031(string value) =>
        value.Contains("RS0030", StringComparison.OrdinalIgnoreCase)
        || value.Contains("RS0031", StringComparison.OrdinalIgnoreCase);

    /// <summary>AnalysisLevel 是否仍为最新规则集（接受 10.0 等数值版本）。</summary>
    /// <param name="value">属性值。</param>
    /// <returns>latest/preview 系或主版本不低于 10 时返回 <see langword="true"/>。</returns>
    private static bool IsRecentAnalysisLevel(string value) =>
        value.StartsWith("latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase)
        || (Version.TryParse(value, out Version? version) && version.Major >= 10);

    /// <summary>元素级 <c>Condition</c> 必须在全部代表性配置下成立。</summary>
    /// <param name="element">构建元素。</param>
    /// <param name="projectName">项目名。</param>
    /// <returns>无条件或条件成立时返回 <see langword="true"/>。</returns>
    private static bool ConditionAllows(XElement element, string projectName)
    {
        string? condition = element.Attribute("Condition")?.Value;
        return condition is null || EvaluateMsBuildCondition(condition, projectName);
    }

    /// <summary><c>ExcludeAssets</c>/<c>IncludeAssets</c>（属性或子元素）不能排除编译器分析器。</summary>
    /// <param name="element">包引用元素。</param>
    /// <returns>排除 analyzers/all 时返回 <see langword="true"/>。</returns>
    private static bool ExcludesAnalyzers(XElement element)
    {
        if (ExcludesAnalyzerAssets(element.Attribute("ExcludeAssets")?.Value))
        {
            return true;
        }

        string? include = element.Attribute("IncludeAssets")?.Value;
        if (include is not null && !IncludeAllowsAnalyzers(include))
        {
            return true;
        }

        foreach (XElement child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "ExcludeAssets" when ExcludesAnalyzerAssets(child.Value):
                    return true;
                case "IncludeAssets" when !IncludeAllowsAnalyzers(child.Value):
                    return true;
            }
        }

        return false;
    }

    /// <summary>资产列表是否显式排除 analyzers/all。</summary>
    /// <param name="value">资产列表。</param>
    /// <returns>排除时返回 <see langword="true"/>。</returns>
    private static bool ExcludesAnalyzerAssets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(part, "analyzers", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>IncludeAssets</c> 必须包含 analyzers（或 all），否则分析器不会被引用。</summary>
    /// <param name="value">资产列表。</param>
    /// <returns>包含 analyzers/all 时返回 <see langword="true"/>。</returns>
    private static bool IncludeAllowsAnalyzers(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(part, "analyzers", StringComparison.OrdinalIgnoreCase));

    /// <summary>是否等价于「全部规则开启」的 AnalysisMode。</summary>
    /// <param name="value">AnalysisMode 值。</param>
    /// <returns>全部开启时返回 <see langword="true"/>。</returns>
    private static bool IsAllAnalyzerMode(string value) =>
        string.Equals(value, "AllEnabledByDefault", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "All", StringComparison.OrdinalIgnoreCase);

    /// <summary>P/Invoke 门禁识别的导入特性（<c>DllImport</c>/<c>LibraryImport</c>）。</summary>
    /// <param name="attributes">方法特性。</param>
    /// <returns>任一导入特性存在时返回 <see langword="true"/>。</returns>
    private static bool IsPInvokeImport(IEnumerable<AttributeSyntax> attributes) =>
        attributes.Any(attribute => TestSourceIndex.AttributeLastName(attribute) is "DllImport" or "LibraryImport");

    /// <summary>搜索路径特性（<c>DefaultDllImportSearchPaths</c>）。</summary>
    /// <param name="attribute">特性语法。</param>
    /// <returns>是搜索路径特性时返回 <see langword="true"/>。</returns>
    private static bool IsSystem32SearchPath(AttributeSyntax attribute) =>
        TestSourceIndex.AttributeLastName(attribute) is "DefaultDllImportSearchPaths";

    /// <summary>搜索路径特性是否显式包含 <c>System32</c>。</summary>
    /// <param name="attribute">搜索路径特性。</param>
    /// <returns>包含 System32 时返回 <see langword="true"/>。</returns>
    private static bool HasSystem32Argument(AttributeSyntax attribute) =>
        attribute.ArgumentList is not null
        && attribute.ArgumentList.Arguments.Any(
            argument => argument.Expression.ToString().Contains("System32", StringComparison.Ordinal));

    private static bool IsNamespace(string name, string target) =>
        string.Equals(name, target, StringComparison.Ordinal)
        || name.StartsWith(target + ".", StringComparison.Ordinal);

    private static void AssertNoViolations(string title, List<string> offenders)
    {
        Assert.True(
            offenders.Count == 0,
            title + "：" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(30)));
    }
}
