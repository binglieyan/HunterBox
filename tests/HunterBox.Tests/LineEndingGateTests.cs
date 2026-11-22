using System.Text.RegularExpressions;

namespace HunterBox.Tests;

/// <summary>
/// 构建期行尾门禁的接线、共享清单与枚举边界。
/// </summary>
/// <remarks>
/// 脚本行为用例在 <see cref="LineEndingScriptTests"/>：拆成两个测试类后 xUnit 可跨类并行，
/// 避免门禁进程启动串行累加墙钟。
/// </remarks>
public class LineEndingGateTests
{
    /// <summary>构建目标必须存在、锚定 Domain 并在 Build 前调用校验脚本；pre-commit 钩子也要接线。</summary>
    /// <remarks>构建期与提交前路径不得携带 `-Fix`/`-Extensions`，否则门禁会退化为自动改写或任意范围。</remarks>
    [SourceTreeFact]
    public void DirectoryBuildTargets_DeclaresLineEndingGate()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string targets = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
        Assert.Contains("VerifyTextFileLineEndings", targets, StringComparison.Ordinal);
        Assert.Contains("BeforeTargets=\"Build\"", targets, StringComparison.Ordinal);
        Assert.Contains("HunterBox.Domain", targets, StringComparison.Ordinal);
        Assert.Contains("verify-line-endings.ps1", targets, StringComparison.Ordinal);

        string hook = File.ReadAllText(Path.Combine(root, ".githooks", "pre-commit"));
        Assert.Contains("verify-line-endings.ps1", hook, StringComparison.Ordinal);

        // 命令提取与禁用开关判定抽到 CommandLineGuard，便于独立证伪。
        List<string> targetCommands = [];
        foreach (string targetsPath in SourceTreeLocator.EnumerateRepositoryFiles()
            .Where(file => string.Equals(Path.GetExtension(file), ".targets", StringComparison.OrdinalIgnoreCase)))
        {
            targetCommands.AddRange(CommandLineGuard.ExtractTargetCommands(File.ReadAllText(targetsPath)));
        }

        Assert.NotEmpty(targetCommands);
        foreach (string command in targetCommands)
        {
            Assert.False(CommandLineGuard.HasForbiddenSwitch(command), $"构建路径不得携带禁用开关：{command}");
        }

        List<string> hookCommands = [.. CommandLineGuard.ExtractHookCommands(hook)];
        Assert.NotEmpty(hookCommands);
        foreach (string command in hookCommands)
        {
            Assert.False(CommandLineGuard.HasForbiddenSwitch(command), $"提交路径不得携带禁用开关：{command}");
        }
    }

    /// <summary>单/双引号、XML 注释与子元素都必须被正确提取。</summary>
    [Fact]
    public void CommandLineGuard_ExtractsQuotesCommentsAndElements()
    {
        const string Targets = """
            <Project>
              <!-- <Exec Command="powershell -Fix" /> -->
              <Exec Command="powershell -NoProfile -File verify.ps1" />
              <Exec Command='powershell -NoProfile -Fi' />
              <Target Name="x">
                <Command>powershell -NoProfile -Ext</Command>
              </Target>
            </Project>
            """;

        IReadOnlyList<string> commands = CommandLineGuard.ExtractTargetCommands(Targets);

        Assert.Equal(3, commands.Count);
        Assert.DoesNotContain(commands, command => command.Contains("-Fix", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("-Fi", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("-Ext", StringComparison.Ordinal));
    }

    /// <summary>hook 续行拼接后检查，注释行不参与。</summary>
    [Fact]
    public void CommandLineGuard_JoinsHookContinuationAndSkipsComments()
    {
        const string Hook = """
            # exec powershell -Fix
            exec powershell -NoProfile \
              -File verify-line-endings.ps1 -Root /repo
            """;

        IReadOnlyList<string> commands = CommandLineGuard.ExtractHookCommands(Hook);

        Assert.Single(commands);
        Assert.Contains("verify-line-endings.ps1", commands[0], StringComparison.Ordinal);
        Assert.False(CommandLineGuard.HasForbiddenSwitch(commands[0]));
    }

    /// <summary>`-F`/`-Fi`/`-Fix` 与 `-E`/`-Ex`/`-Ext`/`-Extensions` 都算禁用开关。</summary>
    [Theory]
    [InlineData("powershell -F", true)]
    [InlineData("powershell -Fi", true)]
    [InlineData("powershell -Fix", true)]
    [InlineData("powershell -E", true)]
    [InlineData("powershell -Ex", true)]
    [InlineData("powershell -Ext", true)]
    [InlineData("powershell -Extensions", true)]
    [InlineData("powershell -File x.ps1 -ExecutionPolicy Bypass", false)]
    public void CommandLineGuard_DetectsSwitchPrefixes(string command, bool forbidden) =>
        Assert.Equal(forbidden, CommandLineGuard.HasForbiddenSwitch(command));

    /// <summary>
    /// 执行一次真实 MSBuild 目标做行为断言。注入 LF 时必须失败，
    /// 且错误里必须出现 `bad.md`；这样删除目标、改写 Condition、添加 ContinueOnError
    /// 或把 -Root 指向空目录都会让本用例失败。
    /// </summary>
    [SourceTreeFact]
    public void DirectoryBuildTargets_GateFailsOnLfWithRealTarget()
    {
        using TempDir temp = TempDir.Create();
        LineEndingGateRunner.CopyGateFixture(temp.Path);
        File.WriteAllText(
            Path.Combine(temp.Path, "HunterBox.Domain.csproj"),
            "<Project><Import Project=\"Directory.Build.targets\" /></Project>");
        _ = temp.File("bad.md", "line1\nline2");

        (int exitCode, string output, string error) =
            LineEndingGateRunner.RunMsBuildTarget(temp.Path);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("bad.md", output + error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>文件链接也必须被测试枚举跳过，不能读取链接目标内容。</summary>
    [FileSymlinkFact]
    public void SourceTreeLocator_SkipsFileLinks()
    {
        using TempDir temp = TempDir.Create();
        string target = temp.File("target.cs", "class Target { }");
        string link = Path.Combine(temp.Path, "link.cs");
        Assert.True(
            LinkTestSupport.TryCreateFileLink(link, target),
            "无法创建文件链接，用例前置条件不成立");
        Assert.True(SourceTreeLocator.IsReparseLink(link));

        string[] files = [.. SourceTreeLocator.EnumerateFilesPruned(temp.Path)];

        Assert.Contains(target, files);
        Assert.DoesNotContain(link, files);
    }

    /// <summary>
    /// 测试侧枚举必须跳过链接并应用共享剪枝清单，
    /// 否则会读取仓库外文件或把已剪枝目录重新纳入扫描。
    /// </summary>
    [SourceTreeDirectoryLinkFact]
    public void SourceTreeLocator_PrunesSharedDirectoriesAndSkipsLinks()
    {
        using TempDir temp = TempDir.Create();
        string good = temp.File("good.cs", "class Good { }");
        _ = temp.File("node_modules/bad.cs", "class Bad { }");
        string external = Path.Combine(
            Path.GetTempPath(),
            "hunterbox-link-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(external);
        string externalFile = Path.Combine(external, "evil.cs");
        File.WriteAllText(externalFile, "class Evil { }");
        string link = Path.Combine(temp.Path, "linked");
        try
        {
            Assert.True(
                LinkTestSupport.TryCreateDirectoryLink(link, external),
                "无法创建目录链接，用例前置条件不成立");
            Assert.True(SourceTreeLocator.IsReparseLink(link));
            Assert.False(SourceTreeLocator.IsReparseLink(good));

            string[] files = [.. SourceTreeLocator.EnumerateFilesPruned(temp.Path)];

            Assert.Single(files);
            Assert.True(string.Equals(good, files[0], StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                files,
                file => file.StartsWith(link + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(external))
            {
                Directory.Delete(external, recursive: true);
            }
        }
    }

    /// <summary>共享扩展名清单自身也必须带 UTF-8 BOM 与 CRLF。</summary>
    [SourceTreeFact]
    public void TextFileExtensionsList_UsesBomAndCrlf()
    {
        string root = SourceTreeLocator.RootOrThrow();
        LineEndingGateRunner.AssertBomAndCrlf(Path.Combine(root, TextFileExtensions.RelativePath));
    }

    /// <summary>共享剪枝清单自身也必须带 UTF-8 BOM 与 CRLF。</summary>
    [SourceTreeFact]
    public void GatePrunedDirectoriesList_UsesBomAndCrlf()
    {
        string root = SourceTreeLocator.RootOrThrow();
        LineEndingGateRunner.AssertBomAndCrlf(
            Path.Combine(root, SourceTreeLocator.PrunedListRelativePath));
    }

    /// <summary>
    /// 脚本必须从 <c>$PSScriptRoot</c> 读取共享剪枝清单，
    /// 且清单覆盖关键构建产物目录——这两点是「两侧同源」的可证伪前提，
    /// 不再用「同一份文件解析两次后比较」的自比较断言。
    /// </summary>
    [SourceTreeFact]
    public void GatePruneList_IsReadByScriptFromSharedFile()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string script = File.ReadAllText(Path.Combine(root, "tools", "verify-line-endings.ps1"));
        string listName = Path.GetFileName(SourceTreeLocator.PrunedListRelativePath);

        Assert.Contains($"Join-Path $PSScriptRoot '{listName}'", script, StringComparison.Ordinal);

        HashSet<string> expected = SourceTreeLocator.LoadPrunedNames(root);
        foreach (string required in new[] { "bin", "obj", ".git", "node_modules", "dist" })
        {
            Assert.Contains(required, expected, StringComparer.OrdinalIgnoreCase);
            Assert.True(
                SourceTreeLocator.IsPrunedDirectory(required),
                $"测试枚举未剪枝 {required}，说明清单未被实际加载");
        }

        Assert.False(SourceTreeLocator.IsPrunedDirectory("src"));
    }

    /// <summary>
    /// 脚本的链接/非链接标签白名单必须与 Windows 官方标签一致。
    /// 名称代理标签（mount point/symlink/global reparse/LX symlink）只允许出现在链接集合里；
    /// 任一常量被挪回非链接白名单时本用例失败。
    /// </summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_TagWhitelistMatchesWindowsTags()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string script = File.ReadAllText(Path.Combine(root, "tools", "verify-line-endings.ps1"));

        string[] linkTags = ExtractScriptArray(script, "LinkTags");
        string[] plainPatterns = ExtractScriptArray(script, "PlainTagPatterns");

        foreach (string tag in new[] { "a0000003", "a000000c", "a0000019", "a000001d" })
        {
            Assert.Contains(tag, linkTags, StringComparer.OrdinalIgnoreCase);
        }

        foreach (string pattern in new[] { "80000017", "80000018", "8000001b", "8000001e" })
        {
            Assert.Contains(
                plainPatterns,
                item => item.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        // a0000019 是名称代理标签，绝不能出现在非链接白名单里（否则 fail-open）。
        Assert.DoesNotContain(
            plainPatterns,
            item => item.Contains("a0000019", StringComparison.OrdinalIgnoreCase));

        // CLOUD 家族正则必须覆盖 CLOUD..CLOUD_F（如 9000001a、9000f01a），
        // 但不得放行未核实的新标签（如 90000027），否则未知云标签会被误判为可校验。
        string[] cloudPatterns =
            [.. plainPatterns.Where(item => item.StartsWith("^9000", StringComparison.Ordinal))];
        Assert.NotEmpty(cloudPatterns);
        Assert.Contains(cloudPatterns, pattern => Regex.IsMatch("9000001a", pattern));
        Assert.Contains(cloudPatterns, pattern => Regex.IsMatch("9000f01a", pattern));
        Assert.DoesNotContain(cloudPatterns, pattern => Regex.IsMatch("90000027", pattern));
    }

    /// <summary>
    /// `.editorconfig` 的全局段必须在编辑期强制 CRLF + UTF-8 BOM，
    /// 覆盖声明扩展名；例外段只允许文档化的无 BOM/LF 文件，并用真实路径求值验证最终生效值。
    /// </summary>
    [SourceTreeFact]
    public void EditorConfig_DeclaredExtensions_EnforceCrlfAndBom()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string[] lines = File.ReadAllLines(Path.Combine(root, ".editorconfig"));

        Dictionary<string, string> global = EditorConfigEvaluator.ReadSection(lines, "[*]");
        Assert.Equal("crlf", global.GetValueOrDefault("end_of_line"));
        Assert.Equal("utf-8-bom", global.GetValueOrDefault("charset"));
        Assert.Equal("true", global.GetValueOrDefault("insert_final_newline"));

        HashSet<string> expectedOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            "[*.json]", "[.editorconfig]", "[.gitattributes]", "[.gitignore]", "[app.manifest]",
            "[.githooks/*]",
        };
        List<string> actualOverrides = [];
        foreach (string section in lines
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith('[') && line.EndsWith(']')))
        {
            if (string.Equals(section, "[*]", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, string> values = EditorConfigEvaluator.ReadSection(lines, section);
            bool charsetOverridden = values.TryGetValue("charset", out string? charset)
                && !string.Equals(charset, "utf-8-bom", StringComparison.OrdinalIgnoreCase);
            bool lineEndingOverridden = values.TryGetValue("end_of_line", out string? lineEnding)
                && !string.Equals(lineEnding, "crlf", StringComparison.OrdinalIgnoreCase);
            if (charsetOverridden || lineEndingOverridden)
            {
                actualOverrides.Add(section);
            }
        }

        Assert.Equal(
            expectedOverrides.Order(StringComparer.OrdinalIgnoreCase),
            actualOverrides.Order(StringComparer.OrdinalIgnoreCase));

        foreach (string section in actualOverrides)
        {
            string glob = section[1..^1];
            string suffix = glob.StartsWith("*.", StringComparison.Ordinal) ? glob[1..] : glob;
            Assert.DoesNotContain(
                TextFileExtensions.Load(root),
                extension => string.Equals(extension, suffix, StringComparison.OrdinalIgnoreCase));
        }

        // 按 glob 对真实路径求最终生效值，防止「段存在但语义不足」（例如 .json 仍继承 CRLF）。
        string editorConfig = string.Join("\r\n", lines);
        AssertEditorConfigValue(editorConfig, "global.json", "charset", "utf-8");
        AssertEditorConfigValue(editorConfig, "global.json", "end_of_line", "lf");
        AssertEditorConfigValue(editorConfig, ".githooks/pre-commit", "charset", "utf-8");
        AssertEditorConfigValue(editorConfig, ".githooks/pre-commit", "end_of_line", "lf");
        AssertEditorConfigValue(editorConfig, "CHANGELOG.md", "charset", "utf-8-bom");
        AssertEditorConfigValue(editorConfig, "CHANGELOG.md", "end_of_line", "crlf");
        // app.manifest 保持无 BOM，行尾沿用 [*] 的 CRLF。
        AssertEditorConfigValue(editorConfig, "src/HunterBox.App/app.manifest", "charset", "utf-8");
        AssertEditorConfigValue(editorConfig, "src/HunterBox.App/app.manifest", "end_of_line", "crlf");

        // 只锁 charset 不够，三条例外路径的行尾也必须锁定为 CRLF。
        AssertEditorConfigValue(editorConfig, ".editorconfig", "end_of_line", "crlf");
        AssertEditorConfigValue(editorConfig, ".gitattributes", "end_of_line", "crlf");
        AssertEditorConfigValue(editorConfig, ".gitignore", "end_of_line", "crlf");

        // 真实文件现状与配置一致：.json 与 Git 钩子无 BOM + LF，app.manifest 无 BOM。
        AssertFileHasNoBomAndNoCarriageReturn(Path.Combine(root, "global.json"));
        AssertFileHasNoBomAndNoCarriageReturn(Path.Combine(root, ".githooks", "pre-commit"));
        Assert.False(
            File.ReadAllBytes(Path.Combine(root, "src", "HunterBox.App", "app.manifest"))
                is [0xEF, 0xBB, 0xBF, ..],
            "app.manifest 必须保持无 BOM");

        // 例外文件自身也必须实际为无 BOM + CRLF，不能只依赖 EditorConfig 求值。
        AssertFileHasNoBomAndCrlf(Path.Combine(root, ".editorconfig"));
        AssertFileHasNoBomAndCrlf(Path.Combine(root, ".gitignore"));
    }

    /// <summary>断言文件无 UTF-8 BOM、全文为纯 CRLF 且以换行结尾。</summary>
    /// <param name="path">文件绝对路径。</param>
    private static void AssertFileHasNoBomAndCrlf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], $"{path} 必须保持无 BOM");
        Assert.False(SourceLineEndingChecks.Inspect(path).Mixed, $"{path} 必须保持 CRLF");
        Assert.True(
            bytes.Length >= 2 && bytes[^2] == 0x0D && bytes[^1] == 0x0A,
            $"{path} 必须以 CRLF 结尾");
    }

    /// <summary>断言文件无 UTF-8 BOM 且不含 CR（纯 LF）。</summary>
    /// <param name="path">文件绝对路径。</param>
    private static void AssertFileHasNoBomAndNoCarriageReturn(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], $"{path} 必须保持无 BOM");
        Assert.True(Array.IndexOf(bytes, (byte)'\r') < 0, $"{path} 必须保持 LF");
    }

    /// <summary>断言指定仓库路径在 `.editorconfig` 中的最终生效值。</summary>
    /// <param name="text">`.editorconfig` 全文。</param>
    /// <param name="path">相对仓库根的路径（正斜杠）。</param>
    /// <param name="key">配置键。</param>
    /// <param name="expected">期望值。</param>
    private static void AssertEditorConfigValue(string text, string path, string key, string expected)
    {
        Dictionary<string, string> effective = EditorConfigEvaluator.Evaluate(text, path);
        Assert.True(effective.TryGetValue(key, out string? actual), $"{path} 缺少 {key}");
        Assert.Equal(expected, actual);
    }

    /// <summary>从脚本里提取 `$script:名字 = @(...)` 的字符串项。</summary>
    /// <param name="script">脚本全文。</param>
    /// <param name="name">数组变量名。</param>
    /// <returns>字符串项数组。</returns>
    private static string[] ExtractScriptArray(string script, string name)
    {
        Match match = Regex.Match(
            script,
            @"\$script:" + name + @"\s*=\s*@\((?<body>[^)]*)\)",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"脚本中找不到 $script:{name} 数组");
        return
        [
            .. Regex.Matches(match.Groups["body"].Value, "'([^']+)'")
                .Select(item => item.Groups[1].Value),
        ];
    }
}
