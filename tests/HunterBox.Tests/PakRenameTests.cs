using System.Text;
using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// / 回归：pak 型 MOD「改模型 ID」必须只重写 TOC的 8 字节路径哈希，
/// 数据区/压缩/加密属性不动（与 pak 条目表重写等价）。
/// </summary>
public class PakRenameTests
{
    private const string ArmorPath = "natives/stm/art/model/character/ch02/012/003/ch02_012_003.mdf2.45";
    private const string ArmorNewPath = "natives/stm/art/model/character/ch02/015/003/ch02_012_003.mdf2.45";
    private const string ItemPath = "natives/stm/art/model/item/it10/10/0000/it1010_0000_0.mdf2.45";
    private const string ItemNewPath = "natives/stm/art/model/item/it10/10/0001/it1010_0000_0.mdf2.45";

    // ---------------------------------------------------------------- helpers

    private static PakModelRewriteService CreateService() => new(
        new RePakReader(),
        new RePakRenamer(),
        new PakPathHashProvider(),
        new PakRepacker());

    /// <summary>按引擎约定计算组合哈希：<c>(hash(大写) &lt;&lt; 32) | hash(小写)</c>。</summary>
    private static ulong EngineHash(string path)
    {
        string normalized = path.Replace('\\', '/');
        return ((ulong)PakPathHasher.HashString(normalized.ToUpperInvariant()) << 32)
            | PakPathHasher.HashString(normalized.ToLowerInvariant());
    }

    private static string BuildPak(string directory, params (string Path, byte[] Content)[] entries)
    {
        (string, byte[], long)[] converted = new (string, byte[], long)[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            converted[i] = (entries[i].Path, entries[i].Content, 0L);
        }

        return BuildPak(directory, 0, converted);
    }

    private static string BuildPak(
        string directory,
        short feature,
        params (string Path, byte[] Content, long Attributes)[] entries)
    {
        const int headerSize = 16;
        const int entrySize = 48;
        long dataOffset = headerSize + (entrySize * entries.Length);

        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory);
        writer.Write(RePakReader.Magic);
        writer.Write((byte)4);
        writer.Write((byte)0);
        writer.Write(feature);
        writer.Write(entries.Length);
        writer.Write(0u);

        long offset = dataOffset;
        foreach ((string path, byte[] content, long attributes) in entries)
        {
            ulong hash = EngineHash(path);
            writer.Write((uint)(hash & 0xFFFFFFFF));
            writer.Write((uint)(hash >> 32));
            writer.Write(offset);
            writer.Write((long)content.Length);
            writer.Write((long)content.Length);
            writer.Write(attributes);
            writer.Write(0uL);
            offset += content.Length;
        }

        foreach ((_, byte[] content, _) in entries)
        {
            writer.Write(content);
        }

        string pakPath = Path.Combine(directory, "mod-" + Guid.NewGuid().ToString("N") + ".pak");
        File.WriteAllBytes(pakPath, memory.ToArray());
        return pakPath;
    }

    private static byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    private static void AssertBytesEqual(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail($"字节不一致：offset={i} expected=0x{expected[i]:X2} actual=0x{actual[i]:X2}");
            }
        }
    }

    /// <summary>断言只有给定条目的 8 字节 TOC 哈希发生变化，其余字节（含数据区）完全不变。</summary>
    private static void AssertOnlyEntryHashesChanged(byte[] before, byte[] after, params int[] changedEntries)
    {
        Assert.Equal(before.Length, after.Length);

        HashSet<int> changed = [];
        foreach (int entryIndex in changedEntries)
        {
            int start = 16 + (entryIndex * 48);
            for (int i = 0; i < 8; i++)
            {
                changed.Add(start + i);
            }

            Assert.False(
                before.AsSpan(start, 8).SequenceEqual(after.AsSpan(start, 8)),
                $"条目 {entryIndex} 的哈希没有变化");
        }

        for (int i = 0; i < before.Length; i++)
        {
            if (!changed.Contains(i))
            {
                Assert.True(before[i] == after[i], $"offset={i} 不应对应改写而变化");
            }
        }
    }

    // ------------------------------------------------------- RewritePaths 主路径

    [Fact]
    public void RewritePaths_Armor_RenamesTocHashOnly_DataAndBackupIntact()
    {
        using TempDir temp = TempDir.Create();
        byte[] armorData = Encoding.ASCII.GetBytes("armor-payload-012");
        byte[] itemData = Encoding.ASCII.GetBytes("item-payload-0000");
        string pak = BuildPak(temp.Path, [(ArmorPath, armorData), (ItemPath, itemData)]);
        byte[] original = ReadBytes(pak);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ItemPath, ArmorNewPath]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "012", "015", index);

        Assert.Equal(2, report.EntriesScanned);
        Assert.Equal(1, report.EntriesChanged);
        Assert.Equal(1, report.Replacements);
        Assert.Empty(report.Warnings);

        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(PakPathHasher.HashPath(ArmorNewPath), archive.Entries[0].Hash);
        Assert.Equal(EngineHash(ItemPath), archive.Entries[1].Hash);

        // 数据区、偏移、大小、属性、校验和都不动
        Assert.Equal(16L + 48 + 48, archive.Entries[0].Offset);
        Assert.Equal(armorData.Length, archive.Entries[0].DecompressedSize);
        Assert.Equal(armorData.Length, archive.Entries[0].CompressedSize);
        Assert.Equal(0, archive.Entries[0].CompressionType);
        Assert.Equal(0, archive.Entries[0].EncryptionType);
        Assert.Equal(0uL, archive.Entries[0].Checksum);
        Assert.Equal(armorData, ReadEntryPayload(pak, 0));
        Assert.Equal(itemData, ReadEntryPayload(pak, 1));

        byte[] updated = ReadBytes(pak);
        AssertOnlyEntryHashesChanged(original, updated, 0);

        Assert.True(File.Exists(pak + ".toc.bak"), "首次写回前应生成 TOC 前像");
        Assert.Equal(
            PakTocPreImage.ReadOutcome.Ok,
            PakTocPreImage.ReadSnapshot(pak + ".toc.bak", out PakTocPreImage.Snapshot snapshot));
        AssertBytesEqual(original.AsSpan(16, 48 * 2).ToArray(), snapshot.Toc);
    }

    [Fact]
    public void RewritePaths_Weapon_RenamesItemId()
    {
        using TempDir temp = TempDir.Create();
        byte[] data = Encoding.ASCII.GetBytes("weapon-payload");
        string pak = BuildPak(temp.Path, [(ItemPath, data)]);
        PakNameIndex index = PakPathHasher.BuildIndex([ItemPath]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "0000", "0001", index);

        Assert.Equal(1, report.EntriesChanged);
        Assert.Equal(PakPathHasher.HashPath(ItemNewPath), Assert.Single(new RePakReader().Read(pak).Entries).Hash);
        Assert.Equal(data, ReadEntryPayload(pak, 0));
    }

    [Fact]
    public void RewritePaths_SameId_NoChanges_NoBackup()
    {
        using TempDir temp = TempDir.Create();
        string pak = BuildPak(temp.Path, [(ArmorPath, Encoding.ASCII.GetBytes("a")), (ItemPath, Encoding.ASCII.GetBytes("b"))]);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ItemPath, ArmorNewPath]);
        byte[] original = ReadBytes(pak);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "012", "012", index);

        Assert.Equal(2, report.EntriesScanned);
        Assert.Equal(0, report.EntriesChanged);
        Assert.Equal(0, report.Replacements);
        Assert.Empty(report.Warnings);
        AssertBytesEqual(original, ReadBytes(pak));
        Assert.False(File.Exists(pak + ".toc.bak"), "没有改写时不应生成 TOC 前像");
    }

    [Fact]
    public void RewritePaths_InvalidId_Throws()
    {
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ArmorNewPath]);

        Assert.Throws<ArgumentException>(() => CreateService().RewritePaths("unused.pak", "12", "015", index));
        Assert.Throws<ArgumentException>(() => CreateService().RewritePaths("unused.pak", "012", "0015", index));
    }

    [Fact]
    public void RewritePaths_MissingPak_ThrowsFileNotFound()
    {
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ArmorNewPath]);
        string missing = Path.Combine(Path.GetTempPath(), "hb-missing-" + Guid.NewGuid().ToString("N") + ".pak");

        Assert.Throws<FileNotFoundException>(() => CreateService().RewritePaths(missing, "012", "015", index));
    }

    [Fact]
    public void RewritePaths_EntryNotInIndex_AddsWarning()
    {
        using TempDir temp = TempDir.Create();
        const string unknownPath = "natives/stm/art/model/character/ch02/999/003/unknown.mod3";
        string pak = BuildPak(temp.Path, [(ArmorPath, Encoding.ASCII.GetBytes("a")), (unknownPath, Encoding.ASCII.GetBytes("b"))]);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ArmorNewPath]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "012", "015", index);

        Assert.Equal(1, report.EntriesChanged);
        string warning = Assert.Single(report.Warnings);
        Assert.Contains("不在路径清单", warning);
        Assert.Contains(EngineHash(unknownPath).ToString("X16"), warning);
    }

    [Fact]
    public void RewritePaths_CompressedAndEncryptedEntries_StillRename()
    {
        using TempDir temp = TempDir.Create();
        const long compression = 1L;
        const long encryption = 2L << 16;
        string pak = BuildPak(
            temp.Path,
            0,
            (ArmorPath, Encoding.ASCII.GetBytes("compressed"), compression),
            (ItemPath, Encoding.ASCII.GetBytes("encrypted"), encryption));
        byte[] original = ReadBytes(pak);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ItemPath, ArmorNewPath]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "012", "015", index);

        Assert.Equal(1, report.EntriesChanged);
        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(compression, archive.Entries[0].Attributes);
        Assert.Equal(encryption, archive.Entries[1].Attributes);
        Assert.Equal(PakPathHasher.HashPath(ArmorNewPath), archive.Entries[0].Hash);
        AssertOnlyEntryHashesChanged(original, ReadBytes(pak), 0);
    }

    [Fact]
    public void RewritePaths_EncryptedToc_RenamesInPlace()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "enc8.pak",
            8,
            PakCodecTestTools.Stored(EngineHash(ArmorPath), Encoding.ASCII.GetBytes("a")));
        byte[] original = ReadBytes(built.Path);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ArmorNewPath]);

        ModelRewriteReport report = CreateService().RewritePaths(built.Path, "012", "015", index);

        Assert.Equal(1, report.EntriesChanged);
        // TOC 仍加密，但解密后能看到新哈希；数据区不动。
        Assert.True(new RePakReader().Read(built.Path).IsTocEncrypted);
        Assert.Equal(EngineHash(ArmorNewPath), Assert.Single(new PakRepacker().Inspect(built.Path).Entries).Hash);
        Assert.True(File.Exists(built.Path + ".toc.bak"));
        byte[] updated = ReadBytes(built.Path);
        int dataStart = 16 + 48 + 128;
        AssertBytesEqual(original.AsSpan(dataStart).ToArray(), updated.AsSpan(dataStart).ToArray());
    }

    [Fact]
    public void RePakRenamer_EncryptedToc_RenamesInPlace()
    {
        using TempDir temp = TempDir.Create();
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "enc24.pak",
            24,
            PakCodecTestTools.Stored(EngineHash(ArmorPath), Encoding.ASCII.GetBytes("a")));
        byte[] original = ReadBytes(built.Path);
        const ulong NewHash = 0x1122334455667788UL;

        new RePakRenamer().RenameEntry(built.Path, 0, NewHash);

        (_, _, _, short feature, _, _) = PakCodecTestTools.ReadHeader(ReadBytes(built.Path));
        Assert.Equal((short)24, feature);
        Assert.Equal(NewHash, Assert.Single(new PakRepacker().Inspect(built.Path).Entries).Hash);
        byte[] updated = ReadBytes(built.Path);
        int dataStart = 16 + 48 + 4 + 128;
        AssertBytesEqual(original.AsSpan(dataStart).ToArray(), updated.AsSpan(dataStart).ToArray());
    }

    [Fact]
    public void RewritePaths_MixedCaseListPath_WritesEngineHashConvention()
    {
        using TempDir temp = TempDir.Create();
        // 内置游戏清单保留原始大小写（natives/STM/Art/...），MOD pak 内路径通常是小写。
        const string listPath = "natives/STM/Art/Model/Character/ch02/012/003/ch02_012_003.mdf2.45";
        const string newListPath = "natives/STM/Art/Model/Character/ch02/015/003/ch02_012_003.mdf2.45";
        string pak = BuildPak(temp.Path, [(listPath.ToLowerInvariant(), Encoding.ASCII.GetBytes("a"))]);
        PakNameIndex index = PakPathHasher.BuildIndex([listPath]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "012", "015", index);

        Assert.Equal(1, report.EntriesChanged);
        // 低 32 位必须是 hash(小写路径)，否则游戏按新路径找不到条目。
        Assert.Equal(EngineHash(newListPath), Assert.Single(new RePakReader().Read(pak).Entries).Hash);
    }

    [Fact]
    public void RewritePaths_UnmatchedId_NoChanges_NoBackup()
    {
        using TempDir temp = TempDir.Create();
        string pak = BuildPak(temp.Path, [(ArmorPath, Encoding.ASCII.GetBytes("a"))]);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorPath, ArmorNewPath]);
        byte[] original = ReadBytes(pak);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "999", "015", index);

        Assert.Equal(0, report.EntriesChanged);
        AssertBytesEqual(original, ReadBytes(pak));
        Assert.False(File.Exists(pak + ".toc.bak"));
    }

    // ------------------------------------------------- ModelIdRewriter.TryRewritePath

    [Fact]
    public void TryRewritePath_RewritesCharacterAndItemIdSlots()
    {
        Assert.True(ModelIdRewriter.TryRewritePath(
            "natives/STM/Art/Model/Character/ch03/012/003/ch03_012_003.mdf2.45",
            "012",
            "015",
            out string armor));
        Assert.Equal("natives/STM/Art/Model/Character/ch03/015/003/ch03_012_003.mdf2.45", armor);

        Assert.True(ModelIdRewriter.TryRewritePath(
            "natives/STM/Art/Model/Item/it10/10/0000/it1010_0000_0.mdf2.45",
            "0000",
            "0001",
            out string item));
        Assert.Equal("natives/STM/Art/Model/Item/it10/10/0001/it1010_0000_0.mdf2.45", item);
    }

    [Fact]
    public void TryRewritePath_DoesNotRewriteSidSegmentThatCoincidesWithId()
    {
        const string path = "natives/STM/Art/Model/Character/ch03/010/012/ch03_010_012.mdf2.45";

        Assert.False(ModelIdRewriter.TryRewritePath(path, "012", "015", out string rewritten));
        Assert.Equal(path, rewritten);
    }

    [Fact]
    public void TryRewritePath_SameId_ReturnsFalse()
    {
        Assert.False(ModelIdRewriter.TryRewritePath(ArmorPath, "012", "012", out string rewritten));
        Assert.Equal(ArmorPath, rewritten);
    }

    [Fact]
    public void TryRewritePath_UnknownShape_UsesUniqueSegment()
    {
        Assert.True(ModelIdRewriter.TryRewritePath(
            "reframework/data/bonesystem/012/ch03.bin",
            "012",
            "015",
            out string rewritten));
        Assert.Equal("reframework/data/bonesystem/015/ch03.bin", rewritten);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12345")]
    [InlineData("abc")]
    public void TryRewritePath_InvalidId_Throws(string id)
    {
        Assert.Throws<ArgumentException>(() =>
            ModelIdRewriter.TryRewritePath(ArmorPath, id, "015", out _));
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] ReadEntryPayload(string pakPath, int entryIndex)
    {
        PakEntryInfo entry = new RePakReader().Read(pakPath).Entries[entryIndex];
        return new RePakEntryReader().ReadEntry(pakPath, entry);
    }
}
