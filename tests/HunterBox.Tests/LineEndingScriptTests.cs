using System.Text;

namespace HunterBox.Tests;

/// <summary>
/// 行尾校验脚本的退出码与链接/剪枝行为。
/// </summary>
/// <remarks>
/// 与 <see cref="LineEndingGateTests"/> 分成两个测试类，让 xUnit 把脚本用例与 msbuild 接线用例
/// 并行执行，避免多次 PowerShell 冷启动串行累加墙钟。
/// </remarks>
public class LineEndingScriptTests
{
    /// <summary>注入 LF 的 `.md` 必须让脚本以非零退出；改成 CRLF 后恢复通过。</summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_FailsOnLfAndPassesOnCrlf()
    {
        using TempDir temp = TempDir.Create();
        string file = Path.Combine(temp.Path, "bad.md");
        File.WriteAllText(file, "line1\nline2");
        (int exitCode, _, string error) = LineEndingGateRunner.RunScript(temp.Path, ".md");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("bad.md", error, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(file, "line1\r\nline2\r\n");
        (exitCode, _, _) = LineEndingGateRunner.RunScript(temp.Path, ".md");
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// `-Fix` 只调整行尾并补齐末尾换行，其它正文逐字保留（含中文），
    /// 重写为 CRLF + 单个 UTF-8 BOM；修复后常规校验退出 0。
    /// </summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_Fix_LfFileBecomesCrlfAndBom()
    {
        using TempDir temp = TempDir.Create();
        string lf = temp.File("bad.md", "line1\nline2");
        string bareCr = Path.Combine(temp.Path, "bare-cr.md");
        File.WriteAllBytes(
            bareCr,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("中文\r行2\r")]);
        string doubleBom = Path.Combine(temp.Path, "double-bom.md");
        File.WriteAllBytes(doubleBom, [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x61, 0x0A]);

        (int exitCode, string output, _) =
            LineEndingGateRunner.RunScript(temp.Path, ".md", fix: true);

        Assert.Equal(0, exitCode);
        Assert.Contains("行尾修复完成", output, StringComparison.Ordinal);
        // 计数只在最终摘要出现一次，前段只列文件名。
        Assert.Contains("已修复的行尾违规文件", output, StringComparison.Ordinal);
        Assert.Equal(1, output.Split("行尾修复完成", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("已修复 3 个", output, StringComparison.Ordinal);
        LineEndingGateRunner.AssertBomAndCrlf(lf);
        Assert.Equal("line1\r\nline2\r\n", File.ReadAllText(lf));
        LineEndingGateRunner.AssertBomAndCrlf(bareCr);
        Assert.Equal("中文\r\n行2\r\n", File.ReadAllText(bareCr));
        Assert.False(
            File.ReadAllBytes(bareCr) is [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, ..],
            "修复后只能保留一个 UTF-8 BOM");
        // 连续 BOM 必须全部剥离，不得把第二个 BOM 写进正文。
        Assert.Equal([0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A], File.ReadAllBytes(doubleBom));

        (exitCode, _, _) = LineEndingGateRunner.RunScript(temp.Path, ".md");
        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// `-Fix` 必须拒绝非 UTF-8 内容（GBK）、UTF-16/UTF-32 BOM 以及含 NUL 的
    /// 无 BOM UTF-16/UTF-32：退出码 1、错误输出含文件名与原因、文件字节保持不变。
    /// </summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_Fix_NonUtf8FileIsRejected()
    {
        using TempDir temp = TempDir.Create();
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            // “中文”的 GBK 字节加裸 LF；硬编码字节，不依赖本机代码页。
            ["gbk.md"] = [0xD6, 0xD0, 0xCE, 0xC4, 0x0A],
            // UTF-16 LE BOM + “a\r\n”。
            ["utf16-bom.md"] = [0xFF, 0xFE, 0x61, 0x00, 0x0D, 0x00, 0x0A, 0x00],
            // 无 BOM 的 UTF-16 LE / UTF-32 LE：ASCII 编码含 0x00，严格 UTF-8 不会报错，必须按 NUL 拒绝。
            ["utf16-nobom.md"] = [0x61, 0x00, 0x0D, 0x00, 0x0A, 0x00],
            ["utf32-nobom.md"] = [0x61, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00],
            // UTF-32 LE BOM：必须优先按 UTF-32 判定，不能被当成 UTF-16 BOM。
            ["utf32-bom.md"] = [0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00],
        };
        foreach ((string name, byte[] content) in files)
        {
            File.WriteAllBytes(Path.Combine(temp.Path, name), content);
        }

        (int exitCode, _, string error) =
            LineEndingGateRunner.RunScript(temp.Path, ".md", fix: true);

        Assert.NotEqual(0, exitCode);
        foreach ((string name, byte[] content) in files)
        {
            Assert.Contains(name, error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(temp.Path, name)));
        }

        Assert.Contains("UTF-16 BOM", error, StringComparison.Ordinal);
        Assert.Contains("UTF-32 BOM", error, StringComparison.Ordinal);
        Assert.Contains("NUL", error, StringComparison.Ordinal);
        Assert.Contains("编码不受支持", error, StringComparison.Ordinal);
    }

    /// <summary>只含 GBK 文件时必须归入「编码不受支持」，不能落入 IO 组。</summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_Fix_GbkOnly_IsReportedAsEncoding()
    {
        using TempDir temp = TempDir.Create();
        File.WriteAllBytes(Path.Combine(temp.Path, "gbk.md"), [0xD6, 0xD0, 0xCE, 0xC4, 0x0A]);

        (int exitCode, _, string error) =
            LineEndingGateRunner.RunScript(temp.Path, ".md", fix: true);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("编码不受支持", error, StringComparison.Ordinal);
        Assert.DoesNotContain("未能修复", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 只读文件修复失败必须归入「未能修复」而不是「编码不受支持」，
    /// 且保持原样、不残留临时文件。
    /// </summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_Fix_ReadOnlyFileReportsIoFailure()
    {
        using TempDir temp = TempDir.Create();
        string file = temp.File("readonly.md", "line1\nline2");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            (int exitCode, _, string error) =
                LineEndingGateRunner.RunScript(temp.Path, ".md", fix: true);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("readonly.md", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("未能修复", error, StringComparison.Ordinal);
            Assert.DoesNotContain("编码不受支持", error, StringComparison.Ordinal);
            Assert.Equal("line1\nline2", File.ReadAllText(file));
            Assert.Empty(Directory.GetFiles(temp.Path, "*.hbtmp", SearchOption.AllDirectories));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    /// <summary>单个文件被占用时记录中文原因并继续检查其它文件，不能中断整轮扫描。</summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_LockedFile_ReportsAndContinues()
    {
        using TempDir temp = TempDir.Create();
        string locked = temp.File("locked.md", "line1\r\n");
        _ = temp.File("bad.md", "line1\nline2");

        using (FileStream hold = new(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            (int exitCode, _, string error) =
                LineEndingGateRunner.RunScript(temp.Path, ".md");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("locked.md", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("无法读取", error, StringComparison.Ordinal);
            Assert.Contains("bad.md", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>没有扫描到任何声明文件时必须失败，避免范围配置错误被静默放过。</summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_NoFiles_Fails()
    {
        using TempDir temp = TempDir.Create();
        (int exitCode, _, string error) = LineEndingGateRunner.RunScript(temp.Path, ".md");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("未扫描到任何声明文件", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真实目录链接必须被识别并跳过，不能进入链接目标。
    /// 该用例同时覆盖 PS 5.1 下的链接判定（`LinkType` 或 `fsutil` 标签字符串比较），
    /// 判定回退成 fail-open 时会因回环/重复校验而失败。
    /// </summary>
    [SourceTreeDirectoryLinkFact]
    public void VerifyLineEndingsScript_SkipsDirectoryLinks()
    {
        using TempDir temp = TempDir.Create();
        _ = temp.File("good.md", "line1\r\nline2\r\n");
        Assert.True(
            LinkTestSupport.TryCreateDirectoryLink(Path.Combine(temp.Path, "loop"), temp.Path),
            "无法创建目录链接，用例前置条件不成立");

        (int exitCode, _, string error) = LineEndingGateRunner.RunScript(temp.Path, ".md");
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("不是纯 CRLF", error, StringComparison.Ordinal);
        Assert.DoesNotContain("警告", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 脚本必须真正读取共享剪枝文件，而不是依赖内联名单；
    /// 用复制脚本 + 自定义清单同时覆盖内置 `node_modules` 与自定义目录，只启动一次 PowerShell。
    /// </summary>
    [SourceTreeFact]
    public void VerifyLineEndingsScript_SkipsSharedPrunedDirectories()
    {
        string root = SourceTreeLocator.RootOrThrow();
        using TempDir temp = TempDir.Create();
        string tools = Directory.CreateDirectory(Path.Combine(temp.Path, "tools")).FullName;
        string script = Path.Combine(tools, "verify-line-endings.ps1");
        File.Copy(Path.Combine(root, "tools", "verify-line-endings.ps1"), script);
        string defaultList = File.ReadAllText(
            Path.Combine(root, SourceTreeLocator.PrunedListRelativePath));
        File.WriteAllText(
            Path.Combine(tools, "gate-pruned-directories.txt"),
            defaultList + "\r\ncustom_skip\r\n");
        _ = temp.File("good.md", "line1\r\nline2\r\n");
        string defaultBad = temp.File("dist/bad.md", "line1\nline2");
        string nodeBad = temp.File("node_modules/bad.md", "line1\nline2");
        string customBad = temp.File("custom_skip/other.md", "line1\nline2");

        (int exitCode, _, _) = LineEndingGateRunner.RunScriptAt(script, temp.Path, ".md");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain('\r', File.ReadAllText(defaultBad));
        Assert.DoesNotContain('\r', File.ReadAllText(nodeBad));
        Assert.DoesNotContain('\r', File.ReadAllText(customBad));
    }
}
