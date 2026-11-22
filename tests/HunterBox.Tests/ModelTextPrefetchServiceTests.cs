using System.Runtime.Versioning;
using HunterBox.Application.Models;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

/// <summary>A-06：分类/文本/负缓存判定与缓存签名在 Application 层可测。</summary>
public class ModelTextPrefetchServiceTests
{
    [Fact]
    public void ShouldClassify_NullSet_UsesEmptyDescriptionRule()
    {
        ModRecord empty = Create(description: null);
        ModRecord described = Create(description: "已有描述");

        Assert.True(ModelTextPrefetchService.ShouldClassify(empty, null));
        Assert.False(ModelTextPrefetchService.ShouldClassify(described, null));
    }

    [Fact]
    public void ShouldClassify_ExplicitSet_OnlyContainsIds()
    {
        ModRecord mod = Create(description: "已有描述");

        Assert.True(ModelTextPrefetchService.ShouldClassify(mod, new HashSet<int> { mod.Id }));
        Assert.False(ModelTextPrefetchService.ShouldClassify(mod, new HashSet<int> { mod.Id + 1 }));
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, true, true, false, false)]
    public void NeedsNegativeCache_OnlyWhenScanAndClassifySucceededWithoutModels(
        bool classify,
        bool scanSucceeded,
        bool classifySucceeded,
        bool classified,
        bool expected) =>
        Assert.Equal(
            expected,
            ModelTextPrefetchService.NeedsNegativeCache(classify, scanSucceeded, classifySucceeded, classified));

    /// <summary>明细缓存要求戳可读且 pak 条目全部读取成功。</summary>
    [Theory]
    [InlineData("pak:1|2", true, true)]
    [InlineData("pak:1|2", false, false)]
    [InlineData(ModelTextPrefetchService.UnreadableStampPrefix + "x", true, false)]
    [InlineData(ModelTextPrefetchService.UnreadableStampPrefix + "x", false, false)]
    public void CanCacheModelDetails_RequiresReadableStampAndEntries(
        string stamp,
        bool entriesReadable,
        bool expected) =>
        Assert.Equal(expected, ModelTextPrefetchService.CanCacheModelDetails(stamp, entriesReadable));

    /// <summary>负缓存还要求所有根层 pak 读取成功（空 pak 也算成功）。</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldRecordNoModel_RequiresAllPaksReadable(bool allPaksReadable, bool expected) =>
        Assert.Equal(
            expected,
            ModelTextPrefetchService.ShouldRecordNoModel(true, true, true, false, allPaksReadable));

    /// <summary>超长文本收紧到上限并带截断标记；未超限时原样返回。</summary>
    [Fact]
    public void ClampText_TruncatesOnlyOverLimit()
    {
        string shortText = new('a', ModelTextPrefetchService.MaxTextLength - 16);
        string longText = new('a', ModelTextPrefetchService.MaxTextLength + 1);

        Assert.Equal(shortText, ModelTextPrefetchService.ClampText(shortText));
        string clamped = ModelTextPrefetchService.ClampText(longText);

        Assert.True(clamped.Length <= ModelTextPrefetchService.MaxTextLength);
        Assert.EndsWith("…（已截断）", clamped, StringComparison.Ordinal);
    }

    /// <summary>非 ASCII 文本按 UTF-8 字节提前截断，整份缓存体积受字节上限约束。</summary>
    [Fact]
    public void ClampText_CjkText_RespectsUtf8ByteLimit()
    {
        string text = new('中', 2000);

        string clamped = ModelTextPrefetchService.ClampText(text);

        Assert.True(clamped.Length < text.Length);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(clamped) <= ModelTextPrefetchService.MaxTextUtf8Bytes);
        Assert.EndsWith("…（已截断）", clamped, StringComparison.Ordinal);
    }

    /// <summary>截断点不拆分代理对，emoji 不会变成孤立代理/U+FFFD。</summary>
    [Fact]
    public void ClampText_Emoji_DoesNotSplitSurrogatePair()
    {
        string text = string.Concat(Enumerable.Repeat("😀", 1500));

        string clamped = ModelTextPrefetchService.ClampText(text);

        for (int index = 0; index < clamped.Length; index++)
        {
            if (char.IsHighSurrogate(clamped[index]))
            {
                Assert.True(index + 1 < clamped.Length && char.IsLowSurrogate(clamped[index + 1]));
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(clamped[index]));
            }
        }

        Assert.EndsWith("…（已截断）", clamped, StringComparison.Ordinal);
    }

    /// <summary>孤立代理输入不抛异常，截断仍受字符上限约束。</summary>
    [Fact]
    public void ClampText_LoneSurrogate_DoesNotThrow()
    {
        string text = "\uD800" + new string('a', ModelTextPrefetchService.MaxTextLength);

        string clamped = ModelTextPrefetchService.ClampText(text);

        Assert.True(clamped.Length <= ModelTextPrefetchService.MaxTextLength);
        Assert.EndsWith("…（已截断）", clamped, StringComparison.Ordinal);
    }

    /// <summary>超长或超字节内容戳改为确定性哈希，短戳原样返回。</summary>
    [Fact]
    public void BoundStamp_LongStamp_IsHashedAndStable()
    {
        string shortStamp = new('a', ModelTextPrefetchService.MaxStampLength);
        Assert.Equal(shortStamp, ModelTextPrefetchService.BoundStamp(shortStamp));

        string chineseStamp = new('中', 300);
        Assert.Equal(chineseStamp, ModelTextPrefetchService.BoundStamp(chineseStamp));

        string longStamp = new('b', ModelTextPrefetchService.MaxStampLength + 1);
        string first = ModelTextPrefetchService.BoundStamp(longStamp);
        string second = ModelTextPrefetchService.BoundStamp(longStamp);

        Assert.StartsWith("hash:", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.NotEqual(first, ModelTextPrefetchService.BoundStamp(longStamp + "c"));
        Assert.True(first.Length <= ModelTextPrefetchService.MaxStampLength);

        // 非 ASCII 超过字节上限（300 × 3 = 900 < 1024 不触发；400 × 3 = 1200 触发）。
        Assert.StartsWith(
            "hash:",
            ModelTextPrefetchService.BoundStamp(new string('中', 400)),
            StringComparison.Ordinal);
    }

    /// <summary>快路径 341 字符与需要字节检查的 342 字符边界。</summary>
    [Fact]
    public void BoundStamp_LengthBoundary_SeparatesFastPathFromByteCheck()
    {
        // 341 字符走常数时间快路径；342 字符走「字符数 + UTF-8 字节数」检查。
        string ascii341 = new('a', ModelTextPrefetchService.MaxStampUtf8Bytes / 3);
        string ascii342 = new('a', ModelTextPrefetchService.MaxStampUtf8Bytes / 3 + 1);
        Assert.Equal(ascii341, ModelTextPrefetchService.BoundStamp(ascii341));
        Assert.Equal(ascii342, ModelTextPrefetchService.BoundStamp(ascii342));

        // 341 个中文是 1023 字节，仍在字节上限内；342 个是 1026 字节，必须哈希。
        string chinese341 = new('中', ModelTextPrefetchService.MaxStampUtf8Bytes / 3);
        string chinese342 = new('中', ModelTextPrefetchService.MaxStampUtf8Bytes / 3 + 1);
        Assert.Equal(chinese341, ModelTextPrefetchService.BoundStamp(chinese341));
        Assert.StartsWith("hash:", ModelTextPrefetchService.BoundStamp(chinese342), StringComparison.Ordinal);
    }

    [Fact]
    public void TextSignature_ChangesWhenDescriptionOrFileListChanges()
    {
        ModRecord mod = Create(description: "描述");
        string baseline = ModelTextPrefetchService.TextSignature(mod);

        ModRecord changedDescription = Create(description: "新描述");
        ModRecord changedFiles = Create(description: "描述", files: ["natives/stm/b.mod3"]);

        Assert.NotEqual(baseline, ModelTextPrefetchService.TextSignature(changedDescription));
        Assert.NotEqual(baseline, ModelTextPrefetchService.TextSignature(changedFiles));
    }

    /// <summary>签名与「描述 + 分隔符 + 文件清单」的朴素哈希完全一致（复用缓冲不改变字节流）。</summary>
    [Fact]
    public void TextSignature_MatchesNaiveHashConstruction()
    {
        ModRecord mod = Create(description: "描述", files: ["a/b.mod3", "c.mod3"]);

        byte[] expected = System.Security.Cryptography.SHA256.HashData(
        [
            .. System.Text.Encoding.UTF8.GetBytes("描述"),
            (byte)ModelTextPrefetchService.DescriptionSeparatorChar,
            .. System.Text.Encoding.UTF8.GetBytes("a/b.mod3"),
            (byte)ModelTextPrefetchService.FileListSeparatorChar,
            .. System.Text.Encoding.UTF8.GetBytes("c.mod3"),
        ]);

        Assert.Equal(
            Convert.ToHexString(expected),
            ModelTextPrefetchService.TextSignature(mod));
    }

    /// <summary>复用同一记录/清单/描述的签名；任一项变化都必须重新计算。</summary>
    [Fact]
    public void ReuseOrComputeSignature_ReusesOnlyUnchangedRecordState()
    {
        ModRecord mod = Create(description: "描述");
        Dictionary<int, KnownTextSignature> known = new()
        {
            [mod.Id] = KnownTextSignature.From(mod, "known-signature"),
        };

        Assert.Equal("known-signature", ModelTextPrefetchService.ReuseOrComputeSignature(mod, known));

        // 同一实例替换文件清单：必须重新计算。
        mod.RelativeFiles = ["natives/stm/other.mod3"];
        string afterFiles = ModelTextPrefetchService.ReuseOrComputeSignature(mod, known);
        Assert.NotEqual("known-signature", afterFiles);
        Assert.Equal(ModelTextPrefetchService.TextSignature(mod), afterFiles);

        // 同一实例替换描述：必须重新计算。
        mod.RelativeFiles = ["natives/stm/a.mod3"];
        mod.Metadata = new ModMetadata { Description = "新描述" };
        string afterDescription = ModelTextPrefetchService.ReuseOrComputeSignature(mod, known);
        Assert.NotEqual(afterFiles, afterDescription);
        Assert.Equal(ModelTextPrefetchService.TextSignature(mod), afterDescription);

        // 记录被整体替换：同样重新计算。
        ModRecord replaced = Create(description: "描述");
        string computed = ModelTextPrefetchService.ReuseOrComputeSignature(replaced, known);
        Assert.NotEqual("known-signature", computed);
        Assert.Equal(64, computed.Length);
        Assert.Equal(computed, ModelTextPrefetchService.TextSignature(replaced));

        Assert.Equal(ModelTextPrefetchService.TextSignature(mod), ModelTextPrefetchService.ReuseOrComputeSignature(mod, null));
    }

    /// <summary>容量按条目数留 2 倍余量，至少 1024。</summary>
    [Fact]
    public void TextCacheCapacity_AtLeastDoubleWithMinimum()
    {
        Assert.Equal(1024, ModelTextPrefetchService.TextCacheCapacity(0));
        Assert.Equal(1024, ModelTextPrefetchService.TextCacheCapacity(512));
        Assert.Equal(1200, ModelTextPrefetchService.TextCacheCapacity(600));
    }

    /// <summary>同名 pak 内容被替换后内容戳必须变化。</summary>
    [Fact]
    public void PakStamp_ReplacingPakContent_ChangesStamp()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["mod.pak"]);
        string file = Path.Combine(paths.FilesDirectory(mod.Id), "mod.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "a");
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string first = ModelTextPrefetchService.PakStamp(paths, mod, fileSystem);
        File.WriteAllText(file, "longer-content");
        string second = ModelTextPrefetchService.PakStamp(paths, mod, fileSystem);

        Assert.NotEmpty(first);
        Assert.NotEqual(first, second);
    }

    /// <summary>没有根层 pak 时内容戳为空；子目录 pak 不参与。</summary>
    [Fact]
    public void PakStamp_NoRootPak_IsEmpty()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["natives/stm/a.mod3", "sub/mod.pak"]);
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        Assert.Equal(string.Empty, ModelTextPrefetchService.PakStamp(paths, mod, fileSystem));
    }

    /// <summary>外部替换根层 pak 后身份戳必须变化，避免已用 ID 误判为空闲。</summary>
    [Fact]
    public void IdentityStamp_ReplacingPakContent_ChangesStamp()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["mod.pak"]);
        string file = Path.Combine(paths.FilesDirectory(mod.Id), "mod.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "a");
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string first = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        File.WriteAllText(file, "longer-content");
        string second = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.StartsWith("pak:", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    /// <summary>纯散装 MOD 的戳覆盖子目录，改名或子目录时间变化都会失效。</summary>
    [Fact]
    public void IdentityStamp_LooseFiles_TracksNestedDirectories()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["natives/stm/a.mod3"]);
        string directory = paths.FilesDirectory(mod.Id);
        string nested = Path.Combine(directory, "natives", "stm");
        Directory.CreateDirectory(nested);
        string file = Path.Combine(nested, "a.mod3");
        File.WriteAllText(file, "x");
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string first = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        File.Move(file, Path.Combine(nested, "b.mod3"));
        // NTFS 目录时间戳可能延迟提交，且不同文件系统粒度不同：显式改一次子目录时间，稳定验证戳会跟随子目录元数据。
        Directory.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddMinutes(1));
        string renamed = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        Directory.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddHours(1));
        string touched = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.StartsWith("dir:", first, StringComparison.Ordinal);
        Assert.NotEqual(first, renamed);
        Assert.NotEqual(renamed, touched);
    }

    /// <summary>根层 pak + 散装文件混合的 MOD，散装目录变化也必须改变戳。</summary>
    [Fact]
    public void IdentityStamp_MixedPakAndLooseFiles_TracksBothParts()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["mod.pak", "natives/stm/a.mod3"]);
        string root = paths.FilesDirectory(mod.Id);
        Directory.CreateDirectory(Path.Combine(root, "natives", "stm"));
        File.WriteAllText(Path.Combine(root, "mod.pak"), "pak");
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string first = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        Directory.SetLastWriteTimeUtc(
            Path.Combine(root, "natives", "stm"),
            DateTime.UtcNow.AddHours(1));
        string second = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.StartsWith("pak:", first, StringComparison.Ordinal);
        Assert.Contains("|dir:", first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    /// <summary>Files 目录缺失时戳稳定，不返回随机值（避免每次打开编辑器都重扫）。</summary>
    [Fact]
    public void IdentityStamp_MissingDirectory_IsStable()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["natives/stm/a.mod3"]);
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string first = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        string second = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.Equal("dir:missing", first);
        Assert.Equal(first, second);
    }

    /// <summary>目录集合由文件清单派生，深层目录出现/消失会改变戳（只 stat 目录）。</summary>
    [Fact]
    public void IdentityStamp_LooseFiles_TracksDirectoryExistence()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["natives/stm/a.mod3"]);
        string directory = paths.FilesDirectory(mod.Id);
        Directory.CreateDirectory(Path.Combine(directory, "natives", "stm"));
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string present = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        Directory.Delete(Path.Combine(directory, "natives", "stm"), recursive: true);
        string missing = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.StartsWith("dir:", present, StringComparison.Ordinal);
        Assert.NotEqual(present, missing);
    }

    /// <summary>pak 读取失败返回统一的一次性哨兵（两次结果不同），不再把不可读标记拼进 pak 戳。</summary>
    [Fact]
    public void IdentityStamp_UnreadablePak_ReturnsOneShotSentinel()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["mod.pak"]);
        // 同名目录让 FileInfo.Length 读取失败，稳定模拟不可读的 pak（不依赖 ACL 权限）。
        Directory.CreateDirectory(Path.Combine(paths.FilesDirectory(mod.Id), "mod.pak"));
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        string first = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);
        string second = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.True(ModelTextPrefetchService.IsUnreadableStamp(first));
        Assert.NotEqual(first, second);
    }

    /// <summary>纯散装 MOD 根目录无权限时返回一次性哨兵，权限恢复后立即重扫。</summary>
    [DirectoryAttributeAclFact]
    [SupportedOSPlatform("windows")]
    public void IdentityStamp_LooseRootUnreadable_ReturnsOneShotSentinel()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["natives/stm/a.mod3"]);
        string root = paths.FilesDirectory(mod.Id);
        Directory.CreateDirectory(Path.Combine(root, "natives", "stm"));
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        using DirectoryReadAttributesDeny deny = DirectoryReadAttributesDeny.Apply(root);
        string stamp = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.True(ModelTextPrefetchService.IsUnreadableStamp(stamp));
    }

    /// <summary>混合 MOD 的散装部分不可读时也返回统一哨兵（pak 部分仍可读）。</summary>
    [DirectoryAttributeAclFact]
    [SupportedOSPlatform("windows")]
    public void IdentityStamp_MixedLooseUnreadable_ReturnsOneShotSentinel()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = Create(description: null, files: ["mod.pak", "natives/stm/a.mod3"]);
        string root = paths.FilesDirectory(mod.Id);
        Directory.CreateDirectory(Path.Combine(root, "natives", "stm"));
        File.WriteAllText(Path.Combine(root, "mod.pak"), "pak");
        HunterBox.Application.Common.IFileSystem fileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

        using DirectoryReadAttributesDeny deny = DirectoryReadAttributesDeny.Apply(root);
        string stamp = ModelTextPrefetchService.IdentityStamp(paths, mod, fileSystem);

        Assert.True(ModelTextPrefetchService.IsUnreadableStamp(stamp));
    }

    [Fact]
    public void FallbackText_UsesDescriptionOrEmptyString()
    {
        Assert.Equal("描述", ModelTextPrefetchService.FallbackText(Create(description: "描述")));
        Assert.Equal(string.Empty, ModelTextPrefetchService.FallbackText(Create(description: null)));
    }

    private static ModRecord Create(string? description, List<string>? files = null) => new()
    {
        Id = 1,
        Index = 1,
        GameId = TestGames.Wilds.Id,
        Kind = ModKind.LooseFiles,
        RelativeFiles = files ?? ["natives/stm/a.mod3"],
        Metadata = new ModMetadata { Description = description },
    };
}
