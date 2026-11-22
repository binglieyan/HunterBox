using System.Text;
using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>Infrastructure 的 pak 条目路径读取端口实现：真实 v4 pak 解析 + 内置路径清单还原。</summary>
public class PakEntryPathReaderTests
{
    private const string KnownPath = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";

    [Fact]
    public void TryReadEntryPaths_ResolvesKnownHashesAndSkipsUnknown()
    {
        using TempDir temp = TempDir.Create();
        const ulong KnownHash = 0x1122_3344_5566_7788UL;
        const ulong UnknownHash = 0x9999_AAAA_BBBB_CCCCUL;
        string pakPath = Path.Combine(temp.Path, "mod.pak");
        WriteV4Pak(pakPath, HashParts(KnownHash), HashParts(UnknownHash));
        PakNameIndex index = new();
        index.Add(KnownHash, KnownPath);

        IReadOnlyList<string>? entries = new PakEntryPathReader()
            .TryReadEntryPaths(pakPath, "mod.pak", index, out string? reason);

        Assert.Null(reason);
        Assert.Equal([KnownPath], entries);
    }

    [Fact]
    public void TryReadEntryPaths_MissingFile_ReturnsReason()
    {
        using TempDir temp = TempDir.Create();
        string pakPath = Path.Combine(temp.Path, "missing.pak");

        IReadOnlyList<string>? entries = new PakEntryPathReader()
            .TryReadEntryPaths(pakPath, "missing.pak", new PakNameIndex(), out string? reason);

        Assert.Null(entries);
        Assert.Equal("文件不存在", reason);
    }

    [Fact]
    public void TryReadEntryPaths_InvalidPak_ReturnsParseReason()
    {
        using TempDir temp = TempDir.Create();
        string pakPath = temp.File("bad.pak", "not a pak");

        IReadOnlyList<string>? entries = new PakEntryPathReader()
            .TryReadEntryPaths(pakPath, "bad.pak", new PakNameIndex(), out string? reason);

        Assert.Null(entries);
        Assert.NotNull(reason);
        Assert.StartsWith("无法解析：", reason, StringComparison.Ordinal);
    }

    /// <summary>明文 TOC 半写损坏时，读取器应释放预检句柄并用已提交前像恢复后重读。</summary>
    [Fact]
    public void TryReadEntryPaths_CorruptPlainTocWithCommittedBackup_RecoversAndReturnsEntries()
    {
        using TempDir temp = TempDir.Create();
        const ulong KnownHash = 0x1122_3344_5566_7788UL;
        string pakPath = Path.Combine(temp.Path, "mod.pak");
        WriteV4PakWithData(pakPath, (KnownHash, Encoding.ASCII.GetBytes("payload")));

        PakTocPreImage.Snapshot snapshot = CaptureSnapshot(pakPath);
        PakTocPreImage.WritePending(pakPath, snapshot);
        PakTocPreImage.Commit(pakPath);

        // 模拟「TOC 写到一半」：条目字段被写坏，包头与数据区保持不变。
        byte[] broken = File.ReadAllBytes(pakPath);
        broken.AsSpan(PakPlainToc.HeaderSize, PakEntryLayout.V4EntrySize).Fill(0xFF);
        File.WriteAllBytes(pakPath, broken);

        PakNameIndex index = new();
        index.Add(KnownHash, KnownPath);
        IReadOnlyList<string>? entries = new PakEntryPathReader()
            .TryReadEntryPaths(pakPath, "mod.pak", index, out string? reason);

        Assert.Null(reason);
        Assert.Equal([KnownPath], entries);
        Assert.True(File.Exists(pakPath + PakTocPreImage.BackupSuffix));
    }

    /// <summary>清零型半写 TOC（全 0）同样必须触发前像恢复，而不是返回空/错误条目。</summary>
    [Fact]
    public void TryReadEntryPaths_ZeroedPlainTocWithCommittedBackup_RecoversAndReturnsEntries()
    {
        using TempDir temp = TempDir.Create();
        const ulong KnownHash = 0x1122_3344_5566_7788UL;
        string pakPath = Path.Combine(temp.Path, "mod.pak");
        WriteV4PakWithData(pakPath, (KnownHash, Encoding.ASCII.GetBytes("payload")));

        PakTocPreImage.Snapshot snapshot = CaptureSnapshot(pakPath);
        PakTocPreImage.WritePending(pakPath, snapshot);
        PakTocPreImage.Commit(pakPath);

        byte[] broken = File.ReadAllBytes(pakPath);
        broken.AsSpan(PakPlainToc.HeaderSize, PakEntryLayout.V4EntrySize).Clear();
        File.WriteAllBytes(pakPath, broken);

        PakNameIndex index = new();
        index.Add(KnownHash, KnownPath);
        IReadOnlyList<string>? entries = new PakEntryPathReader()
            .TryReadEntryPaths(pakPath, "mod.pak", index, out string? reason);

        Assert.Null(reason);
        Assert.Equal([KnownPath], entries);
    }

    [Fact]
    public void LoadNameIndex_LoadsEmbeddedListsPerGame()
    {
        PakEntryPathReader reader = new();

        Assert.True(reader.LoadNameIndex(ModelGame.Wilds).Count > 40_000);
        Assert.True(reader.LoadNameIndex(ModelGame.Rise).Count > 0);
        Assert.Equal(0, reader.LoadNameIndex(ModelGame.World).Count);
        Assert.True(reader.LoadNameIndex(ModelGame.Unknown).Count > 40_000);
    }

    /// <summary>把 64 位组合哈希拆成 pak 条目里的高低 32 位。</summary>
    private static (uint Lower, uint Upper) HashParts(ulong hash) =>
        ((uint)(hash & 0xFFFF_FFFFUL), (uint)(hash >> 32));

    /// <summary>写一个只含包头 + 条目表的 v4 pak（读取端口只解析 TOC，不需要数据区）。</summary>
    private static void WriteV4Pak(string path, params (uint Lower, uint Upper)[] entries)
    {
        using MemoryStream memory = new();
        using (BinaryWriter writer = new(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(RePakReader.Magic);
            writer.Write((byte)4);
            writer.Write((byte)0);
            writer.Write((short)0);
            writer.Write(entries.Length);
            writer.Write(0x1234_5678u);
            foreach ((uint lower, uint upper) in entries)
            {
                writer.Write(lower);
                writer.Write(upper);
                writer.Write(0L); // offset
                writer.Write(0L); // compressed size
                writer.Write(0L); // decompressed size
                writer.Write(0L); // attributes
                writer.Write(0UL); // checksum
            }
        }

        File.WriteAllBytes(path, memory.ToArray());
    }

    /// <summary>写一个含包头 + 条目表 + 数据区的 v4 pak（前像恢复用例需要可校验的数据区采样）。</summary>
    private static void WriteV4PakWithData(string path, params (ulong Hash, byte[] Data)[] entries)
    {
        using MemoryStream memory = new();
        using (BinaryWriter writer = new(memory, Encoding.UTF8, leaveOpen: true))
        {
            long offset = PakPlainToc.HeaderSize + ((long)entries.Length * PakEntryLayout.V4EntrySize);
            writer.Write(RePakReader.Magic);
            writer.Write((byte)4);
            writer.Write((byte)0);
            writer.Write((short)0);
            writer.Write(entries.Length);
            writer.Write(0x1234_5678u);
            foreach ((ulong hash, byte[] data) in entries)
            {
                writer.Write((uint)(hash & 0xFFFF_FFFFUL));
                writer.Write((uint)(hash >> 32));
                writer.Write(offset);
                writer.Write(0L); // compressed size
                writer.Write((long)data.Length);
                writer.Write(0L); // attributes
                writer.Write(0UL); // checksum
                offset += data.Length;
            }

            foreach ((_, byte[] data) in entries)
            {
                writer.Write(data);
            }
        }

        File.WriteAllBytes(path, memory.ToArray());
    }

    /// <summary>按当前 pak 内容构造一份前像快照（与 PakTocPreImage 写入前捕获的口径一致）。</summary>
    private static PakTocPreImage.Snapshot CaptureSnapshot(string pakPath)
    {
        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        byte[] toc = PakPlainToc.ReadToc(stream, header);
        return new PakTocPreImage.Snapshot(
            PakTocPreImage.CurrentVersion,
            stream.Length,
            header.Major,
            header.Minor,
            header.Feature,
            header.Count,
            PakTocPreImage.ComputeDataSample(stream, PakTocPreImage.DataStart(header)),
            toc);
    }
}
