using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HunterBox.Tests;

/// <summary>一个项目的解析/编译结果，供多个源码门禁共享。</summary>
/// <param name="Trees">源文件语法树。</param>
/// <param name="Compilation">包含全部语法树的一次编译（语义解析用）。</param>
internal sealed record ProjectSource(IReadOnlyList<SyntaxTree> Trees, CSharpCompilation Compilation);

/// <summary>按项目缓存源码语法树与编译：整个测试进程只解析/编译一次。</summary>
internal static class SourceTreeCompilationCache
{
    private static readonly Lazy<Dictionary<string, ProjectSource>> Sources = new(Build);

    /// <summary>取指定项目的源码缓存。</summary>
    /// <param name="projectName">项目名（如 <c>HunterBox.App</c>）。</param>
    /// <returns>语法树与编译。</returns>
    public static ProjectSource Get(string projectName) => Sources.Value[projectName];

    /// <summary>测试项目源码语法树：所有门禁共享，整个测试进程只解析一次。</summary>
    public static IReadOnlyList<SyntaxTree> TestTrees => TestSources.Value;

    /// <summary>门禁语义解析引用（供内联源码门禁用例复用）。</summary>
    public static MetadataReference[] References => GuardReferences.Value;

    private static readonly Lazy<IReadOnlyList<SyntaxTree>> TestSources = new(() =>
    {
        string root = SourceTreeLocator.RootOrThrow();
        return
        [
            .. SourceTreeLocator.EnumerateSourceFiles(Path.Combine(root, "tests", "HunterBox.Tests")).Select(file =>
                CSharpSyntaxTree.ParseText(
                    File.ReadAllText(file),
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: file)),
        ];
    });

    private static Dictionary<string, ProjectSource> Build()
    {
        string root = SourceTreeLocator.RootOrThrow();
        Dictionary<string, ProjectSource> sources = new(StringComparer.Ordinal);
        foreach (string project in new[]
        {
            "HunterBox.Domain", "HunterBox.Application", "HunterBox.Infrastructure", "HunterBox.App",
        })
        {
            List<SyntaxTree> trees =
            [
                .. SourceTreeLocator.EnumerateSourceFiles(Path.Combine(root, "src", project)).Select(file =>
                    CSharpSyntaxTree.ParseText(
                        File.ReadAllText(file),
                        new CSharpParseOptions(LanguageVersion.Latest),
                        path: file)),
            ];
            sources[project] = new ProjectSource(
                trees,
                CSharpCompilation.Create(
                    "Guard-" + project,
                    trees,
                    GuardReferences.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        }

        return sources;
    }

    /// <summary>门禁语义解析引用：TPA + 本解决方案的三个被测试程序集，确保属性/参数类型可解析。</summary>
    private static readonly Lazy<MetadataReference[]> GuardReferences = new(() =>
    {
        List<MetadataReference> references = [];
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && !string.IsNullOrEmpty(tpa))
        {
            references.AddRange(tpa.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)));
        }

        references.Add(MetadataReference.CreateFromFile(
            typeof(HunterBox.Application.Settings.ISettings).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(
            typeof(HunterBox.Domain.Mods.ModRecord).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(
            typeof(HunterBox.Infrastructure.IO.SystemFileSystem).Assembly.Location));
        return [.. references];
    });
}
