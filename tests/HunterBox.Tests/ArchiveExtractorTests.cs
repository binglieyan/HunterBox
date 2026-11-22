using System.IO.Compression;
using System.Text;
using HunterBox.Application.Common;
using HunterBox.Domain.Archives;
using HunterBox.Infrastructure.Archives;

namespace HunterBox.Tests;

public class ArchiveExtractorTests
{
    /// <summary>只记录错误的内存日志，用于验证端口注入（A-23）。</summary>
    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Errors { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void WarnBatch(IReadOnlyList<string> messages)
        {
            // 本用例不应走批量告警；走到这里说明调用路径变了，直接失败而不是静默忽略。
            throw new InvalidOperationException("解压用例不应批量记录警告");
        }

        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }

    [Fact]
    public void Extract_TotalSizeExceedsLimit_ThrowsInvalidDataException()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("a.txt", new string('x', 64)));
        string destination = temp.Dir("out");
        ArchiveExtractionOptions options = new() { MaxTotalBytes = 16 };

        // C-13：按声明总量提前拒绝压缩炸弹/损坏包。
        Assert.Throws<InvalidDataException>(() => new ArchiveExtractor().Extract(zip, destination, options));
    }

    [Fact]
    public void Extract_NestedArchives_ShareTotalSizeBudget()
    {
        using TempDir temp = TempDir.Create();
        string stage = temp.Dir("stage");
        string inner = ZipBuilder.Create(
            stage,
            "inner.zip",
            ("b.bin", new string('b', 900)),
            ("c.bin", new string('c', 900)));

        // 外层：900 字节普通条目 + 压缩后的 inner.zip；两层的声明总量都低于上限，
        // 但嵌套共享预算后累计写入必须被拒绝。
        string outer = Path.Combine(stage, "outer.zip");
        using (FileStream file = File.Create(outer))
        using (ZipArchive zip = new(file, ZipArchiveMode.Create))
        {
            ZipArchiveEntry payload = zip.CreateEntry("a.bin");
            using (StreamWriter writer = new(payload.Open()))
            {
                writer.Write(new string('a', 900));
            }

            zip.CreateEntryFromFile(inner, "inner.zip");
        }

        string destination = temp.Dir("out");
        ArchiveExtractionOptions options = new() { MaxTotalBytes = 2000 };

        Assert.Throws<InvalidDataException>(() => new ArchiveExtractor().Extract(outer, destination, options));
    }

    [Fact]
    public void Extract_NestedArchiveWithUnknownExtension_IsDetectedByContent()
    {
        using TempDir temp = TempDir.Create();
        string stage = temp.Dir("stage");
        string inner = ZipBuilder.Create(stage, "inner.zip", ("natives/inner.tex", "inner"));
        string outer = Path.Combine(stage, "outer.zip");
        using (FileStream file = File.Create(outer))
        using (ZipArchive zip = new(file, ZipArchiveMode.Create))
        {
            // .bin 不在扩展名白名单/已知非压缩列表中：必须按内容识别并递归解压。
            zip.CreateEntryFromFile(inner, "payload.bin");
        }

        string destination = temp.Dir("out");
        new ArchiveExtractor().Extract(outer, destination);

        Assert.True(File.Exists(Path.Combine(destination, "payload.binfiles", "natives", "inner.tex")));
        Assert.False(File.Exists(Path.Combine(destination, "payload.bin")));
    }

    [Fact]
    public void Extract_SingleEntryExceedsLimit_ThrowsInvalidDataException()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "mod.zip", ("a.txt", new string('x', 64)));
        string destination = temp.Dir("out");
        ArchiveExtractionOptions options = new() { MaxEntryBytes = 16 };

        Assert.Throws<InvalidDataException>(() => new ArchiveExtractor().Extract(zip, destination, options));
    }

    [Fact]
    public void Extract_WritesAllFilesWithNormalizedPaths()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(
            temp.Path,
            "mod.zip",
            (@"natives\STM\a.tex", "A"),
            ("modinfo.ini", "name=x"));

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(zip, destination);

        Assert.Equal(2, files.Count);
        Assert.True(File.Exists(Path.Combine(destination, "natives", "stm", "a.tex")));
        Assert.True(File.Exists(Path.Combine(destination, "modinfo.ini")));
        Assert.All(files, f => Assert.DoesNotContain('\\', f));
    }

    [Fact]
    public void Extract_RejectsEntriesEscapingDestination()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(
            temp.Path,
            "evil.zip",
            ("../evil.txt", "pwned"));

        Assert.Throws<InvalidDataException>(() => new ArchiveExtractor().Extract(zip, temp.Dir("out")));
    }

    [Fact]
    public void Extract_Failure_LogsThroughInjectedLogger()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(
            temp.Path,
            "evil.zip",
            ("../evil.txt", "pwned"));
        RecordingLogger logger = new();

        Assert.Throws<InvalidDataException>(
            () => new ArchiveExtractor(logger).Extract(zip, temp.Dir("out")));

        Assert.Contains(logger.Errors, message => message.Contains("解压失败"));
    }

    [Fact]
    public void Extract_RejectsCaseVariantDuplicateEntries()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(
            temp.Path,
            "case.zip",
            ("natives/STM/A.tex", "A"),
            ("natives/stm/a.tex", "a"));

        Assert.Throws<InvalidDataException>(() => new ArchiveExtractor().Extract(zip, temp.Dir("out")));
    }

    [Fact]
    public void Extract_ThrowsWhenArchiveMissing()
    {
        using TempDir temp = TempDir.Create();

        Assert.Throws<FileNotFoundException>(() => new ArchiveExtractor().Extract(
            Path.Combine(temp.Path, "nope.zip"),
            temp.Dir("out")));
    }

    /// <summary>
    /// 内层包在写出部分条目后损坏时，必须删除本次创建的
    /// <c>&lt;包名&gt;files</c>，并把原嵌套包移出产物目录，避免被当普通 MOD 安装。
    /// </summary>
    [Fact]
    public void Extract_NestedArchiveFailsMidway_RemovesPartialDirectory()
    {
        using TempDir temp = TempDir.Create();
        string inner = ZipBuilder.Create(
            temp.Path,
            "inner.zip",
            ("natives/first.tex", "first"),
            ("natives/second.tex", "second"));
        CorruptSecondLocalHeader(inner);
        byte[] innerBytes = File.ReadAllBytes(inner);
        string outer = ZipTestTools.CreateFromBytes(temp.Path, "outer.zip", ("inner.zip", innerBytes));

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(outer, destination);

        Assert.False(
            Directory.Exists(Path.Combine(destination, "inner.zipfiles")),
            "内层解压失败后不应留下半成品目录");
        Assert.DoesNotContain(
            files,
            file => file.StartsWith("inner.zipfiles/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("inner.zip", files);
        Assert.False(File.Exists(Path.Combine(destination, "inner.zip")));
        Assert.False(Directory.Exists(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + ".nested-kept"));
    }

    /// <summary>内容损坏的端到端流程不得误删预存在隔离内容。</summary>
    [Fact]
    public void Extract_PreservesPreExistingQuarantineContent()
    {
        using TempDir temp = TempDir.Create();
        string inner = ZipBuilder.Create(
            temp.Path,
            "inner.zip",
            ("natives/a.tex", "a"),
            ("natives/b.tex", "b"));
        CorruptSecondLocalHeader(inner);
        string outer = ZipTestTools.CreateFromBytes(
            temp.Path,
            "outer.zip",
            ("inner.zip", File.ReadAllBytes(inner)));
        string destination = temp.Dir("out");
        string quarantineBase =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + ".nested-kept";
        Directory.CreateDirectory(quarantineBase);
        string leftover = Path.Combine(quarantineBase, "leftover.zip");
        File.WriteAllText(leftover, "x");
        File.SetAttributes(leftover, FileAttributes.ReadOnly);
        try
        {
            IReadOnlyList<string> files = new ArchiveExtractor().Extract(outer, destination);

            Assert.DoesNotContain(files, file => file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Combine(destination, "inner.zip")));
            Assert.True(File.Exists(leftover), "预存在隔离文件不应被本次解压删除");
            Assert.Empty(Directory.GetDirectories(quarantineBase));
        }
        finally
        {
            File.SetAttributes(leftover, FileAttributes.Normal);
            Directory.Delete(quarantineBase, recursive: true);
        }
    }

    /// <summary>预先存在的空隔离目录不能被本次解压删除。</summary>
    [Fact]
    public void Extract_PreservesPreExistingEmptyQuarantineDirectory()
    {
        using TempDir temp = TempDir.Create();
        string zip = ZipBuilder.Create(temp.Path, "plain.zip", ("natives/a.tex", "a"));
        string destination = temp.Dir("out");
        string quarantineBase =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + ".nested-kept";
        Directory.CreateDirectory(quarantineBase);
        try
        {
            new ArchiveExtractor().Extract(zip, destination);

            Assert.True(Directory.Exists(quarantineBase), "预存在的空隔离目录不应被删除");
        }
        finally
        {
            Directory.Delete(quarantineBase, recursive: true);
        }
    }

    /// <summary>清理本次隔离子目录时必须同时处理只读目录与文件。</summary>
    [Fact]
    public void TryDeleteQuarantine_ReadOnlyDirectoryAndFile_DeletesRunDirectory()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("quarantine");
        string sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        string file = Path.Combine(sub, "a.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(sub, FileAttributes.ReadOnly);

        ArchiveExtractor.TryDeleteQuarantine(root);

        Assert.False(Directory.Exists(root));
    }

    /// <summary>
    /// 目标目录在解压前已存在时，内层失败只能清理暂存，
    /// 不能把本次半成品混进既有目录。
    /// </summary>
    [Fact]
    public void Extract_NestedArchiveFailsWithPreExistingTarget_LeavesTargetUntouched()
    {
        using TempDir temp = TempDir.Create();
        string inner = ZipBuilder.Create(
            temp.Path,
            "inner.zip",
            ("natives/first.tex", "first"),
            ("natives/second.tex", "second"));
        CorruptSecondLocalHeader(inner);
        byte[] innerBytes = File.ReadAllBytes(inner);
        string outer = ZipTestTools.CreateFromBytes(
            temp.Path,
            "outer.zip",
            ("inner.zip", innerBytes),
            ("inner.zipfiles/existing.txt", Encoding.UTF8.GetBytes("existing")));

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(outer, destination);

        string target = Path.Combine(destination, "inner.zipfiles");
        Assert.True(Directory.Exists(target));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(target, "natives", "first.tex")));
        Assert.False(File.Exists(Path.Combine(target, "natives", "second.tex")));
        Assert.DoesNotContain(
            files,
            file => file.StartsWith("inner.zipfiles/natives/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("inner.zip", files);
        Assert.False(File.Exists(Path.Combine(destination, "inner.zip")));
        Assert.Empty(Directory.GetDirectories(destination, "*.tmp-*", SearchOption.AllDirectories));
    }

    /// <summary>成功嵌套包也要走「移入运行隔离目录 → 结束清理」的完整流程。</summary>
    [Fact]
    public void Extract_ValidNestedArchive_QuarantinesOriginalAndPreservesExistingContent()
    {
        using TempDir temp = TempDir.Create();
        string inner = ZipBuilder.Create(temp.Path, "inner.zip", ("natives/inner.tex", "inner"));
        string outer = ZipTestTools.CreateFromBytes(
            temp.Path,
            "outer.zip",
            ("inner.zip", File.ReadAllBytes(inner)));
        string destination = temp.Dir("out");
        string quarantineBase =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + ".nested-kept";
        Directory.CreateDirectory(quarantineBase);
        string leftover = Path.Combine(quarantineBase, "leftover.zip");
        File.WriteAllText(leftover, "keep");
        try
        {
            IReadOnlyList<string> files = new ArchiveExtractor().Extract(outer, destination);

            Assert.Contains("inner.zipfiles/natives/inner.tex", files);
            Assert.False(File.Exists(Path.Combine(destination, "inner.zip")));
            Assert.True(File.Exists(leftover));
            Assert.Empty(Directory.GetDirectories(quarantineBase));
        }
        finally
        {
            Directory.Delete(quarantineBase, recursive: true);
        }
    }

    /// <summary>/L-01：目标目录预先存在且内层解压成功时，既有文件与内层文件都要保留。</summary>
    [Fact]
    public void Extract_NestedArchiveSucceedsWithPreExistingTarget_MergesAllFiles()
    {
        using TempDir temp = TempDir.Create();
        string inner = ZipBuilder.Create(temp.Path, "inner.zip", ("natives/inner.tex", "inner"));
        string outer = ZipTestTools.CreateFromBytes(
            temp.Path,
            "outer.zip",
            ("inner.zip", File.ReadAllBytes(inner)),
            ("inner.zipfiles/existing.txt", Encoding.UTF8.GetBytes("existing")));

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(outer, destination);

        string target = Path.Combine(destination, "inner.zipfiles");
        Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(target, "natives", "inner.tex")));
        Assert.DoesNotContain("inner.zip", files);
        Assert.Empty(Directory.GetDirectories(destination, "*.tmp-*", SearchOption.AllDirectories));
    }

    /// <summary>深度 ≥ 2 的内层损坏也必须把原包移出产物目录。</summary>
    [Fact]
    public void Extract_DeepNestedCorrupt_RemovesAllArchiveFiles()
    {
        using TempDir temp = TempDir.Create();
        string deep = ZipBuilder.Create(
            temp.Path,
            "deep.zip",
            ("natives/a.tex", "a"),
            ("natives/b.tex", "b"));
        CorruptSecondLocalHeader(deep);
        string inner = ZipTestTools.CreateFromBytes(
            temp.Path,
            "inner.zip",
            ("deep.zip", File.ReadAllBytes(deep)));
        string outer = ZipTestTools.CreateFromBytes(
            temp.Path,
            "outer.zip",
            ("inner.zip", File.ReadAllBytes(inner)));

        string destination = temp.Dir("out");
        IReadOnlyList<string> files = new ArchiveExtractor().Extract(outer, destination);

        Assert.DoesNotContain(files, file => file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(destination, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(destination, "*.nested-kept", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(destination, "*.tmp-*", SearchOption.AllDirectories));
    }

    /// <summary>合并覆盖只读目标失败时必须抛出，目标保持旧内容。</summary>
    [Fact]
    public void MergeOrMoveDirectory_ReadOnlyTarget_ThrowsAndKeepsOldContent()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("staging");
        string destination = temp.Dir("target");
        File.WriteAllText(Path.Combine(source, "a.txt"), "new");
        string target = Path.Combine(destination, "a.txt");
        File.WriteAllText(target, "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            Exception? error = Record.Exception(() =>
                ArchiveExtractor.MergeOrMoveDirectory(source, destination, CancellationToken.None));

            Assert.NotNull(error);
            Assert.True(
                error is IOException or UnauthorizedAccessException,
                $"合并失败应抛 IO/权限异常，实际为 {error.GetType().Name}");
            Assert.Equal("old", File.ReadAllText(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>合并阶段必须响应取消，且取消时源文件保持原位。</summary>
    [Fact]
    public void MergeOrMoveDirectory_Canceled_ThrowsAndKeepsSource()
    {
        using TempDir temp = TempDir.Create();
        string source = temp.Dir("staging");
        string destination = temp.Dir("target");
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ArchiveExtractor.MergeOrMoveDirectory(source, destination, cancellation.Token));
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
    }

    /// <summary>深层只读原包也必须隔离到顶层产物目录之外。</summary>
    [Fact]
    public void RemoveNestedArchive_DeepReadOnlyFile_QuarantinesOutsideProductRoot()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("out");
        string deepDirectory = Path.Combine(root, "inner.zipfiles", "deep");
        Directory.CreateDirectory(deepDirectory);
        string path = Path.Combine(deepDirectory, "readonly.zip");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        string quarantineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + ".nested-kept";
        try
        {
            ArchiveExtractor.RemoveNestedArchive(path, quarantineRoot);

            Assert.False(File.Exists(path));
            string kept = Assert.Single(Directory.GetFiles(quarantineRoot));
            Assert.Contains("readonly.zip", Path.GetFileName(kept));
            Assert.Empty(Directory.GetDirectories(root, "*.nested-kept", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(quarantineRoot))
            {
                foreach (string file in Directory.GetFiles(quarantineRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(quarantineRoot, recursive: true);
            }
        }
    }

    /// <summary>原包既不能删除也不能移动时必须中止，而不是静默继续。</summary>
    [Fact]
    public void RemoveNestedArchive_LockedFile_ThrowsAndKeepsFile()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("out");
        string path = Path.Combine(root, "locked.zip");
        File.WriteAllText(path, "x");
        using FileStream lockStream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        string quarantineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + ".nested-kept";

        Assert.Throws<IOException>(() => ArchiveExtractor.RemoveNestedArchive(path, quarantineRoot));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// 嵌套包被占用（可读但拒绝移动/删除）时，嵌套处理必须抛 <see cref="IOException"/> 中止安装，
    /// 原包保留、暂存目录不残留。
    /// </summary>
    [Fact]
    public void ExtractNestedArchives_LockedNestedArchive_ThrowsAndKeepsOriginal()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("out");
        string inner = ZipBuilder.Create(root, "inner.zip", ("natives/inner.tex", "inner"));
        string quarantineRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + ".nested-kept";
        try
        {
            // 允许读取（解压能成功），拒绝删除/移动（FileShare.Delete 未授予）。
            using (new FileStream(inner, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => ArchiveExtractor.ExtractNestedArchives(
                    root,
                    ["inner.zip"],
                    new ArchiveExtractionOptions(),
                    depth: 0,
                    quarantineRoot));
            }

            // 原包保留在产物目录，解出的内容已合并到目标目录。
            Assert.True(File.Exists(inner));
            Assert.True(File.Exists(Path.Combine(root, "inner.zipfiles", "natives", "inner.tex")));
            // 暂存目录已在 finally 中清理，不留半成品。
            Assert.Empty(Directory.GetDirectories(root, "*.tmp-*", SearchOption.AllDirectories));
        }
        finally
        {
            ArchiveExtractor.TryDeleteQuarantine(quarantineRoot);
            if (Directory.Exists(quarantineRoot))
            {
                Directory.Delete(quarantineRoot, recursive: true);
            }
        }
    }

    /// <summary>把第二个 local file header 的签名清零，模拟「第一个条目可读、第二个条目损坏」。</summary>
    private static void CorruptSecondLocalHeader(string zipPath)
    {
        byte[] data = File.ReadAllBytes(zipPath);
        ReadOnlySpan<byte> signature = [0x50, 0x4B, 0x03, 0x04];
        int first = data.AsSpan().IndexOf(signature);
        Assert.True(first >= 0, "测试 ZIP 应至少包含一个 local header");
        int relative = data.AsSpan(first + signature.Length).IndexOf(signature);
        Assert.True(relative >= 0, "测试 ZIP 应包含第二个 local header");
        int second = first + signature.Length + relative;
        data.AsSpan(second, signature.Length).Clear();
        File.WriteAllBytes(zipPath, data);
    }
}
