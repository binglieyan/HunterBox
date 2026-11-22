using System.Text;
using HunterBox.Application.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class RePakWriterTests
{
    private const string EntryPath = "natives/stm/entry.bin";

    private static (string Path, byte[] Payload) BuildSingleEntryPak(int compressionType = 0, ushort encryptionType = 0)
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdef");
        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory);

        writer.Write(RePakReader.Magic);
        writer.Write((byte)4);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(1);
        writer.Write(0u);
        writer.Write(0x1u);
        writer.Write(0x2u);
        writer.Write(64L);
        writer.Write((long)payload.Length);
        writer.Write((long)payload.Length);
        writer.Write((long)(compressionType | (encryptionType << 16)));
        writer.Write(0uL);
        writer.Write(payload);

        string path = Path.Combine(Path.GetTempPath(), "hb-write-" + Guid.NewGuid().ToString("N") + ".pak");
        File.WriteAllBytes(path, memory.ToArray());
        return (path, payload);
    }

    /// <summary>用重打包器生成一个条目内容合法的 pak（v4 会写入真实 checksum）。</summary>
    private static string BuildPakFromContent(TempDir temp, byte major, string tag, byte[] content)
    {
        string source = temp.Dir("src-" + tag);
        PakCodecTestTools.WriteSourceFile(source, EntryPath, content);
        string pak = Path.Combine(temp.Path, $"{tag}.pak");
        new PakRepacker().Pack(
            source,
            pak,
            new PakRepackProfile { Major = major, Minor = 0, Feature = 0 },
            CancellationToken.None);
        return pak;
    }

    [Fact]
    public void ReplaceEntry_SameSize_UpdatesContent()
    {
        (string path, byte[] original) = BuildSingleEntryPak();
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);
            byte[] replacement = Encoding.UTF8.GetBytes("FEDCBA9876543210");

            new RePakWriter().ReplaceEntry(path, entry, replacement);

            Assert.Equal(replacement, new RePakEntryReader().ReadEntry(path, entry));
            Assert.True(File.Exists(path + ".bak"), "应自动生成备份");
            Assert.Equal(original, File.ReadAllBytes(path + ".bak").AsSpan(64, original.Length).ToArray());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    /// <summary>v4 回归：等长替换必须同步重算条目 checksum（checksum 是内容摘要，不是布局的一部分）。</summary>
    [Fact]
    public void ReplaceEntry_V4_RecomputesChecksum()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        byte[] replacement = Encoding.ASCII.GetBytes("FEDCBA9876543210");
        string pak = BuildPakFromContent(temp, major: 4, tag: "old", original);
        string reference = BuildPakFromContent(temp, major: 4, tag: "new", replacement);

        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
        ulong expectedChecksum = Assert.Single(new RePakReader().Read(reference).Entries).Checksum;
        Assert.NotEqual(entry.Checksum, expectedChecksum);

        new RePakWriter().ReplaceEntry(pak, entry, replacement, backup: false);

        PakEntryInfo updated = Assert.Single(new RePakReader().Read(pak).Entries);
        Assert.Equal(expectedChecksum, updated.Checksum);
        Assert.Equal(entry.Offset, updated.Offset);
        Assert.Equal(entry.DecompressedSize, updated.DecompressedSize);
        Assert.Equal(replacement, new RePakEntryReader().ReadEntry(pak, updated));
    }

    /// <summary>v2 条目表没有 checksum 字段：等长替换只改数据区，不能往条目表里写东西。</summary>
    [Fact]
    public void ReplaceEntry_V2_LeavesEntryTableUntouched()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        byte[] replacement = Encoding.ASCII.GetBytes("FEDCBA9876543210");
        string pak = BuildPakFromContent(temp, major: 2, tag: "v2", original);
        byte[] before = File.ReadAllBytes(pak);
        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);

        new RePakWriter().ReplaceEntry(pak, entry, replacement, backup: false);

        byte[] after = File.ReadAllBytes(pak);
        Assert.Equal(before.Length, after.Length);
        // v2 包头 + 24 字节条目表：整个 TOC 必须原样不动。
        Assert.Equal(before.AsSpan(0, 16 + 24).ToArray(), after.AsSpan(0, 16 + 24).ToArray());
        Assert.Equal(replacement, new RePakEntryReader().ReadEntry(pak, entry));
    }

    /// <summary>N-22：v2 没有 checksum 定位，仍必须拒绝不属于该 pak 的条目，不能按传入 offset 覆盖相邻数据。</summary>
    [Fact]
    public void ReplaceEntry_V2_ForeignEntry_LeavesPakUntouched()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        string pak = BuildPakFromContent(temp, major: 2, tag: "v2-foreign", original);
        byte[] before = File.ReadAllBytes(pak);
        PakEntryInfo foreign = new(
            HashLower: 0xDEAD_BEEFu,
            HashUpper: 0xDEAD_BEEFu,
            Offset: 16 + 24,
            CompressedSize: original.LongLength,
            DecompressedSize: original.LongLength,
            Attributes: 0L,
            Checksum: 0uL);

        Assert.Throws<InvalidDataException>(() =>
            new RePakWriter().ReplaceEntry(pak, foreign, Encoding.ASCII.GetBytes("FEDCBA9876543210")));

        Assert.Equal(before, File.ReadAllBytes(pak));
    }

    /// <summary>TOC 加密时无法只写明文 checksum：必须拒绝，而不是把 pak 写成半明文半密文。</summary>
    [Fact]
    public void ReplaceEntry_TocEncrypted_Throws()
    {
        using TempDir temp = TempDir.Create();
        const ulong hash = 0x0123_4567_89AB_CDEFuL;
        byte[] content = Encoding.ASCII.GetBytes("0123456789abcdef");
        string pak = PakCodecTestTools.BuildPak(
            temp.Path,
            "enc.pak",
            8,
            PakCodecTestTools.Stored(hash, content)).Path;
        // PakRepacker.Inspect 会先解密 TOC，拿到的是真实条目字段。
        PakEntryInfo entry = Assert.Single(new PakRepacker().Inspect(pak).Entries);
        byte[] replacement = Encoding.ASCII.GetBytes("FEDCBA9876543210");

        Assert.Throws<NotSupportedException>(() =>
            new RePakWriter().ReplaceEntry(pak, entry, replacement, backup: false));
    }

    [Fact]
    public void ReplaceEntry_DifferentLength_Throws()
    {
        (string path, _) = BuildSingleEntryPak();
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);

            Assert.Throws<NotSupportedException>(() =>
                new RePakWriter().ReplaceEntry(path, entry, Encoding.UTF8.GetBytes("too-short")));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void ReplaceEntry_Encrypted_Throws()
    {
        (string path, byte[] payload) = BuildSingleEntryPak(encryptionType: 1);
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);

            Assert.Throws<NotSupportedException>(() =>
                new RePakWriter().ReplaceEntry(path, entry, payload));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void ReplaceEntry_Compressed_Throws()
    {
        (string path, byte[] payload) = BuildSingleEntryPak(compressionType: 1);
        try
        {
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(path).Entries);

            Assert.Throws<NotSupportedException>(() =>
                new RePakWriter().ReplaceEntry(path, entry, payload));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    private static void AssertNoTemporaryArtifacts(TempDir temp) =>
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));

    /// <summary>
    /// 失败注入：条目不在条目表里时 checksum 定位失败。旧实现先写数据再定位，会把 pak 写成
    /// 「新数据 + 旧 checksum」；事务化实现必须在动笔前失败，原 pak 一个字节都不能变。
    /// </summary>
    [Fact]
    public void ReplaceEntry_ChecksumLocateFailure_LeavesPakUntouched()
    {
        (string path, byte[] payload) = BuildSingleEntryPak();
        try
        {
            byte[] before = File.ReadAllBytes(path);
            PakEntryInfo foreign = new(
                HashLower: 0xDEAD_BEEFu,
                HashUpper: 0xDEAD_BEEFu,
                Offset: 64L,
                CompressedSize: payload.LongLength,
                DecompressedSize: payload.LongLength,
                Attributes: 0L,
                Checksum: 0uL);

            Assert.Throws<InvalidDataException>(() =>
                new RePakWriter().ReplaceEntry(path, foreign, Encoding.ASCII.GetBytes("FEDCBA9876543210")));

            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    /// <summary>已有 .bak 时不再重建/覆盖，写回仍应成功。</summary>
    [Fact]
    public void ReplaceEntry_ExistingBackup_IsKeptAndPakUpdated()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        string pak = BuildPakFromContent(temp, major: 4, tag: "bak", original);
        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
        string backupPath = pak + ".bak";
        byte[] oldBackup = Encoding.ASCII.GetBytes("VALID-OLD-BACKUP");
        File.WriteAllBytes(backupPath, oldBackup);
        byte[] replacement = Encoding.ASCII.GetBytes("FEDCBA9876543210");

        new RePakWriter().ReplaceEntry(pak, entry, replacement);

        Assert.Equal(oldBackup, File.ReadAllBytes(backupPath));
        Assert.Equal(replacement, new RePakEntryReader().ReadEntry(pak, entry));
        AssertNoTemporaryArtifacts(temp);
    }

    /// <summary>
    /// 失败注入：最终原子替换失败（目标被设为只读）。原 pak 必须保持原样，临时副本被清理。
    /// </summary>
    [Fact]
    public void ReplaceEntry_CommitFailure_KeepsPakUnchanged()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        string pak = BuildPakFromContent(temp, major: 4, tag: "readonly", original);
        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
        byte[] before = File.ReadAllBytes(pak);
        File.SetAttributes(pak, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() =>
                new RePakWriter().ReplaceEntry(pak, entry, Encoding.ASCII.GetBytes("FEDCBA9876543210")));

            Assert.Equal(before, File.ReadAllBytes(pak));
            AssertNoTemporaryArtifacts(temp);
        }
        finally
        {
            File.SetAttributes(pak, FileAttributes.Normal);
        }
    }

    /// <summary>.bak 只保留首次写回前的原始 pak，第二次替换不能覆盖它。</summary>
    [Fact]
    public void ReplaceEntry_SecondCall_KeepsOriginalBackup()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        string pak = BuildPakFromContent(temp, major: 4, tag: "refresh", original);
        string backupPath = pak + ".bak";
        byte[] beforeFirst = File.ReadAllBytes(pak);
        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);

        new RePakWriter().ReplaceEntry(pak, entry, Encoding.ASCII.GetBytes("1111111111111111"));
        Assert.Equal(beforeFirst, File.ReadAllBytes(backupPath));

        PakEntryInfo updated = Assert.Single(new RePakReader().Read(pak).Entries);
        new RePakWriter().ReplaceEntry(pak, updated, Encoding.ASCII.GetBytes("2222222222222222"));

        Assert.Equal(beforeFirst, File.ReadAllBytes(backupPath));
        Assert.Equal(
            Encoding.ASCII.GetBytes("2222222222222222"),
            new RePakEntryReader().ReadEntry(pak, updated));
        AssertNoTemporaryArtifacts(temp);
    }

    [Fact]
    public void ReplaceEntry_NoBackup_DoesNotCreateBackup()
    {
        using TempDir temp = TempDir.Create();
        byte[] original = Encoding.ASCII.GetBytes("0123456789abcdef");
        string pak = BuildPakFromContent(temp, major: 4, tag: "nobak", original);
        PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);

        new RePakWriter().ReplaceEntry(pak, entry, Encoding.ASCII.GetBytes("FEDCBA9876543210"), backup: false);

        Assert.False(File.Exists(pak + ".bak"));
        AssertNoTemporaryArtifacts(temp);

        // backup:false 时已有的旧备份也不能被动：既不覆盖也不删除。
        string backupPath = pak + ".bak";
        byte[] existingBackup = Encoding.ASCII.GetBytes("EXISTING-BACKUP");
        File.WriteAllBytes(backupPath, existingBackup);
        PakEntryInfo updated = Assert.Single(new RePakReader().Read(pak).Entries);
        new RePakWriter().ReplaceEntry(pak, updated, Encoding.ASCII.GetBytes("0123456789abcdef"), backup: false);

        Assert.Equal(existingBackup, File.ReadAllBytes(backupPath));
        AssertNoTemporaryArtifacts(temp);
    }
}
