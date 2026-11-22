using System.Text;
using System.Buffers.Binary;
using HunterBox.Application.Models;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class PakModelRewriteServiceTests
{
    /// <summary>同一 pak 的多个 change 只调用一次批量改名（一次整包复制）。</summary>
    [Fact]
    public void RewritePaths_BatchChanges_WritesOnceAndKeepsOriginalBackup()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        const string pathB = "natives/stm/art/model/character/ch03/012/004/head.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        ulong hashB = PakPathHasher.HashPath(pathB);
        string pak = BuildPakWithHashes(
            (hashA, Encoding.ASCII.GetBytes("body")),
            (hashB, Encoding.ASCII.GetBytes("head")));
        byte[] original = File.ReadAllBytes(pak);
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            index.Add(hashB, pathB);
            CountingRenamer renamer = new(new RePakRenamer());
            PakModelRewriteService service = new(
                new RePakReader(),
                renamer,
                new PakPathHashProvider());

            ModelRewriteReport report = service.RewritePaths(
                pak,
                [("ch03/012/003", "ch03/015/007"), ("ch03/012/004", "ch03/015/008")],
                index);

            Assert.Equal(2, report.EntriesScanned);
            Assert.Equal(2, report.EntriesChanged);
            Assert.Equal(1, renamer.BatchCalls);
            // 前像文件带头部指纹，正文是写回前的原始条目表。
            Assert.Equal(
                PakTocPreImage.ReadOutcome.Ok,
                PakTocPreImage.ReadSnapshot(pak + ".toc.bak", out PakTocPreImage.Snapshot snapshot));
            Assert.Equal(original.AsSpan(16, 2 * 48).ToArray(), snapshot.Toc);

            HashSet<ulong> hashes = [.. new RePakReader().Read(pak).Entries.Select(entry => entry.Hash)];
            Assert.Contains(PakPathHasher.HashPath("natives/stm/art/model/character/ch03/015/007/body.mod3"), hashes);
            Assert.Contains(PakPathHasher.HashPath("natives/stm/art/model/character/ch03/015/008/head.mod3"), hashes);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>同一源身份在一次改写里指定多个不同目标时，必须在改动 pak 之前拒绝。</summary>
    [Fact]
    public void RewritePaths_SameSourceWithDifferentTargets_RejectsBeforeTouchingPak()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        string pak = BuildPakWithHashes((hashA, Encoding.ASCII.GetBytes("body")));
        byte[] original = File.ReadAllBytes(pak);
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            ArgumentException ex = Assert.Throws<ArgumentException>(() => service.RewritePaths(
                pak,
                [("ch03/012/003", "ch03/015/007"), ("ch03/012/003", "ch03/020/001")],
                index));

            Assert.Contains("多个目标", ex.Message);
            Assert.Equal(original, File.ReadAllBytes(pak));
            Assert.False(File.Exists(pak + ".toc.bak"), "校验失败时不能留下前像或改动 pak");
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>多个条目映射到同一目标哈希时只写一个并告警。</summary>
    [Fact]
    public void RewritePaths_DuplicateTargetHash_WarnsAndSkipsDuplicate()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        const string pathB = "natives/stm/art/model/character/ch03/012/004/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        ulong hashB = PakPathHasher.HashPath(pathB);
        string pak = BuildPakWithHashes(
            (hashA, Encoding.ASCII.GetBytes("a")),
            (hashB, Encoding.ASCII.GetBytes("b")));
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            index.Add(hashB, pathB);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            ModelRewriteReport report = service.RewritePaths(
                pak,
                [("ch03/012/003", "ch03/015/007"), ("ch03/012/004", "ch03/015/007")],
                index);

            Assert.Equal(1, report.EntriesChanged);
            Assert.Contains(report.Warnings, warning => warning.Contains("同一路径哈希"));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>目标路径已被 pak 内未参与改写的既有条目占用时，跳过并告警。</summary>
    [Fact]
    public void RewritePaths_TargetHashOwnedByExistingEntry_WarnsAndSkips()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        const string pathB = "natives/stm/art/model/character/ch03/020/001/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        ulong hashB = PakPathHasher.HashPath(pathB);
        string pak = BuildPakWithHashes(
            (hashA, Encoding.ASCII.GetBytes("a")),
            (hashB, Encoding.ASCII.GetBytes("b")));
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            index.Add(hashB, pathB);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            ModelRewriteReport report = service.RewritePaths(
                pak,
                [("ch03/012/003", "ch03/020/001")],
                index);

            Assert.Equal(0, report.EntriesChanged);
            Assert.Contains(report.Warnings, warning => warning.Contains("既有条目"));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>单条重载与批量重载共享目标哈希冲突检测，写入前必须跳过并告警。</summary>
    [Fact]
    public void RewritePaths_SingleTargetHashOwnedByExistingEntry_WarnsAndSkips()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        const string pathB = "natives/stm/art/model/character/ch03/020/001/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        ulong hashB = PakPathHasher.HashPath(pathB);
        string pak = BuildPakWithHashes(
            (hashA, Encoding.ASCII.GetBytes("a")),
            (hashB, Encoding.ASCII.GetBytes("b")));
        byte[] original = File.ReadAllBytes(pak);
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            index.Add(hashB, pathB);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            ModelRewriteReport report = service.RewritePaths(pak, "ch03/012/003", "ch03/020/001", index);

            Assert.Equal(0, report.EntriesChanged);
            Assert.Contains(report.Warnings, warning => warning.Contains("既有条目"));
            Assert.Equal(original, File.ReadAllBytes(pak));
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".toc.bak");
        }
    }

    /// <summary>
    /// B→C 因 C 已存在被跳过后，B 保留旧路径；A→B 的旧路径也必须被跳过，
    /// 不能因为「初始占用集合排除了 B」而放行，最终 TOC 出现重复哈希。
    /// </summary>
    [Fact]
    public void RewritePaths_CascadingConflict_KeepsOriginalHashes()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        const string pathB = "natives/stm/art/model/character/ch03/012/004/body.mod3";
        const string pathC = "natives/stm/art/model/character/ch03/020/001/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        ulong hashB = PakPathHasher.HashPath(pathB);
        ulong hashC = PakPathHasher.HashPath(pathC);
        string pak = BuildPakWithHashes(
            (hashA, Encoding.ASCII.GetBytes("a")),
            (hashB, Encoding.ASCII.GetBytes("b")),
            (hashC, Encoding.ASCII.GetBytes("c")));
        byte[] original = File.ReadAllBytes(pak);
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            index.Add(hashB, pathB);
            index.Add(hashC, pathC);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            // 顺序很重要：先处理 B→C（冲突被跳过、保留 B 的旧路径），再处理 A→B 的旧路径。
            ModelRewriteReport report = service.RewritePaths(
                pak,
                [("ch03/012/004", "ch03/020/001"), ("ch03/012/003", "ch03/012/004")],
                index);

            Assert.Equal(0, report.EntriesChanged);
            Assert.Contains(report.Warnings, warning => warning.Contains("既有条目"));
            Assert.Equal(original, File.ReadAllBytes(pak));
            HashSet<ulong> hashes = [.. new RePakReader().Read(pak).Entries.Select(entry => entry.Hash)];
            Assert.Equal(3, hashes.Count);
            Assert.Contains(hashA, hashes);
            Assert.Contains(hashB, hashes);
            Assert.Contains(hashC, hashes);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + ".bak");
        }
    }

    /// <summary>
    /// 半写但仍可解析的 TOC + 未提交日志时，必须先恢复再规划 rename，
    /// 否则会按哈希混合的条目表把改名应用到错误条目。
    /// </summary>
    [Fact]
    public void RewritePaths_HalfWrittenTocWithPending_RecoversBeforePlanning()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        const string pathB = "natives/stm/art/model/character/ch03/012/004/head.mod3";
        const string targetA = "natives/stm/art/model/character/ch03/015/007/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        ulong hashB = PakPathHasher.HashPath(pathB);
        string pak = BuildPakWithHashes(
            (hashA, Encoding.ASCII.GetBytes("a")),
            (hashB, Encoding.ASCII.GetBytes("b")));
        try
        {
            PakTocPreImage.Snapshot snapshot = CaptureTocSnapshot(pak);
            // 模拟「TOC 写到一半」：条目 0 的哈希变成随机值，但偏移/大小仍合法（可解析）。
            byte[] broken = File.ReadAllBytes(pak);
            BinaryPrimitives.WriteUInt64LittleEndian(broken.AsSpan(16, 8), 0xDEAD_BEEF_DEAD_BEEFUL);
            File.WriteAllBytes(pak, broken);
            PakTocPreImage.WritePending(pak, snapshot);

            PakNameIndex index = new();
            index.Add(hashA, pathA);
            index.Add(hashB, pathB);
            index.Add(PakPathHasher.HashPath(targetA), targetA);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            ModelRewriteReport report = service.RewritePaths(
                pak,
                [("ch03/012/003", "ch03/015/007")],
                index);

            Assert.Equal(1, report.EntriesChanged);
            Assert.Contains(report.Warnings, warning => warning.Contains("恢复"));
            HashSet<ulong> hashes = [.. new RePakReader().Read(pak).Entries.Select(entry => entry.Hash)];
            Assert.Contains(PakPathHasher.HashPath(targetA), hashes);
            Assert.Equal(2, hashes.Count);
            Assert.Contains(hashB, hashes);
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>pak 被占用时抛出可操作的 IOException，而不是裸系统异常。</summary>
    [Fact]
    public void RewritePaths_LockedPak_ThrowsFriendlyIOException()
    {
        const string pathA = "natives/stm/art/model/character/ch03/012/003/body.mod3";
        ulong hashA = PakPathHasher.HashPath(pathA);
        string pak = BuildPakWithHashes((hashA, Encoding.ASCII.GetBytes("a")));
        try
        {
            PakNameIndex index = new();
            index.Add(hashA, pathA);
            PakModelRewriteService service = new(
                new RePakReader(),
                new RePakRenamer(),
                new PakPathHashProvider());

            using (new FileStream(pak, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                IOException error = Assert.Throws<IOException>(() =>
                    service.RewritePaths(pak, [("ch03/012/003", "ch03/015/007")], index));
                Assert.Contains("无法读取或恢复", error.Message);
            }
        }
        finally
        {
            File.Delete(pak);
            File.Delete(pak + PakTocPreImage.BackupSuffix);
            File.Delete(pak + PakTocPreImage.PendingSuffix);
        }
    }

    /// <summary>读取 pak 当前的 TOC 快照（构造前像用）。</summary>
    private static PakTocPreImage.Snapshot CaptureTocSnapshot(string pakPath)
    {
        using FileStream stream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PakPlainHeader header = PakPlainToc.ReadHeader(stream);
        byte[] toc = PakPlainToc.ReadToc(stream, header);
        ulong sample = PakTocPreImage.ComputeDataSample(stream, PakTocPreImage.DataStart(header));
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

    /// <summary>生成 v4 明文 pak，条目哈希由调用方指定（用于构造可还原路径的测试数据）。</summary>
    private static string BuildPakWithHashes(params (ulong Hash, byte[] Content)[] entries)
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

        string path = Path.Combine(Path.GetTempPath(), "hb-batch-" + Guid.NewGuid().ToString("N") + ".pak");
        File.WriteAllBytes(path, memory.ToArray());
        return path;
    }

    /// <summary>统计批量改名调用次数的测试替身。</summary>
    private sealed class CountingRenamer : IPakEntryRenamer, IBatchPakEntryRenamer
    {
        private readonly IPakEntryRenamer _inner;

        public CountingRenamer(IPakEntryRenamer inner) => _inner = inner;

        public int BatchCalls { get; private set; }

        public void RenameEntry(string pakPath, int entryIndex, ulong newHash, bool backup = true) =>
            _inner.RenameEntry(pakPath, entryIndex, newHash, backup);

        public void RenameEntries(string pakPath, IReadOnlyList<PakEntryRename> renames, bool backup = true)
        {
            BatchCalls++;
            ((IBatchPakEntryRenamer)_inner).RenameEntries(pakPath, renames, backup);
        }
    }
}
