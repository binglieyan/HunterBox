using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace HunterBox.Tests;

/// <summary>
/// 窗口标题版本必须来自程序集（csproj 的 <c>Version</c> 是唯一来源），
/// 不允许再维护第二份常量，避免发版时标题与日志/崩溃报告漂移。
/// </summary>
public partial class VersionConsistencyTests
{
    [SourceTreeFact]
    public void WindowTitle_UsesAssemblyVersion_SingleSource()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string appDirectory = Path.Combine(root, "src", "HunterBox.App");
        string mainWindow = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.xaml.cs"));
        string startupUi = File.ReadAllText(Path.Combine(appDirectory, "MainWindow.StartupUi.cs"));
        string app = File.ReadAllText(Path.Combine(appDirectory, "App.xaml.cs"));
        string csproj = File.ReadAllText(Path.Combine(appDirectory, "HunterBox.App.csproj"));

        Assert.DoesNotContain("const string AppVersion", mainWindow, StringComparison.Ordinal);
        Assert.Contains("App.AppVersion", startupUi, StringComparison.Ordinal);
        Assert.Contains("Assembly.GetName().Version", app, StringComparison.Ordinal);
        Assert.Matches(@"<Version>\d+\.\d+\.\d+</Version>", csproj);
    }

    [SourceTreeFact]
    public void AppSources_HaveNoHardcodedWindowTitleVersion()
    {
        string root = SourceTreeLocator.RootOrThrow();
        foreach (string file in SourceTreeLocator.EnumerateSourceFiles(Path.Combine(root, "src", "HunterBox.App")))
        {
            string text = File.ReadAllText(file);
            Assert.False(
                HardcodedTitleVersion().IsMatch(text),
                $"{Path.GetRelativePath(root, file)} 仍硬编码了窗口标题版本；请使用 App.AppVersion");
        }
    }

    [GeneratedRegex(@"HunterBox v\d+\.\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex HardcodedTitleVersion();

    /// <summary>csproj 三处版本一致；所有产品文档的版本字段与测试计数都必须与源码一致。</summary>
    [SourceTreeFact]
    public void VersionFields_AreSynchronizedAcrossCsprojAndDocs()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string csproj = File.ReadAllText(Path.Combine(root, "src", "HunterBox.App", "HunterBox.App.csproj"));
        Match version = Regex.Match(csproj, @"<Version>([^<]+)</Version>");
        Assert.True(version.Success, "csproj 缺少 <Version>");

        string current = version.Groups[1].Value;
        Assert.Contains($"<AssemblyVersion>{current}.0</AssemblyVersion>", csproj, StringComparison.Ordinal);
        Assert.Contains($"<FileVersion>{current}.0</FileVersion>", csproj, StringComparison.Ordinal);

        // 仓库已移除 CHANGELOG：版本号唯一来源是 csproj，产品文档的版本字段必须逐文档与之一致。
        // 扫描 README 与 docs 下全部 Markdown，按「字段标签 + 值」提取产品版本，
        // 并逐文档断言至少存在一个版本字段，删除字段会直接失败。
        // 全文只遍历一遍；标签行出现非当前版本即失败。
        string[] versionLabels = ["当前版本", "产品版本", "代码基线", "HunterBox v", "记录"];
        Dictionary<string, string> docs = new(StringComparer.Ordinal)
        {
            ["README.md"] = File.ReadAllText(Path.Combine(root, "README.md")),
        };
        foreach (string file in SourceTreeLocator.EnumerateRepositoryFiles()
            .Where(file => file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && file.StartsWith(Path.Combine(root, "docs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            docs[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllText(file);
        }

        List<string> mismatches = [];
        Dictionary<string, int> labeledCounts = new(StringComparer.Ordinal);
        foreach ((string relative, string text) in docs)
        {
            foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                bool labeled = versionLabels.Any(label => line.Contains(label, StringComparison.Ordinal));
                foreach (Match match in VersionToken().Matches(line))
                {
                    string declared = match.Groups[1].Value;
                    if (labeled)
                    {
                        labeledCounts[relative] = labeledCounts.GetValueOrDefault(relative) + 1;
                    }

                    if (!labeled || string.Equals(declared, current, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    mismatches.Add($"{relative}: 版本字段为 {declared}，应为 {current}（{line.Trim()}）");
                }
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "产品文档版本字段不一致：" + Environment.NewLine + string.Join(Environment.NewLine, mismatches));

        string[] requiredVersionDocs =
        [
            "README.md", "docs/SPEC.md", "docs/wiki/README.md", "docs/wiki/01-overview.md",
            "docs/ARCHITECTURE.md", "docs/wiki/09-build-test-run.md", "docs/wiki/10-test-map.md",
        ];
        List<string> missingVersionFields = [.. requiredVersionDocs.Where(relative => labeledCounts.GetValueOrDefault(relative) == 0)];
        Assert.True(
            missingVersionFields.Count == 0,
            "以下文档缺少可识别的版本字段：" + Environment.NewLine + string.Join(Environment.NewLine, missingVersionFields));

        // 测试计数由源码统计并在三份文档中断言，发版时不会漏改。
        (int fact, int theory, int derived, int methods, Dictionary<string, int> derivedByName) = CountTestMethods();
        string canonicalCounts = $"`[Fact]` {fact} / `[Theory]` {theory} / 派生 `[Fact]` {derived}";
        foreach (string relative in new[]
        {
            "docs/ARCHITECTURE.md", "docs/wiki/09-build-test-run.md", "docs/wiki/10-test-map.md",
        })
        {
            Assert.Contains(canonicalCounts, docs[relative], StringComparison.Ordinal);
        }

        Assert.Contains(
            $"共 {methods} 个测试方法",
            docs["docs/wiki/10-test-map.md"],
            StringComparison.Ordinal);

        // 仓库不再记录运行计数（CHANGELOG 已移除）：文档只保留由源码统计的用例数。
        string breakdown = string.Join(
            "、",
            TestSourceIndex.DerivedAttributeNames.Select(name => $"`{name}` {derivedByName.GetValueOrDefault(name)}"));
        Assert.Contains(breakdown, docs["docs/wiki/09-build-test-run.md"], StringComparison.Ordinal);

        // 09 文档必须与方法总数和测试源文件数一致。
        string buildDoc = docs["docs/wiki/09-build-test-run.md"];
        Assert.Contains($"共 {methods} 个测试方法", buildDoc, StringComparison.Ordinal);
        Match sourceFiles = Regex.Match(buildDoc, @"(\d+) 个测试源文件");
        Assert.True(sourceFiles.Success, "09 文档缺少测试源文件计数");
        Assert.Equal(TestSourceIndex.Trees.Count, int.Parse(sourceFiles.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>版本 token：可选 <c>v</c> 前缀的三段数字，避免命中更长数字的前缀。</summary>
    [GeneratedRegex(@"(?<![\d.])v?(\d+\.\d+\.\d+)(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionToken();

    /// <summary>统计测试源码里的 <c>[Fact]</c>/<c>[Theory]</c>/派生事实方法数量（复用共享测试语法树）。</summary>
    /// <returns>(普通 Fact, Theory, 派生 Fact, 方法总数, 各派生特性计数)。</returns>
    private static (int Fact, int Theory, int Derived, int Methods, Dictionary<string, int> DerivedByName) CountTestMethods()
    {
        HashSet<string> derivedNames = new(TestSourceIndex.DerivedAttributeNames, StringComparer.Ordinal);
        Dictionary<string, int> derivedByName = new(StringComparer.Ordinal);
        int fact = 0;
        int theory = 0;
        int derived = 0;
        foreach (SyntaxTree tree in TestSourceIndex.Trees)
        {
            foreach (Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method in
                tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
            {
                foreach (Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax attribute in
                    method.AttributeLists.SelectMany(list => list.Attributes))
                {
                    string name = TestSourceIndex.AttributeLastName(attribute);
                    if (derivedNames.Contains(name))
                    {
                        derived++;
                        derivedByName[name] = derivedByName.GetValueOrDefault(name) + 1;
                        break;
                    }

                    if (string.Equals(name, "Fact", StringComparison.Ordinal))
                    {
                        fact++;
                        break;
                    }

                    if (string.Equals(name, "Theory", StringComparison.Ordinal))
                    {
                        theory++;
                        break;
                    }
                }
            }
        }

        return (fact, theory, derived, fact + theory + derived, derivedByName);
    }
}
