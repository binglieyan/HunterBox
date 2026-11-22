namespace HunterBox.Tests;

/// <summary>
/// 注释、编码与行尾规范：防止「残句冒号」这类模板产物、BOM 编码漂移与 LF 行尾再次进入仓库。
/// 行尾门禁覆盖共享扩展名清单（源码、文档、工程、解决方案与脚本），构建期另有同名校验。
/// </summary>
public class CommentHygieneTests
{
    /// <summary>扫描 src 与 tests 的 C# 注释，命中残句立即失败。</summary>
    [SourceTreeFact]
    public void SourceAndTests_HaveNoTruncatedCommentColons()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string[] patterns =
        [
            "// /" + "：",
            "//" + "：",
            "/// " + "：",
            "<summary>" + "：",
            "<summary> /" + "：",
            "<para>" + "：",
            "（的 " + "pak",
        ];

        List<string> offenders = [];
        foreach (string directory in new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") })
        {
            foreach (string file in SourceTreeLocator.EnumerateSourceFiles(directory))
            {
                string relative = Path.GetRelativePath(root, file);
                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (patterns.Any(pattern => line.Contains(pattern, StringComparison.Ordinal)))
                    {
                        offenders.Add($"{relative}:{lineNumber}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "发现残句/损坏注释，请清理后重试：" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(20)));
    }

    /// <summary>
    /// 每个 .cs 与 .xaml 文件开头 64 字节内必须恰好带一个 UTF-8 BOM，
    /// 且位于第一个字节；正文中的 BOM 字节序列不在本门禁范围内（避免整文件读入）。
    /// </summary>
    [SourceTreeFact]
    public void SourceFiles_HaveExactlyOneBom()
    {
        string root = SourceTreeLocator.RootOrThrow();
        List<string> missing = [];
        List<string> duplicated = [];
        foreach (string directory in new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") })
        {
            foreach (string file in SourceTreeLocator.EnumerateSourceAndXamlFiles(directory))
            {
                // BOM 判定只看文件头 64 字节，避免整文件读入。
                byte[] head = ReadHead(file, 64);
                bool hasBomAtStart = head.Length >= 3
                    && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                int bomCount = CountBomSequences(head);
                if (!hasBomAtStart)
                {
                    missing.Add(Path.GetRelativePath(root, file));
                }
                else if (bomCount > 1)
                {
                    // 不只看相邻双 BOM；BOM + 空白 + BOM 这类中间重复也要拦截。
                    duplicated.Add(Path.GetRelativePath(root, file) + $"（检测到 {bomCount} 个 BOM）");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "以下文件缺少 UTF-8 BOM，与 .editorconfig 的 charset = utf-8-bom 不一致："
                + Environment.NewLine + string.Join(Environment.NewLine, missing.Take(20)));
        Assert.True(
            duplicated.Count == 0,
            "以下文件带重复 UTF-8 BOM，请只保留开头一个：" + Environment.NewLine + string.Join(Environment.NewLine, duplicated));
    }

    /// <summary>BOM 与空白分隔的重复 BOM 也要被统计到。</summary>
    [Fact]
    public void CountBomSequences_DetectsSeparatedDuplicateBom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, 0x20, 0xEF, 0xBB, 0xBF, 0x78];

        Assert.Equal(2, CountBomSequences(bytes));
    }

    /// <summary>
    /// 每个 .cs 与 .xaml 必须以换行结尾；
    /// 判定与混合行尾门禁共用 <see cref="SourceLineEndingChecks"/>。
    /// </summary>
    /// <remarks>
    /// 本用例与 CRLF 门禁各读一次文件（约 3.4MB、数十毫秒），
    /// 相比测试套件整体开销不是瓶颈，保留两条独立断言以便失败信息更具体。
    /// </remarks>
    [SourceTreeFact]
    public void SourceFiles_EndWithNewline()
    {
        string root = SourceTreeLocator.RootOrThrow();
        List<string> offenders = [];
        foreach (string directory in new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") })
        {
            foreach (string file in SourceTreeLocator.EnumerateSourceAndXamlFiles(directory))
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (!SourceLineEndingChecks.EndsWithNewline(bytes))
                {
                    offenders.Add(
                        Path.GetRelativePath(root, file) + (bytes.Length == 0 ? "（空文件）" : string.Empty));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下文件缺少尾随换行：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 逐行检查能发现「纯 LF」与「LF 文件末尾追加 CRLF」的混合行尾，
    /// 不再因只看最后一个字节而漏检。
    /// </summary>
    [Fact]
    public void SourceLineEndingChecks_DetectsMixedLineEndings()
    {
        Assert.Equal(-1, SourceLineEndingChecks.FindFirstNonCrlfLineEnding("a\r\nb\r\n"u8));
        Assert.True(SourceLineEndingChecks.FindFirstNonCrlfLineEnding("a\r\nb\n"u8) >= 0);
        Assert.True(SourceLineEndingChecks.FindFirstNonCrlfLineEnding("a\nb\r\n"u8) >= 0);
        Assert.True(SourceLineEndingChecks.FindFirstNonCrlfLineEnding("a\rb"u8) >= 0);

        Assert.True(SourceLineEndingChecks.EndsWithNewline("a\r\n"u8));
        Assert.False(SourceLineEndingChecks.EndsWithNewline("a"u8));
    }

    /// <summary>
    /// 真实写入临时文件后再检查，证明门禁入口（<see cref="SourceLineEndingChecks.Inspect"/>）
    /// 能拦截混合行尾、尾随空行与连续空行，而不是只验证纯函数。
    /// </summary>
    [Fact]
    public void SourceLineEndingChecks_InspectsRealFiles()
    {
        using TempDir temp = TempDir.Create();
        string mixed = temp.File("mixed.txt", "a\r\nb\nc\r\n");
        string crlf = temp.File("crlf.txt", "a\r\nb\r\n");
        string trailing = temp.File("trailing.txt", "a\r\n\r\n");
        string consecutive = temp.File("consecutive.txt", "a\r\n\r\n\r\nb\r\n");

        SourceLineEndingChecks.LineEndingIssues mixedIssues = SourceLineEndingChecks.Inspect(mixed);
        Assert.True(mixedIssues.Mixed);
        Assert.False(mixedIssues.TrailingBlank);
        Assert.False(mixedIssues.ConsecutiveBlank);

        SourceLineEndingChecks.LineEndingIssues crlfIssues = SourceLineEndingChecks.Inspect(crlf);
        Assert.False(crlfIssues.Mixed);
        Assert.False(crlfIssues.TrailingBlank);
        Assert.False(crlfIssues.ConsecutiveBlank);

        Assert.True(SourceLineEndingChecks.Inspect(trailing).TrailingBlank);
        Assert.True(SourceLineEndingChecks.Inspect(consecutive).ConsecutiveBlank);
    }

    /// <summary>
    /// 门禁枚举确实同时覆盖 `.cs` 与 `.xaml`，
    /// 普通源码枚举不含 `.xaml`；剪枝规则对 `.codegraph`/`obj` 返回真、对源码目录返回假。
    /// </summary>
    [SourceTreeFact]
    public void SourceTreeLocator_SourceAndXamlEnumeration_CoversBothExtensions()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string sourceRoot = Path.Combine(root, "src");
        string[] gateFiles = SourceTreeLocator.EnumerateSourceAndXamlFiles(sourceRoot);
        string[] sourceFiles = SourceTreeLocator.EnumerateSourceFiles(sourceRoot);

        Assert.Contains(gateFiles, file => file.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sourceFiles, file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
        Assert.True(gateFiles.Length > sourceFiles.Length, "C#/XAML 门禁枚举应包含源码枚举之外的文件");

        Assert.True(SourceTreeLocator.IsPrunedDirectory(".codegraph"));
        Assert.True(SourceTreeLocator.IsPrunedDirectory("obj"));
        Assert.True(SourceTreeLocator.IsPrunedDirectory("bin"));
        Assert.False(SourceTreeLocator.IsPrunedDirectory("src"));
        Assert.DoesNotContain(
            SourceTreeLocator.EnumerateRepositoryFiles(),
            file => Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar)
                .Contains(".codegraph", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>每个 .cs 与 .xaml 必须全部使用 CRLF，且末尾只有一个换行（不得有尾随空行）。</summary>
    /// <remarks>正文中也不允许连续两个空行：判定与注入用例共用 `Inspect`。</remarks>
    [SourceTreeFact]
    public void SourceFiles_UseCrlfAndHaveNoTrailingBlankLine()
    {
        string root = SourceTreeLocator.RootOrThrow();
        List<string> mixed = [];
        List<string> trailingBlank = [];
        List<string> consecutiveBlank = [];
        foreach (string directory in new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") })
        {
            foreach (string file in SourceTreeLocator.EnumerateSourceAndXamlFiles(directory))
            {
                SourceLineEndingChecks.LineEndingIssues issues = SourceLineEndingChecks.Inspect(file);
                if (issues.Mixed)
                {
                    mixed.Add(Path.GetRelativePath(root, file));
                }

                if (issues.TrailingBlank)
                {
                    trailingBlank.Add(Path.GetRelativePath(root, file));
                }

                if (issues.ConsecutiveBlank)
                {
                    consecutiveBlank.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(
            mixed.Count == 0,
            "以下文件混用了 CRLF/LF（返工请按 CRLF 保存）：" + Environment.NewLine + string.Join(Environment.NewLine, mixed));
        Assert.True(
            trailingBlank.Count == 0,
            "以下文件末尾有多余空行：" + Environment.NewLine + string.Join(Environment.NewLine, trailingBlank));
        Assert.True(
            consecutiveBlank.Count == 0,
            "以下文件正文存在连续空行：" + Environment.NewLine + string.Join(Environment.NewLine, consecutiveBlank));
    }

    /// <summary>
    /// 声明范围内的全部文本文件必须使用 CRLF；
    /// 扩展名与编码门禁共用 <see cref="TextFileExtensions"/>，空清单直接失败。
    /// </summary>
    /// <remarks>
    /// `.gitattributes` 的 `eol=crlf` 只影响新检出，工作区被编辑器写成 LF 时 git 不显示差异，
    /// 因此由本门禁直接扫描工作区；`.cs` 与 `.xaml` 也包含在共享清单内。
    /// 工作区扫描为数十毫秒（文件数随仓库增长，不写死快照）；
    /// 构建期同一脚本本机实测约 1.6 秒，主要是 PowerShell 启动开销（见 Directory.Build.targets），
    /// 不属于测试热点，按设计取舍接受。
    /// </remarks>
    [SourceTreeFact]
    public void DeclaredTextFiles_UseCrlfLineEndings()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string[] extensions = TextFileExtensions.Load(root);

        // 清单为空说明门禁配置被破坏，必须失败而不是「0 个违规」通过。
        Assert.NotEmpty(extensions);
        List<string> mixed = SourceLineEndingChecks.FindNonCrlfFiles(
            SourceTreeLocator.EnumerateRepositoryFiles(),
            extensions);
        Assert.True(
            mixed.Count == 0,
            "以下文件未使用 CRLF（.gitattributes 只影响新检出，请本地按 CRLF 保存）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, mixed.Select(file => Path.GetRelativePath(root, file))));
    }

    /// <summary>注入纯 LF 的 `.md`/`.csproj` 后门禁必须拦截，证明判定可证伪。</summary>
    [Fact]
    public void DeclaredTextFiles_Gate_DetectsInjectedLfFiles()
    {
        using TempDir temp = TempDir.Create();
        string badMarkdown = Path.Combine(temp.Path, "bad.md");
        string badProject = Path.Combine(temp.Path, "bad.csproj");
        string goodMarkdown = Path.Combine(temp.Path, "good.md");
        File.WriteAllText(badMarkdown, "line1\nline2");
        File.WriteAllText(badProject, "line1\r\nline2\n");
        File.WriteAllText(goodMarkdown, "line1\r\nline2\r\n");

        List<string> offenders = SourceLineEndingChecks.FindNonCrlfFiles(
            Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories),
            [".md", ".csproj"]);

        Assert.Contains(badMarkdown, offenders);
        Assert.Contains(badProject, offenders);
        Assert.DoesNotContain(goodMarkdown, offenders);
    }

    /// <summary>扩展名清单为空或缺失时直接失败，避免门禁静默通过。</summary>
    [Fact]
    public void DeclaredTextExtensions_EmptyOrMissingList_Throws()
    {
        using TempDir temp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(temp.Path, "tools"));
        File.WriteAllText(Path.Combine(temp.Path, TextFileExtensions.RelativePath), string.Empty);
        Assert.Throws<InvalidOperationException>(() => TextFileExtensions.Load(temp.Path));
        Assert.Throws<InvalidOperationException>(
            () => TextFileExtensions.Load(Path.Combine(temp.Path, "missing")));
    }

    /// <summary>`.gitattributes` 自身必须为纯 CRLF，并显式声明 `eol=crlf`。</summary>
    [SourceTreeFact]
    public void GitAttributes_UsesCrlfAndDeclaresItself()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string path = Path.Combine(root, ".gitattributes");
        Assert.False(SourceLineEndingChecks.Inspect(path).Mixed);
        string[] lines = File.ReadAllLines(path);
        Assert.Contains(
            lines,
            line => line.TrimStart().StartsWith(".gitattributes ", StringComparison.OrdinalIgnoreCase)
                && line.Contains("eol=crlf", StringComparison.OrdinalIgnoreCase));

        // 共享清单是 .txt，属性文件必须显式覆盖该扩展名。
        Assert.Contains(
            lines,
            line => line.TrimStart().StartsWith("*.txt ", StringComparison.OrdinalIgnoreCase)
                && line.Contains("eol=crlf", StringComparison.OrdinalIgnoreCase));

        // 按「最后匹配生效」求最终 eol；末尾追加覆盖规则后用例必须失败。
        Assert.Equal("lf", EvaluateGitAttributeEol(lines, "global.json"));
        Assert.Equal("crlf", EvaluateGitAttributeEol(lines, "src/HunterBox.App/app.manifest"));
    }

    /// <summary>`.gitattributes` 必须为共享清单中的每个扩展名声明 `eol=crlf`。</summary>
    [SourceTreeFact]
    public void GitAttributes_DeclaresCrlfForEveryDeclaredExtension()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string[] lines = File.ReadAllLines(Path.Combine(root, ".gitattributes"));
        foreach (string extension in TextFileExtensions.Load(root))
        {
            string pattern = "*" + extension;
            Assert.Contains(
                lines,
                line => line.TrimStart().StartsWith(pattern + " ", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("eol=crlf", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>按 gitattributes 的「最后匹配生效」求指定路径的 eol 最终值。</summary>
    /// <param name="lines">属性文件全部行。</param>
    /// <param name="relativePath">相对仓库根的路径（正斜杠）。</param>
    /// <returns>最终 eol 值；没有匹配规则时返回 <see langword="null"/>。</returns>
    private static string? EvaluateGitAttributeEol(IReadOnlyList<string> lines, string relativePath)
    {
        string? eol = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !EditorConfigEvaluator.SectionMatches(tokens[0], relativePath))
            {
                continue;
            }

            foreach (string token in tokens.Skip(1))
            {
                if (token.StartsWith("eol=", StringComparison.OrdinalIgnoreCase))
                {
                    eol = token[4..];
                }
            }
        }

        return eol;
    }

    /// <summary>读取文件开头若干字节（BOM/换行门禁只读文件头/尾）。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="maxBytes">最多读取的字节数。</param>
    /// <returns>实际读到的字节。</returns>
    private static byte[] ReadHead(string path, int maxBytes)
    {
        byte[] buffer = new byte[maxBytes];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return buffer[..read];
    }

    /// <summary>统计文件开头 64 字节内出现的 UTF-8 BOM 序列数量。</summary>
    /// <param name="bytes">文件字节。</param>
    /// <returns>BOM 序列数量。</returns>
    private static int CountBomSequences(byte[] bytes)
    {
        const int scanLimit = 64;
        int count = 0;
        int limit = Math.Min(bytes.Length - 3, scanLimit);
        for (int i = 0; i <= limit; i++)
        {
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
            {
                count++;
                i += 2;
            }
        }

        return count;
    }
}
