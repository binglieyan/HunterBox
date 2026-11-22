using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace HunterBox.Tests;

/// <summary>
/// 重复实现候选检测：按方法体 token 结构做归一化哈希，列出结构相同的多个方法。
/// 这不是格式检查，而是把“复制粘贴后只改变量名”的实现找出来，供后续收敛。
/// </summary>
public class DuplicateCodeCandidateTests
{
    private const int MinimumStatements = 8;
    private const int MinimumTokens = 40;
    private const int BlockingStatements = 12;

    private readonly ITestOutputHelper _output;

    /// <summary>注入测试输出。</summary>
    /// <param name="output">xUnit 输出。</param>
    public DuplicateCodeCandidateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>扫描 src 下的方法体，输出重复候选；超过阈值的重复直接失败。</summary>
    [SourceTreeFact]
    public void SourceMethods_ReportStructuralDuplicates()
    {
        string root = SourceTreeLocator.RootOrThrow();
        List<Candidate> candidates = [];
        // 复用共享语法树缓存，App/Application 源码不再由本门禁重复解析。
        foreach (string project in new[]
        {
            "HunterBox.Domain", "HunterBox.Application", "HunterBox.Infrastructure", "HunterBox.App",
        })
        {
            foreach (SyntaxTree tree in SourceTreeCompilationCache.Get(project).Trees)
            {
                candidates.AddRange(ParseCandidates(Path.GetRelativePath(root, tree.FilePath), tree));
            }
        }

        List<IGrouping<string, Candidate>> groups = FindGroups(candidates);
        List<string> lines = [];
        foreach (IGrouping<string, Candidate> group in groups.Take(40))
        {
            Candidate first = group.First();
            lines.Add($"[{first.Statements} 条语句 × {group.Count()} 处] {first.Name}");
            foreach (Candidate candidate in group.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"    {candidate.Path}:{candidate.Line}  {candidate.Name}");
            }
        }

        string report = lines.Count == 0
            ? "未发现结构重复的方法体。"
            : "重复实现候选：" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        _output.WriteLine(report);
        string reportPath = Path.Combine(AppContext.BaseDirectory, "duplication-candidates.txt");
        File.WriteAllText(reportPath, report);
        _output.WriteLine($"候选清单已写入：{reportPath}");

        List<IGrouping<string, Candidate>> blocking = groups
            .Where(group => group.First().Statements >= BlockingStatements)
            .ToList();
        Assert.True(
            blocking.Count == 0,
            "发现大规模结构重复（>= " + BlockingStatements + " 条语句），请先收敛再提交："
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                blocking.Select(group => $"{group.First().Name} × {group.Count()} 处")));
    }

    /// <summary>检测器自检：两个只改变量名的同构方法必须被识别为同一组。</summary>
    [Fact]
    public void Detector_FindsSyntheticStructuralDuplicate()
    {
        const string source = """
            internal static class Sample
            {
                public static int First(int input)
                {
                    int value = input + 1;
                    value += 2;
                    value -= 3;
                    value *= 4;
                    value /= 2;
                    if (value > 10)
                    {
                        value = 10;
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        value += i;
                    }

                    while (value < 0)
                    {
                        value++;
                    }

                    return value;
                }

                public static int Second(int seed)
                {
                    int result = seed + 1;
                    result += 2;
                    result -= 3;
                    result *= 4;
                    result /= 2;
                    if (result > 10)
                    {
                        result = 10;
                    }

                    for (int j = 0; j < 3; j++)
                    {
                        result += j;
                    }

                    while (result < 0)
                    {
                        result++;
                    }

                    return result;
                }
            }
            """;

        List<IGrouping<string, Candidate>> groups = FindGroups(ParseCandidates("probe.cs", source));
        Assert.Contains(groups, group => group.Count() == 2 && group.All(candidate => candidate.Statements >= MinimumStatements));
    }

    /// <summary>检测器自检：结构相同但调用目标不同的方法不能被误判为重复。</summary>
    [Fact]
    public void Detector_DoesNotGroupDifferentCallTargets()
    {
        const string source = """
            internal static class Sample
            {
                public static int First(int input)
                {
                    int value = input + 1;
                    value += System.IO.File.ReadAllText("a").Length;
                    value -= 3;
                    value *= 4;
                    value /= 2;
                    if (value > 10)
                    {
                        value = 10;
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        value += i;
                    }

                    while (value < 0)
                    {
                        value++;
                    }

                    return value;
                }

                public static int Second(int input)
                {
                    int value = input + 1;
                    value += System.IO.Directory.GetFiles("a").Length;
                    value -= 3;
                    value *= 4;
                    value /= 2;
                    if (value > 10)
                    {
                        value = 10;
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        value += i;
                    }

                    while (value < 0)
                    {
                        value++;
                    }

                    return value;
                }
            }
            """;

        List<IGrouping<string, Candidate>> groups = FindGroups(ParseCandidates("probe.cs", source));
        Assert.DoesNotContain(groups, group => group.Select(candidate => candidate.Name).Distinct().Count() > 1);
    }

    private static List<Candidate> ParseCandidates(string relative, string text)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            text,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: relative);
        return ParseCandidates(relative, tree);
    }

    private static List<Candidate> ParseCandidates(string relative, SyntaxTree tree)
    {
        List<Candidate> candidates = [];
        foreach (BlockSyntax body in tree.GetRoot().DescendantNodes().OfType<BlockSyntax>())
        {
            if (body.Statements.Count < MinimumStatements)
            {
                continue;
            }

            if (body.DescendantTokens().Count() < MinimumTokens)
            {
                continue;
            }

            string normalized = Normalize(body);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            FileLinePositionSpan span = body.GetLocation().GetLineSpan();
            candidates.Add(new Candidate(
                relative,
                span.StartLinePosition.Line + 1,
                DescribeOwner(body),
                body.Statements.Count,
                hash));
        }

        return candidates;
    }

    private static List<IGrouping<string, Candidate>> FindGroups(IEnumerable<Candidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.Hash)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Max(candidate => candidate.Statements))
            .ToList();

    private static string DescribeOwner(BlockSyntax body) =>
        body.Parent switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            LocalFunctionStatementSyntax local => local.Identifier.ValueText,
            AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText,
            _ => "unknown",
        };

    /// <summary>
    /// 归一化方法体 token：只把「局部变量/参数名」替换为稳定占位，调用目标/类型名保持原样，
    /// 避免结构相同但语义不同的代码被误判重复。
    /// </summary>
    /// <param name="body">方法体。</param>
    /// <returns>归一化文本。</returns>
    private static string Normalize(BlockSyntax body)
    {
        Dictionary<string, string> localNames = new(StringComparer.Ordinal);
        void Remember(string name)
        {
            if (!localNames.ContainsKey(name))
            {
                localNames[name] = "LV" + localNames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (body.Parent is MethodDeclarationSyntax method)
        {
            foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
            {
                Remember(parameter.Identifier.ValueText);
            }
        }

        foreach (SyntaxNode node in body.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    Remember(declarator.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax forEach:
                    Remember(forEach.Identifier.ValueText);
                    break;
                case CatchDeclarationSyntax catchDeclaration when catchDeclaration.Identifier.ValueText.Length > 0:
                    Remember(catchDeclaration.Identifier.ValueText);
                    break;
                case SingleVariableDesignationSyntax designation:
                    Remember(designation.Identifier.ValueText);
                    break;
            }
        }

        return string.Join(
            ' ',
            body.DescendantTokens()
                .Where(token => !token.IsKind(SyntaxKind.OpenBraceToken) && !token.IsKind(SyntaxKind.CloseBraceToken))
                .Select(token => token.IsKind(SyntaxKind.IdentifierToken)
                    && localNames.TryGetValue(token.ValueText, out string? placeholder)
                        ? placeholder
                        : token.ValueText));
    }

    private sealed record Candidate(string Path, int Line, string Name, int Statements, string Hash);
}
