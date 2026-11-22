using System.Text;
using HunterBox.Application.Models;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>不支持改写的 pak 原因要能带回界面（汇总），且不中断其它改写。</summary>
public class ModelRewriteCoordinatorTests
{
    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    private const string LooseMesh = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";
    private const string LooseMeshNew = "natives/stm/art/model/character/ch02/015/007/5/ch02_015_0075.mesh.1";
    private const string PakMesh = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";
    private const string PakMeshNew = "natives/stm/art/model/character/ch02/015/007/5/ch02_015_0075.mesh.1";

    private static PakModelRewriteService CreatePakService() => new(
        new RePakReader(),
        new RePakRenamer(),
        new PakPathHashProvider());

    private static ulong EngineHash(string path)
    {
        string normalized = path.Replace('\\', '/');
        return ((ulong)PakPathHasher.HashString(normalized.ToUpperInvariant()) << 32)
            | PakPathHasher.HashString(normalized.ToLowerInvariant());
    }

    private static void WritePak(string pakPath, short feature, params (string Path, byte[] Content, long Attributes)[] entries)
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

        Directory.CreateDirectory(Path.GetDirectoryName(pakPath)!);
        File.WriteAllBytes(pakPath, memory.ToArray());
    }

    private static (BoxPaths Paths, ModRecord Mod, ModelRewriteCoordinator Coordinator) Setup(TempDir temp)
    {
        BoxPaths paths = new(temp.Path);
        string filesDir = paths.FilesDirectory(1);
        Directory.CreateDirectory(filesDir);
        ModRecord mod = new() { Id = 1, Index = 1, GameId = "mhws" };
        return (paths, mod, new ModelRewriteCoordinator(paths, CreatePakService(), Fs));
    }

    private static void WriteLoose(BoxPaths paths, string relative)
    {
        string full = Path.Combine(paths.FilesDirectory(1), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "path=" + relative);
    }

    private static string Full(BoxPaths paths, string relative) =>
        Path.Combine(paths.FilesDirectory(1), relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Rewrite_EncryptedTocPak_IsReportedAndOtherChangesStillApplied()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, ModelRewriteCoordinator coordinator) = Setup(temp);
        WritePak(Full(paths, "mod.pak"), 8, (PakMesh, Encoding.ASCII.GetBytes("enc"), 0L));
        WriteLoose(paths, LooseMesh);
        mod.RelativeFiles = ["mod.pak", LooseMesh];
        PakNameIndex index = PakPathHasher.BuildIndex([PakMesh, PakMeshNew]);

        ModelRewriteSummary summary = coordinator.Rewrite(
            mod, [("ch02/012/003", "ch02/015/007")], index, ModelGame.Wilds);

        string unsupported = Assert.Single(summary.UnsupportedPaks);
        Assert.Contains("mod.pak", unsupported);
        Assert.Contains("加密", unsupported);
        Assert.Equal(0, summary.EntriesChanged);
        Assert.Equal(1, summary.FilesMoved); // 散装文件照常改名
        Assert.True(summary.HasChanges);
        Assert.Contains(LooseMeshNew, mod.RelativeFiles);
        Assert.True(File.Exists(Full(paths, LooseMeshNew)));
        Assert.False(File.Exists(Full(paths, "mod.pak") + ".toc.bak"));

    }

    [Fact]
    public void Rewrite_CompressedPak_RenamesTocHashAndKeepsBackup()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, ModelRewriteCoordinator coordinator) = Setup(temp);
        string pakPath = Full(paths, "mod.pak");
        WritePak(pakPath, 0, (PakMesh, Encoding.ASCII.GetBytes("compressed-payload"), 1L));
        byte[] original = File.ReadAllBytes(pakPath);
        mod.RelativeFiles = ["mod.pak"];
        PakNameIndex index = PakPathHasher.BuildIndex([PakMesh, PakMeshNew]);

        ModelRewriteSummary summary = coordinator.Rewrite(
            mod, [("ch02/012/003", "ch02/015/007")], index, ModelGame.Wilds);

        Assert.Empty(summary.UnsupportedPaks);
        Assert.Empty(summary.Warnings);
        Assert.Equal(1, summary.EntriesChanged);
        Assert.Equal(1, summary.TotalChanges);
        Assert.Equal(EngineHash(PakMeshNew), Assert.Single(new RePakReader().Read(pakPath).Entries).Hash);
        Assert.True(File.Exists(pakPath + ".toc.bak"));

        // 只改了条目表前 8 字节哈希，压缩属性/数据区原样
        byte[] updated = File.ReadAllBytes(pakPath);
        Assert.Equal(original.Length, updated.Length);
        for (int i = 0; i < original.Length; i++)
        {
            bool hashByte = i >= 16 && i < 24;
            Assert.True(hashByte || original[i] == updated[i], $"offset={i} 不应变化");
        }
    }

    [Fact]
    public void Rewrite_CollectsWarningsForUnknownEntriesAndBrokenPak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, ModelRewriteCoordinator coordinator) = Setup(temp);
        WritePak(Full(paths, "unknown.pak"), 0, (PakMesh, Encoding.ASCII.GetBytes("a"), 0L));
        File.WriteAllText(Full(paths, "broken.pak"), "not a pak");
        mod.RelativeFiles = ["unknown.pak", "broken.pak"];
        PakNameIndex emptyIndex = PakPathHasher.BuildIndex([]);

        ModelRewriteSummary summary = coordinator.Rewrite(
            mod, [("ch02/012/003", "ch02/015/007")], emptyIndex, ModelGame.Wilds);

        Assert.Equal(2, summary.PaksScanned);
        Assert.False(summary.HasChanges);
        Assert.Empty(summary.UnsupportedPaks);
        Assert.Contains(summary.Warnings, w => w.Contains("不在路径清单", StringComparison.Ordinal));
        Assert.Contains(summary.Warnings, w => w.Contains("broken.pak", StringComparison.Ordinal));
    }

    [Fact]
    public void Rewrite_NoChanges_ReturnsEmptySummary()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, ModelRewriteCoordinator coordinator) = Setup(temp);
        WritePak(Full(paths, "mod.pak"), 0, (PakMesh, Encoding.ASCII.GetBytes("a"), 0L));
        mod.RelativeFiles = ["mod.pak"];
        PakNameIndex index = PakPathHasher.BuildIndex([PakMesh, PakMeshNew]);

        ModelRewriteSummary summary = coordinator.Rewrite(mod, [], index, ModelGame.Wilds);

        Assert.Equal(1, summary.PaksScanned);
        Assert.False(summary.HasChanges);
        Assert.Empty(summary.Warnings);
        Assert.Empty(summary.UnsupportedPaks);
        Assert.False(File.Exists(Full(paths, "mod.pak") + ".toc.bak"));
    }
}
