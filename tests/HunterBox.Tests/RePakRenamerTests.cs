using System.Buffers.Binary;
using System.Text;
using HunterBox.Application.Models;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class RePakRenamerTests
{
    private const string Path012 = "natives/stm/art/model/character/ch03/012/body.mod3";
    private const string PathOther = "natives/stm/art/model/character/ch03/020/body.mod3";

    private static string BuildPak(params (ulong Hash, byte[] Content)[] entries)
    {
        const int header = 16;
        const int entrySize = 48;
        long dataOffset = header + (entrySize * entries.Length);

        using MemoryStream memory = new();
        using BinaryWriter writer = new(memory);
        writer.Write(RePakReader.Magic);
        writer.Write((byte)4);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write(entries.Length);
        writer.Write(0u);

        long offset = dataOffset;
        foreach ((ulong hash, byte[] content) in entries)
        {
            writer.Write((uint)(hash & 0xFFFFFFFF));
            writer.Write((uint)(hash >> 32));
            writer.Write(offset);
            writer.Write((long)content.Length);
            writer.Write((long)content.Length);
            writer.Write(0L);
            writer.Write(0uL);
            offset += content.Length;
        }

        foreach ((_, byte[] content) in entries)
        {
            writer.Write(content);
        }

        string path = Path.Combine(Path.GetTempPath(), "hb-rename-" + Guid.NewGuid().ToString("N") + ".pak");
        File.WriteAllBytes(path, memory.ToArray());
        return path;
    }

    /// <summary>断言只有指定条目的哈希字段发生变化，其余字节（含 offset/大小/checksum/数据区）完全不变。</summary>
    private static void AssertOnlyHashFieldChanged(
        byte[] before,
        byte[] after,
        byte major,
        int entryIndex,
        ulong expectedHash)
    {
        Assert.Equal(before.Length, after.Length);

        int entrySize = major == 2 ? 24 : 48;
        int hashFieldOffset = major == 2 ? 16 : 0;
        int start = 16 + (entryIndex * entrySize) + hashFieldOffset;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i])
            {
                continue;
            }

            Assert.True(
                i >= start && i < start + 8,
                $"条目 {entryIndex} 之外的字节被改写：offset={i}（允许范围 {start}–{start + 7}）");
        }

        Assert.Equal(
            BitConverter.GetBytes((uint)(expectedHash & 0xFFFFFFFF)),
            after.AsSpan(start, 4).ToArray());
        Assert.Equal(
            BitConverter.GetBytes((uint)(expectedHash >> 32)),
            after.AsSpan(start + 4, 4).ToArray());
    }

    /// <summary>
    /// N-04：备份重建失败必须发生在提交点之前，原 pak 的 TOC 不能出现半新半旧。
    /// 用「.toc.bak 路径被目录占住」制造备份阶段失败，验证原文件字节完全不变。
    /// </summary>
    [Fact]
    public void RenameEntries_BackupCommitFailure_LeavesPakUntouched()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        string pak = BuildPak((PakPathHasher.HashPath(Path012), content));
        string backupPath = pak + ".toc.bak";
        try
        {
            byte[] before = File.ReadAllBytes(pak);
            Directory.CreateDirectory(backupPath);

            Exception? failure = Record.Exception(() =>
                new RePakRenamer().RenameEntries(
                    pak,
                    [new PakEntryRename(0, PakPathHasher.HashPath(PathOther))]));

            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Equal(before, File.ReadAllBytes(pak));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(pak)!,
                "." + Path.GetFileName(pak) + ".*.tmp"));
        }
        finally
        {
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath);
            }

            File.Delete(pak);
            File.Delete(backupPath);
        }
    }

    /// <summary>.toc.bak 保存「上一次成功改名」前的条目表，并带 pak 指纹（只作用于同一 pak）。</summary>
    [Fact]
    public void RenameEntries_SecondCall_KeepsLastPreImage()
    {
        byte[] firstContent = Encoding.ASCII.GetBytes("first-payload");
        byte[] secondContent = Encoding.ASCII.GetBytes("second-payload");
        string pak = BuildPak(
            (PakPathHasher.HashPath(Path012), firstContent),
            (PakPathHasher.HashPath(PathOther), secondContent));
        try
        {
            int tocEnd = 16 + (2 * 48);
            byte[] original = File.ReadAllBytes(pak);
            RePakRenamer renamer = new();
            renamer.RenameEntries(pak, [new PakEntryRename(0, PakPathHasher.HashPath(PathOther))]);
            byte[] tocBeforeSecond = File.ReadAllBytes(pak).AsSpan(16, tocEnd - 16).ToArray();

            renamer.RenameEntries(pak, [new PakEntryRename(1, PakPathHasher.HashPath(Path012))]);

            Assert.Equal(
                PakTocPreImage.ReadOutcome.Ok,
                PakTocPreImage.ReadSnapshot(pak + ".toc.bak", out PakTocPreImage.Snapshot snapshot));
            Assert.Equal(tocBeforeSecond, snapshot.Toc);
            Assert.Equal(new FileInfo(pak).Length, snapshot.PakLength);
            // 数据区从不被改名修改：前像 TOC + 当前数据区就是上一次改名前的 pak。
            Assert.Equal(original.AsSpan(tocEnd).ToArray(), File.ReadAllBytes(pak).AsSpan(tocEnd).ToArray());
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    [Fact]
    public void RenameEntry_ChangesOnlyHashBytes()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong oldHash = PakPathHasher.HashPath(Path012);
        ulong newHash = PakPathHasher.HashPath(PathOther);
        string pak = BuildPak((oldHash, content));
        try
        {
            new RePakRenamer().RenameEntry(pak, 0, newHash);

            PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
            Assert.Equal(newHash, entry.Hash);
            Assert.Equal(64L, entry.Offset);
            Assert.Equal(content.Length, entry.DecompressedSize);
            Assert.Equal(content, new RePakEntryReader().ReadEntry(pak, entry));
            Assert.True(File.Exists(pak + ".toc.bak"), "改名应在写回前生成 TOC 前像");
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>v4 回归：哈希在条目起始（+0），改名只能改这 8 字节，offset/size/checksum 与数据区不动。</summary>
    [Fact]
    public void RenameEntry_V4_OnlyHashFieldChanged_AndStillUnpackable()
    {
        using TempDir temp = TempDir.Create();
        byte[] first = Encoding.ASCII.GetBytes("first-payload");
        byte[] second = PakCodecTestTools.CreateBytes(64, seed: 21);
        ulong oldHash = PakPathHasher.HashPath(Path012);
        ulong newHash = PakPathHasher.HashPath(PathOther);
        string source = temp.Dir("src");
        PakCodecTestTools.WriteSourceFile(source, Path012, first);
        PakCodecTestTools.WriteSourceFile(source, PathOther + ".b", second);
        string pak = Path.Combine(temp.Path, "v4.pak");
        new PakRepacker().Pack(source, pak, new PakRepackProfile { Major = 4, Minor = 0, Feature = 0 }, CancellationToken.None);

        byte[] before = File.ReadAllBytes(pak);
        PakArchiveInfo original = new RePakReader().Read(pak);
        int index = IndexOf(original.Entries, oldHash);
        Assert.True(original.Entries[index].Checksum != 0, "v4 打包后 checksum 应非 0，这个断言才有意义");

        new RePakRenamer().RenameEntry(pak, index, newHash, backup: false);

        byte[] after = File.ReadAllBytes(pak);
        AssertOnlyHashFieldChanged(before, after, major: 4, index, newHash);
        Assert.Equal(before.Length, after.Length);

        PakArchiveInfo archive = new RePakReader().Read(pak);
        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Offset, archive.Entries[i].Offset);
            Assert.Equal(original.Entries[i].CompressedSize, archive.Entries[i].CompressedSize);
            Assert.Equal(original.Entries[i].DecompressedSize, archive.Entries[i].DecompressedSize);
            Assert.Equal(original.Entries[i].Attributes, archive.Entries[i].Attributes);
            Assert.Equal(original.Entries[i].Checksum, archive.Entries[i].Checksum);
        }

        Assert.Equal(newHash, archive.Entries[index].Hash);
        // 数据区仍可读：offset/size/checksum 没被写坏。
        Assert.Equal(first, new RePakEntryReader().ReadEntry(pak, archive.Entries[index]));
    }

    /// <summary>v2 回归：哈希在 offset/decompressedSize 之后（条目内 +16），绝不能写到条目起始。</summary>
    [Fact]
    public void RenameEntry_V2_OnlyHashFieldChanged_AndStillUnpackable()
    {
        using TempDir temp = TempDir.Create();
        byte[] first = Encoding.ASCII.GetBytes("v2-first-payload");
        byte[] second = PakCodecTestTools.CreateBytes(48, seed: 22);
        ulong oldHash = PakPathHasher.HashPath(Path012);
        ulong otherHash = PakPathHasher.HashPath(PathOther + ".b");
        ulong newHash = PakPathHasher.HashPath(PathOther);
        string pak = PakCodecTestTools.BuildV2Pak(
            temp.Path,
            "v2.pak",
            (oldHash, first),
            (otherHash, second));
        byte[] before = File.ReadAllBytes(pak);
        PakArchiveInfo original = new RePakReader().Read(pak);
        int index = IndexOf(original.Entries, oldHash);

        new RePakRenamer().RenameEntry(pak, index, newHash, backup: false);

        AssertOnlyHashFieldChanged(before, File.ReadAllBytes(pak), major: 2, index, newHash);
        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(2, archive.Major);
        Assert.Equal(newHash, archive.Entries[index].Hash);
        Assert.Equal(original.Entries[index].Offset, archive.Entries[index].Offset);
        Assert.Equal(original.Entries[index].DecompressedSize, archive.Entries[index].DecompressedSize);
        Assert.Equal(original.Entries[(index + 1) % 2].Offset, archive.Entries[(index + 1) % 2].Offset);
        // 数据区仍可读：若 offset 被写坏，这里会读出错误内容或越界。
        Assert.Equal(first, new RePakEntryReader().ReadEntry(pak, archive.Entries[index]));
    }

    [Fact]
    public void RewritePaths_OldId_RenamesMatchingSegmentOnly()
    {
        ulong target = PakPathHasher.HashPath(Path012);
        ulong other = PakPathHasher.HashPath(PathOther);
        string pak = BuildPak((target, Encoding.ASCII.GetBytes("a")), (other, Encoding.ASCII.GetBytes("b")));
        try
        {
            PakNameIndex index = PakPathHasher.BuildIndex([Path012, PathOther]);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            ModelRewriteReport report = service.RewritePaths(pak, "012", "015", index);

            Assert.Equal(1, report.EntriesChanged);
            HashSet<ulong> hashes = [.. new RePakReader().Read(pak).Entries.Select(e => e.Hash)];
            Assert.Contains(PakPathHasher.HashPath("natives/stm/art/model/character/ch03/015/body.mod3"), hashes);
            Assert.Contains(other, hashes);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>
    /// 同一 pak 的并发改名由进程内路径锁串行化——所有改动都保留，TOC 不会被写撕裂。
    /// </summary>
    [Fact]
    public void RenameEntries_ConcurrentSamePak_KeepsEveryChange()
    {
        const int Count = 8;
        (ulong Hash, byte[] Content)[] entries = new (ulong, byte[])[Count];
        for (int i = 0; i < Count; i++)
        {
            entries[i] = (
                PakPathHasher.HashPath($"{Path012}.{i}"),
                Encoding.ASCII.GetBytes("concurrent-" + i));
        }

        string pak = BuildPak(entries);
        ulong[] targets = [.. entries.Select((_, i) => PakPathHasher.HashPath($"{PathOther}.{i}"))];
        try
        {
            Parallel.For(0, Count, i =>
                new RePakRenamer().RenameEntries(pak, [new PakEntryRename(i, targets[i])]));

            PakArchiveInfo archive = new RePakReader().Read(pak);
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal(targets[i], archive.Entries[i].Hash);
                Assert.Equal(entries[i].Content, new RePakEntryReader().ReadEntry(pak, archive.Entries[i]));
            }
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>未提交写入日志存在（模拟进程被杀/断电）时自动恢复原始条目表。</summary>
    [Fact]
    public void TryRecover_PendingJournal_RestoresInterruptedToc()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.Snapshot snapshot = CaptureTocSnapshot(pak);
            // 模拟「TOC 写到一半」：条目表被写坏，但未提交日志还在。
            byte[] broken = File.ReadAllBytes(pak);
            broken.AsSpan(16, 48).Fill(0xFF);
            File.WriteAllBytes(pak, broken);
            PakTocPreImage.WritePending(pak, snapshot);

            bool recovered = new RePakRenamer().TryRecover(pak, out string message);

            Assert.True(recovered);
            Assert.False(string.IsNullOrEmpty(message));
            Assert.False(File.Exists(pak + PakTocPreImage.PendingSuffix));
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
            Assert.Equal(hash, entry.Hash);
            Assert.Equal(content, new RePakEntryReader().ReadEntry(pak, entry));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>前像指纹与当前 pak 不符（pak 已被替换）时拒绝恢复，只丢弃日志。</summary>
    [Fact]
    public void TryRecover_PendingJournalForDifferentPak_IsDiscarded()
    {
        byte[] contentA = Encoding.ASCII.GetBytes("payload-A");
        byte[] contentB = Encoding.ASCII.GetBytes("payload-B");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pakA = BuildPak((hash, contentA));
        string pakB = BuildPak((hash, contentB));
        try
        {
            PakTocPreImage.Snapshot snapshotA = CaptureTocSnapshot(pakA);
            byte[] before = File.ReadAllBytes(pakB);
            // pak B 与 A 长度相同但数据区不同：把 A 的日志放到 B 旁边不能覆盖 B。
            PakTocPreImage.WritePending(pakB, snapshotA);

            Assert.False(new RePakRenamer().TryRecover(pakB, out _));

            Assert.False(File.Exists(pakB + PakTocPreImage.PendingSuffix));
            Assert.Equal(before, File.ReadAllBytes(pakB));
        }
        finally
        {
            File.Delete(pakA);
            File.Delete(pakB);
            File.Delete(pakA + PakTocPreImage.PendingSuffix);
            File.Delete(pakB + PakTocPreImage.PendingSuffix);
            File.Delete(pakA + PakTocPreImage.BackupSuffix);
            File.Delete(pakB + PakTocPreImage.BackupSuffix);
        }
    }

    /// <summary>已提交的 .toc.bak 在条目表结构损坏时自动恢复；正常 pak 不会被回退。</summary>
    [Fact]
    public void TryRecover_CommittedBackup_RestoresOnlyBrokenToc()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.WritePending(pak, CaptureTocSnapshot(pak));
            PakTocPreImage.Commit(pak);

            // 条目表仍合法：不能把正常 pak 回退到旧快照。
            Assert.False(new RePakRenamer().TryRecover(pak, out _));

            // 破坏条目偏移（结构非法），此时前像才应生效。
            byte[] broken = File.ReadAllBytes(pak);
            broken.AsSpan(16 + 8, 8).Fill(0xFF);
            File.WriteAllBytes(pak, broken);

            Assert.True(new RePakRenamer().TryRecover(pak, out _));
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(pak).Entries);
            Assert.Equal(hash, entry.Hash);
            Assert.Equal(64L, entry.Offset);
            Assert.Equal(content, new RePakEntryReader().ReadEntry(pak, entry));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>pak 被占用导致恢复失败时，未提交日志必须保留，解锁后仍能恢复。</summary>
    [Fact]
    public void TryRecover_PakLocked_KeepsPendingJournal()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.Snapshot snapshot = CaptureTocSnapshot(pak);
            byte[] broken = File.ReadAllBytes(pak);
            broken.AsSpan(16, 48).Fill(0xFF);
            File.WriteAllBytes(pak, broken);
            PakTocPreImage.WritePending(pak, snapshot);

            using (new FileStream(pak, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => new RePakRenamer().TryRecover(pak, out _));
            }

            Assert.True(File.Exists(pak + PakTocPreImage.PendingSuffix), "IO 失败不能删除唯一的恢复日志");
            Assert.True(new RePakRenamer().TryRecover(pak, out _));
            Assert.Equal(hash, Assert.Single(new RePakReader().Read(pak).Entries).Hash);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>feature 0x10（无加密 TOC）的数据区起点包含 4 字节附加字，前像指纹必须一致。</summary>
    [Fact]
    public void TryRecover_Feature16Pak_RestoresFromBackup()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakPathHasher.HashPath(Path012);
        ulong newHash = PakPathHasher.HashPath(PathOther);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "feature16.pak",
            feature: 16,
            PakCodecTestTools.Stored(hash, Encoding.ASCII.GetBytes("payload")));
        try
        {
            new RePakRenamer().RenameEntry(built.Path, 0, newHash);
            Assert.True(File.Exists(built.Path + PakTocPreImage.BackupSuffix));

            // 破坏条目偏移，让当前 TOC 结构非法；只有指纹正确的 .toc.bak 才能恢复。
            byte[] broken = File.ReadAllBytes(built.Path);
            broken.AsSpan(16 + 8, 8).Fill(0xFF);
            File.WriteAllBytes(built.Path, broken);

            Assert.True(new RePakRenamer().TryRecover(built.Path, out _));
            PakEntryInfo entry = Assert.Single(new RePakReader().Read(built.Path).Entries);
            Assert.Equal(hash, entry.Hash);
            Assert.Equal(16L + 48 + 4, entry.Offset);
        }
        finally
        {
            File.Delete(built.Path);
            File.Delete(built.Path + PakTocPreImage.BackupSuffix);
            File.Delete(built.Path + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>并行解密多块同时失败时统一抛 <see cref="InvalidDataException"/>。</summary>
    [Fact]
    public void ResourceDecryptionStream_MultipleBadBlocks_ThrowsInvalidData()
    {
        byte[] payload = PakCodecTestTools.CreateBytes(8 * 600, seed: 9);
        byte[] encrypted = PakCodecTestTools.EncryptResource(payload);
        // 破坏前两个块的 64 字节密钥大数：ModPow(0) = 0，解密必失败。
        encrypted.AsSpan(8, 64).Clear();
        encrypted.AsSpan(8 + 128, 64).Clear();
        using MemoryStream source = new(encrypted, writable: false);
        using Stream stream = PakCipher.CreateResourceDecryptionStream(source, encrypted.LongLength);

        Assert.Throws<InvalidDataException>(() => stream.Read(new byte[4096], 0, 4096));
    }

    /// <summary>前像文件本身被占用（读不出来）时，同样保留未提交日志。</summary>
    [Fact]
    public void TryRecover_PendingFileLocked_KeepsJournal()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.Snapshot snapshot = CaptureTocSnapshot(pak);
            byte[] broken = File.ReadAllBytes(pak);
            broken.AsSpan(16, 48).Fill(0xFF);
            File.WriteAllBytes(pak, broken);
            PakTocPreImage.WritePending(pak, snapshot);

            using (new FileStream(pak + PakTocPreImage.PendingSuffix, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => new RePakRenamer().TryRecover(pak, out _));
            }

            Assert.True(File.Exists(pak + PakTocPreImage.PendingSuffix), "读不出前像时不能删除恢复日志");
            Assert.True(new RePakRenamer().TryRecover(pak, out _));
            Assert.Equal(hash, Assert.Single(new RePakReader().Read(pak).Entries).Hash);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>未提交日志与已提交前像同时存在时，优先用未提交日志。</summary>
    [Fact]
    public void TryRecover_PendingTakesPrecedenceOverBackup()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        ulong otherHash = PakPathHasher.HashPath(PathOther);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.Snapshot current = CaptureTocSnapshot(pak);
            // 先构造一份「另一个状态」的已提交前像。
            byte[] otherToc = current.Toc.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(otherToc, otherHash);
            PakTocPreImage.WritePending(
                pak,
                new PakTocPreImage.Snapshot(
                    current.Version,
                    current.PakLength,
                    current.Major,
                    current.Minor,
                    current.Feature,
                    current.Count,
                    current.DataSample,
                    otherToc));
            PakTocPreImage.Commit(pak);

            // 再把未提交日志写成当前状态，并破坏 pak 的 TOC。
            PakTocPreImage.WritePending(pak, current);
            byte[] broken = File.ReadAllBytes(pak);
            broken.AsSpan(16, 48).Fill(0xFF);
            File.WriteAllBytes(pak, broken);

            Assert.True(new RePakRenamer().TryRecover(pak, out _));
            Assert.Equal(hash, Assert.Single(new RePakReader().Read(pak).Entries).Hash);
            Assert.True(File.Exists(pak + PakTocPreImage.BackupSuffix), "已提交前像不应被未提交日志的恢复删除");
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>v1 前像（只采样数据区前 4KB）仍能被识别并恢复。</summary>
    [Fact]
    public void TryRecover_LegacyV1PreImage_Restores()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.Snapshot snapshot = CaptureTocSnapshot(pak);
            byte[] toc = snapshot.Toc;
            ulong legacySample = LegacyDataSample(pak);
            using (FileStream file = new(pak + PakTocPreImage.BackupSuffix, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new(file))
            {
                writer.Write(Encoding.ASCII.GetBytes("HBTOCPRE"));
                writer.Write(1); // v1
                writer.Write(snapshot.PakLength);
                writer.Write(toc.Length);
                writer.Write(snapshot.Major);
                writer.Write(snapshot.Minor);
                writer.Write(snapshot.Feature);
                writer.Write(snapshot.Count);
                writer.Write(legacySample);
                writer.Write(0uL);
                writer.Write(toc);
            }

            byte[] broken = File.ReadAllBytes(pak);
            broken.AsSpan(16 + 8, 8).Fill(0xFF);
            File.WriteAllBytes(pak, broken);

            Assert.True(new RePakRenamer().TryRecover(pak, out _));
            Assert.Equal(hash, Assert.Single(new RePakReader().Read(pak).Entries).Hash);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>只读锁存在时，并发改名必须等待，释放后再完成。</summary>
    [Fact]
    public void AcquireReadLock_BlocksConcurrentRename()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        ulong newHash = PakPathHasher.HashPath(PathOther);
        string pak = BuildPak((hash, content));
        try
        {
            RePakRenamer renamer = new();
            // 锁必须由同一线程获取/释放（Monitor 是线程亲和的），因此这里不用 async/await。
            IDisposable readLock = renamer.AcquireReadLock(pak)
                ?? throw new InvalidOperationException("应支持只读锁");
            Task rename = Task.Run(() => renamer.RenameEntry(pak, 0, newHash, backup: false));
            bool blocked;
            try
            {
                Thread.Sleep(300);
                blocked = !rename.IsCompleted;
            }
            finally
            {
                readLock.Dispose();
            }

            Assert.True(blocked, "只读锁未释放时改名不应完成");
            Assert.True(SpinWait.SpinUntil(() => rename.IsCompleted, TimeSpan.FromSeconds(5)), "释放只读锁后改名应完成");
            if (rename.Exception is not null)
            {
                throw rename.Exception;
            }

            Assert.Equal(newHash, Assert.Single(new RePakReader().Read(pak).Entries).Hash);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>截断或消失的前像应判为 Invalid 并清理日志，而不是一直抛占用错误。</summary>
    [Fact]
    public void TryRecover_TruncatedPending_IsDiscardedWithoutThrowing()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            PakTocPreImage.WritePending(pak, CaptureTocSnapshot(pak));
            using (FileStream pending = new(pak + PakTocPreImage.PendingSuffix, FileMode.Open, FileAccess.Write))
            {
                pending.SetLength(60); // 头部 48 字节 + 半个条目：TOC 长度与声明不符
            }

            byte[] before = File.ReadAllBytes(pak);
            Assert.False(new RePakRenamer().TryRecover(pak, out _));
            Assert.False(File.Exists(pak + PakTocPreImage.PendingSuffix), "损坏的日志应被清理");
            Assert.Equal(before, File.ReadAllBytes(pak));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>前像在 HasPending 与打开之间消失时按「不适用」处理。</summary>
    [Fact]
    public void ReadSnapshot_MissingFile_IsInvalid()
    {
        string missing = Path.Combine(Path.GetTempPath(), "hb-missing-" + Guid.NewGuid().ToString("N") + ".toc.pending");
        Assert.False(File.Exists(missing));
        Assert.Equal(PakTocPreImage.ReadOutcome.Invalid, PakTocPreImage.ReadSnapshot(missing, out _));
    }

    /// <summary>解包先取路径锁再打开 pak；并发改名持锁时应等待而不是失败。</summary>
    [Fact]
    public void Unpack_WaitsForPathLockInsteadOfFailing()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        string destination = Path.Combine(
            Path.GetDirectoryName(pak)!,
            "hb-unpack-" + Guid.NewGuid().ToString("N"));
        try
        {
            RePakRenamer renamer = new();
            IDisposable readLock = renamer.AcquireReadLock(pak)
                ?? throw new InvalidOperationException("应支持只读锁");
            Task unpack = Task.Run(() =>
                new PakRepacker().Unpack(pak, destination, CancellationToken.None));
            bool blocked;
            try
            {
                Thread.Sleep(300);
                blocked = !unpack.IsCompleted;
            }
            finally
            {
                readLock.Dispose();
            }

            Assert.True(blocked, "路径锁未释放时解包不应完成");
            Assert.True(SpinWait.SpinUntil(() => unpack.IsCompleted, TimeSpan.FromSeconds(5)));
            if (unpack.Exception is not null)
            {
                throw unpack.Exception;
            }

            Assert.True(Directory.Exists(destination));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    /// <summary>按 v1 算法（只采样数据区前 4KB）计算采样哈希。</summary>
    private static ulong LegacyDataSample(string pakPath)
    {
        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        long dataStart = PakTocPreImage.DataStart(header);
        long length = stream.Length;
        if (dataStart >= length)
        {
            return 0;
        }

        int size = (int)Math.Min(4096, length - dataStart);
        byte[] buffer = new byte[size];
        stream.Position = dataStart;
        stream.ReadExactly(buffer);
        ulong value = 14695981039346656037UL;
        foreach (byte item in buffer)
        {
            value ^= item;
            value *= 1099511628211UL;
        }

        return value;
    }

    /// <summary>读取 pak 当前的 TOC 快照（供崩溃恢复测试构造前像）。</summary>
    private static PakTocPreImage.Snapshot CaptureTocSnapshot(string pakPath)
    {
        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        byte[] toc = PakPlainToc.ReadToc(stream, header);
        ulong sample = PakTocPreImage.ComputeDataSample(stream, PakPlainToc.TocEnd(header));
        return new PakTocPreImage.Snapshot(
            PakTocPreImage.CurrentVersion,
            stream.Length,
            header.Major,
            header.Minor,
            header.Feature,
            header.Count,
            sample,
            toc);
    }

    /// <summary>断言只有给定条目的哈希字段变化，其余条目字段与数据区字节完全不变。</summary>
    private static void AssertOnlyHashFieldsChanged(
        byte[] before,
        byte[] after,
        byte major,
        params (int Index, ulong Hash)[] expected)
    {
        Assert.Equal(before.Length, after.Length);
        int entrySize = major == 2 ? 24 : 48;
        int hashFieldOffset = major == 2 ? 16 : 0;
        int entryCount = (before.Length - 16) / entrySize;
        Dictionary<int, ulong> expectedByIndex = expected.ToDictionary(item => item.Index, item => item.Hash);
        HashSet<int> allowed = [];

        foreach ((int index, ulong hash) in expected)
        {
            int start = 16 + (index * entrySize) + hashFieldOffset;
            for (int i = 0; i < 8; i++)
            {
                allowed.Add(start + i);
            }

            Assert.Equal(
                BitConverter.GetBytes((uint)(hash & 0xFFFFFFFF)),
                after.AsSpan(start, 4).ToArray());
            Assert.Equal(
                BitConverter.GetBytes((uint)(hash >> 32)),
                after.AsSpan(start + 4, 4).ToArray());
        }

        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i])
            {
                continue;
            }

            Assert.True(allowed.Contains(i), $"未请求改写的字节被改：offset={i}（应只动目标条目的哈希字段）");
        }

        // 未请求的条目：整条（含哈希字段）必须逐字节不变。
        for (int entry = 0; entry < entryCount; entry++)
        {
            if (expectedByIndex.ContainsKey(entry))
            {
                continue;
            }

            int start = 16 + (entry * entrySize);
            Assert.Equal(
                before.AsSpan(start, entrySize).ToArray(),
                after.AsSpan(start, entrySize).ToArray());
        }
    }

    /// <summary>批量改名：一次读 TOC、一次写回，只有目标条目的哈希字段变化，其余字节（含 checksum/数据区）不动。</summary>
    [Fact]
    public void RenameEntries_V4_ChangesOnlyRequestedHashFields()
    {
        byte[] first = Encoding.ASCII.GetBytes("first-payload");
        byte[] second = Encoding.ASCII.GetBytes("second-payload");
        byte[] third = Encoding.ASCII.GetBytes("third-payload");
        ulong firstHash = PakPathHasher.HashPath(Path012);
        ulong secondHash = PakPathHasher.HashPath(PathOther);
        ulong thirdHash = PakPathHasher.HashPath(Path012 + ".x");
        ulong newFirstHash = PakPathHasher.HashPath(PathOther + ".n1");
        ulong newThirdHash = PakPathHasher.HashPath(PathOther + ".n3");
        string pak = BuildPak((firstHash, first), (secondHash, second), (thirdHash, third));
        try
        {
            byte[] before = File.ReadAllBytes(pak);

            new RePakRenamer().RenameEntries(
                pak,
                [new PakEntryRename(0, newFirstHash), new PakEntryRename(2, newThirdHash)]);

            byte[] after = File.ReadAllBytes(pak);
            AssertOnlyHashFieldsChanged(before, after, major: 4, (0, newFirstHash), (2, newThirdHash));

            PakArchiveInfo archive = new RePakReader().Read(pak);
            Assert.Equal(newFirstHash, archive.Entries[0].Hash);
            Assert.Equal(secondHash, archive.Entries[1].Hash);
            Assert.Equal(newThirdHash, archive.Entries[2].Hash);
            Assert.True(File.Exists(pak + ".toc.bak"), "批量改名也应生成一次 TOC 前像");
            // 数据区未被写坏：仍能按原长度读出原内容。
            Assert.Equal(second, new RePakEntryReader().ReadEntry(pak, archive.Entries[1]));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>批量写回的字节结果必须与「逐条 RenameEntry」完全一致（语义等价，仅少读/少 fsync）。</summary>
    [Fact]
    public void RenameEntries_ProducesSameBytesAsSequentialRenameEntry()
    {
        byte[] contentA = Encoding.ASCII.GetBytes("a-content");
        byte[] contentB = Encoding.ASCII.GetBytes("b-content");
        byte[] contentC = Encoding.ASCII.GetBytes("c-content");
        byte[] contentD = Encoding.ASCII.GetBytes("d-content");
        ulong hashA = PakPathHasher.HashPath(Path012);
        ulong hashB = PakPathHasher.HashPath(PathOther);
        ulong hashC = PakPathHasher.HashPath(Path012 + ".c");
        ulong hashD = PakPathHasher.HashPath(PathOther + ".d");
        ulong newB = PakPathHasher.HashPath(PathOther + ".nb");
        ulong newD = PakPathHasher.HashPath(PathOther + ".nd");
        string sequential = BuildPak((hashA, contentA), (hashB, contentB), (hashC, contentC), (hashD, contentD));
        string batched = Path.Combine(Path.GetDirectoryName(sequential)!, "hb-rename-batch-" + Guid.NewGuid().ToString("N") + ".pak");
        File.Copy(sequential, batched);
        try
        {
            new RePakRenamer().RenameEntry(sequential, 1, newB, backup: false);
            new RePakRenamer().RenameEntry(sequential, 3, newD, backup: false);
            new RePakRenamer().RenameEntries(
                batched,
                [new PakEntryRename(1, newB), new PakEntryRename(3, newD)],
                backup: false);

            Assert.Equal(File.ReadAllBytes(sequential), File.ReadAllBytes(batched));
        }
        finally
        {
            File.Delete(sequential);
            File.Delete(batched);
            File.Delete(sequential + ".toc.bak");
            File.Delete(batched + ".toc.bak");
        }
    }

    /// <summary>任一条目序号越界时整批拒绝：不写文件、不生成备份。</summary>
    [Fact]
    public void RenameEntries_OutOfRange_RejectsWholeBatchWithoutWriting()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            byte[] before = File.ReadAllBytes(pak);

            Assert.Throws<ArgumentOutOfRangeException>(() => new RePakRenamer().RenameEntries(
                pak,
                [new PakEntryRename(0, PakPathHasher.HashPath(PathOther)), new PakEntryRename(9, 1)]));

            Assert.Equal(before, File.ReadAllBytes(pak));
            Assert.False(File.Exists(pak + ".toc.bak"), "整批被拒绝时不应留下备份文件");
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>同一批次重复序号是调用方 bug：整批拒绝，文件不变。</summary>
    [Fact]
    public void RenameEntries_DuplicateIndex_ThrowsBeforeWriting()
    {
        byte[] content = Encoding.ASCII.GetBytes("payload");
        ulong hash = PakPathHasher.HashPath(Path012);
        string pak = BuildPak((hash, content));
        try
        {
            byte[] before = File.ReadAllBytes(pak);

            Assert.Throws<ArgumentException>(() => new RePakRenamer().RenameEntries(
                pak,
                [new PakEntryRename(0, 1), new PakEntryRename(0, 2)]));

            Assert.Equal(before, File.ReadAllBytes(pak));
            Assert.False(File.Exists(pak + ".toc.bak"));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>TOC 加密的 pak 也能只改哈希——解密条目表、改哈希、按同一密钥块加密写回。</summary>
    [Fact]
    public void RenameEntries_EncryptedToc_RewritesDecryptedToc()
    {
        using TempDir temp = TempDir.Create();
        ulong hash = PakPathHasher.HashPath(Path012);
        ulong newHash = PakPathHasher.HashPath(PathOther);
        BuiltPak built = PakCodecTestTools.BuildPak(
            temp.Path,
            "encrypted.pak",
            feature: 8,
            PakCodecTestTools.Stored(hash, Encoding.ASCII.GetBytes("payload")));
        byte[] before = File.ReadAllBytes(built.Path);

        new RePakRenamer().RenameEntries(built.Path, [new PakEntryRename(0, newHash)]);

        // 磁盘上的 TOC 仍是密文（feature 不变），解密后能看到新哈希。
        Assert.True(new RePakReader().Read(built.Path).IsTocEncrypted);
        Assert.Equal(newHash, Assert.Single(new PakRepacker().Inspect(built.Path).Entries).Hash);
        Assert.Equal(
            PakTocPreImage.ReadOutcome.Ok,
            PakTocPreImage.ReadSnapshot(built.Path + ".toc.bak", out PakTocPreImage.Snapshot snapshot));
        Assert.Equal(before.AsSpan(16, 48).ToArray(), snapshot.Toc);
        // 数据区（TOC + 密钥块之后）逐字节不变。
        int dataStart = 16 + 48 + 128;
        Assert.Equal(before.AsSpan(dataStart).ToArray(), File.ReadAllBytes(built.Path).AsSpan(dataStart).ToArray());
    }

    /// <summary>v2 批量改名：哈希字段在条目内 +16，不能写到条目起始。</summary>
    [Fact]
    public void RenameEntries_V2_ChangesOnlyRequestedHashFields()
    {
        using TempDir temp = TempDir.Create();
        ulong hashA = PakPathHasher.HashPath(Path012);
        ulong hashB = PakPathHasher.HashPath(PathOther);
        ulong hashC = PakPathHasher.HashPath(Path012 + ".c");
        byte[] contentA = Encoding.ASCII.GetBytes("v2-a");
        byte[] contentB = Encoding.ASCII.GetBytes("v2-b");
        byte[] contentC = Encoding.ASCII.GetBytes("v2-c");
        string pak = PakCodecTestTools.BuildV2Pak(
            temp.Path,
            "v2-batch.pak",
            (hashA, contentA),
            (hashB, contentB),
            (hashC, contentC));
        byte[] before = File.ReadAllBytes(pak);
        ulong newA = PakPathHasher.HashPath(Path012 + ".na");
        ulong newC = PakPathHasher.HashPath(PathOther + ".nc");

        new RePakRenamer().RenameEntries(pak, [new PakEntryRename(0, newA), new PakEntryRename(2, newC)], backup: false);

        AssertOnlyHashFieldsChanged(before, File.ReadAllBytes(pak), major: 2, (0, newA), (2, newC));
        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(2, archive.Major);
        Assert.Equal(newA, archive.Entries[0].Hash);
        Assert.Equal(hashB, archive.Entries[1].Hash);
        Assert.Equal(newC, archive.Entries[2].Hash);
        Assert.Equal(contentB, new RePakEntryReader().ReadEntry(pak, archive.Entries[1]));
    }

    private static int IndexOf(IReadOnlyList<PakEntryInfo> entries, ulong hash)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Hash == hash)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"条目不存在：{hash:X16}");
    }
}
