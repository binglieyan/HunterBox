using System.Text;
using HunterBox.Application.Models;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// pak 型 MOD 必须按「完整模型身份」只重写 TOC 路径哈希，
/// 数据区/压缩/加密属性不动；TOC 加密的 pak 明确拒绝且不写坏文件。
/// </summary>
public class ModelIdentityPakRewriteTests
{
    private const string ArmorMesh = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.241111606";
    private const string ArmorMeshNew = "natives/stm/art/model/character/ch02/015/007/5/ch02_015_0075.mesh.241111606";
    private const string ArmorPrefab = "natives/stm/gamedesign/equip/_prefab/armor/male/012/003/body/ch02_012_0032.pfb.18";
    private const string ArmorPrefabNew = "natives/stm/gamedesign/equip/_prefab/armor/male/015/007/body/ch02_015_0072.pfb.18";
    private const string ArmorTexture =
        "natives/stm/art/model/character/ch02/012/003/5/textures/ch02_012_0035_ALBD.tex.241106027";
    private const string ArmorFemale = "natives/stm/art/model/character/ch03/012/003/5/ch03_012_0035.mesh.1";
    private const string WeaponMesh = "natives/stm/art/model/item/it10/03/0000/it1003_0000_0.mdf2.45";
    private const string WeaponMeshNew = "natives/stm/art/model/item/it10/05/0001/it1005_0001_0.mdf2.45";
    private const string WeaponPrefab =
        "natives/stm/gamedesign/equip/_prefab/weapon/wp10/03/0000/it1003_0000_0.pfb.18";
    private const string WeaponPrefabNew =
        "natives/stm/gamedesign/equip/_prefab/weapon/wp10/05/0001/it1005_0001_0.pfb.18";
    private const string WeaponTexture =
        "natives/stm/art/model/item/it10/03/0000/textures/it1003_0000_0_ALBD.tex.1";
    private const string CatMesh = "natives/stm/art/model/character/ch05/003/0001/ch05_003_0001.mesh.1";
    private const string CatMeshNew = "natives/stm/art/model/character/ch05/004/0002/ch05_004_0002.mesh.1";

    private static PakModelRewriteService CreateService() => new(
        new RePakReader(),
        new RePakRenamer(),
        new PakPathHashProvider());

    /// <summary>按引擎约定计算组合哈希：<c>(hash(大写) &lt;&lt; 32) | hash(小写)</c>。</summary>
    private static ulong EngineHash(string path)
    {
        string normalized = path.Replace('\\', '/');
        return ((ulong)PakPathHasher.HashString(normalized.ToUpperInvariant()) << 32)
            | PakPathHasher.HashString(normalized.ToLowerInvariant());
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

    private static void AssertOnlyHashesChanged(byte[] before, byte[] after, params int[] entryIndexes)
    {
        Assert.Equal(before.Length, after.Length);
        HashSet<int> changed = [];
        foreach (int index in entryIndexes)
        {
            int start = 16 + (index * 48);
            for (int i = 0; i < 8; i++)
            {
                changed.Add(start + i);
            }

            Assert.False(before.AsSpan(start, 8).SequenceEqual(after.AsSpan(start, 8)));
        }

        for (int i = 0; i < before.Length; i++)
        {
            if (!changed.Contains(i))
            {
                Assert.Equal(before[i], after[i]);
            }
        }
    }

    [Fact]
    public void RewritePaths_ArmorFullIdentity_RenamesMeshAndPrefab_KeepsTextureAndOtherSex()
    {
        using TempDir temp = TempDir.Create();
        string pak = BuildPak(
            temp.Path,
            0,
            (ArmorMesh, Encoding.ASCII.GetBytes("mesh-payload"), 0L),
            (ArmorPrefab, Encoding.ASCII.GetBytes("prefab-payload"), 0L),
            (ArmorTexture, Encoding.ASCII.GetBytes("tex-payload"), 0L),
            (ArmorFemale, Encoding.ASCII.GetBytes("female-payload"), 0L));
        byte[] original = File.ReadAllBytes(pak);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorMesh, ArmorPrefab, ArmorTexture, ArmorFemale, ArmorMeshNew, ArmorPrefabNew]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "ch02/012/003", "ch02/015/007", index);

        Assert.Equal(4, report.EntriesScanned);
        Assert.Equal(2, report.EntriesChanged);
        Assert.Empty(report.Warnings);

        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(EngineHash(ArmorMeshNew), archive.Entries[0].Hash);
        Assert.Equal(EngineHash(ArmorPrefabNew), archive.Entries[1].Hash);
        Assert.Equal(EngineHash(ArmorTexture), archive.Entries[2].Hash); // 贴图不改名
        Assert.Equal(EngineHash(ArmorFemale), archive.Entries[3].Hash); // 女款不受男款身份影响
        AssertOnlyHashesChanged(original, File.ReadAllBytes(pak), 0, 1);
        Assert.True(File.Exists(pak + ".toc.bak"));
    }

    [Fact]
    public void RewritePaths_WeaponFullIdentity_RenamesSeriesAndPrefab()
    {
        using TempDir temp = TempDir.Create();
        string pak = BuildPak(
            temp.Path,
            0,
            (WeaponMesh, Encoding.ASCII.GetBytes("w0"), 0L),
            (WeaponPrefab, Encoding.ASCII.GetBytes("w1"), 0L),
            (WeaponTexture, Encoding.ASCII.GetBytes("w2"), 0L));
        PakNameIndex index = PakPathHasher.BuildIndex([WeaponMesh, WeaponPrefab, WeaponTexture]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "it10/03/0000", "it10/05/0001", index);

        Assert.Equal(2, report.EntriesChanged);
        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(EngineHash(WeaponMeshNew), archive.Entries[0].Hash);
        Assert.Equal(EngineHash(WeaponPrefabNew), archive.Entries[1].Hash);
        Assert.Equal(EngineHash(WeaponTexture), archive.Entries[2].Hash);
    }

    [Fact]
    public void RewritePaths_CatEquipFullIdentity_RenamesFourDigitSid()
    {
        using TempDir temp = TempDir.Create();
        string pak = BuildPak(temp.Path, 0, (CatMesh, Encoding.ASCII.GetBytes("cat"), 0L));
        PakNameIndex index = PakPathHasher.BuildIndex([CatMesh]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "ch05/003/0001", "ch05/004/0002", index);

        Assert.Equal(1, report.EntriesChanged);
        Assert.Equal(EngineHash(CatMeshNew), Assert.Single(new RePakReader().Read(pak).Entries).Hash);
    }

    [Fact]
    public void RewritePaths_CompressedEntries_StillRenameTocHashOnly()
    {
        using TempDir temp = TempDir.Create();
        const long compression = 1L;
        const long encryption = 2L << 16;
        string pak = BuildPak(
            temp.Path,
            0,
            (ArmorMesh, Encoding.ASCII.GetBytes("compressed"), compression),
            (ArmorPrefab, Encoding.ASCII.GetBytes("encrypted"), encryption));
        byte[] original = File.ReadAllBytes(pak);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorMesh, ArmorPrefab]);

        ModelRewriteReport report = CreateService().RewritePaths(pak, "ch02/012/003", "ch02/015/007", index);

        Assert.Equal(2, report.EntriesChanged);
        PakArchiveInfo archive = new RePakReader().Read(pak);
        Assert.Equal(compression, archive.Entries[0].Attributes);
        Assert.Equal(encryption, archive.Entries[1].Attributes);
        Assert.Equal(EngineHash(ArmorMeshNew), archive.Entries[0].Hash);
        Assert.Equal(EngineHash(ArmorPrefabNew), archive.Entries[1].Hash);
        AssertOnlyHashesChanged(original, File.ReadAllBytes(pak), 0, 1);
    }

    [Theory]
    [InlineData((short)8)]
    [InlineData((short)24)]
    public void RewritePaths_EncryptedToc_ThrowsAndLeavesFileUntouched(short feature)
    {
        using TempDir temp = TempDir.Create();
        string pak = BuildPak(temp.Path, feature, (ArmorMesh, Encoding.ASCII.GetBytes("a"), 0L));
        byte[] original = File.ReadAllBytes(pak);
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorMesh]);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            CreateService().RewritePaths(pak, "ch02/012/003", "ch02/015/007", index));
        Assert.Contains("加密", error.Message);
        Assert.Equal(original, File.ReadAllBytes(pak));
        Assert.False(File.Exists(pak + ".toc.bak"));
    }

    [Fact]
    public void RewritePaths_MismatchedIdentityType_Throws()
    {
        PakNameIndex index = PakPathHasher.BuildIndex([ArmorMesh]);

        Assert.Throws<ArgumentException>(() =>
            CreateService().RewritePaths("unused.pak", "ch02/012/003", "ch03/012/003", index));
        Assert.Throws<ArgumentException>(() =>
            CreateService().RewritePaths("unused.pak", "ch02/012/003", "it10/03/0000", index));
        Assert.Throws<ArgumentException>(() =>
            CreateService().RewritePaths("unused.pak", "ghost/012/003", "ch02/015/007", index));
    }
}
