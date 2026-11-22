using System.Text;
using HunterBox.Application.Models;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Mods;
using HunterBox.Infrastructure.Paks;
using ModelIdentity = HunterBox.Domain.Models.ModelIdentity;

namespace HunterBox.Tests;

/// <summary>三作模型行检测、散装改名与 pak 条目改写。</summary>
public class ModelGameRewriteTests
{
    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    // ---------------------------------------------------------------- 模型行检测

    private const string RiseArmorMesh = "natives/stm/player/mod/f/pl001/f_arm001.mdf2.23";
    private const string RiseArmorPrefab = "natives/stm/player/prefab/mod/f/pl001/f_arm001.pfb.17";
    private const string RiseArmorTex = "natives/stm/player/mod/f/pl001/f_arm001_ALBD.tex.28";
    private const string RiseBone = "reframework/data/bonesystem/f_arm001";
    private const string WorldArmorMesh = "nativePC/pl/m_equip/pl001_0000/body/mod/m_body001_0000.mod3";
    private const string WorldWeaponMesh = "nativePC/wp/bow/bow001/mod/bow001.mdf2.45";

    [Fact]
    public void Detect_RiseArmorMergesModelAndPrefab_IgnoresTextureAndBone()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            (RiseArmorMesh, null),
            (RiseArmorPrefab, null),
            (RiseArmorTex, null),
            (RiseBone, null),
        ]);

        DetectedModel row = Assert.Single(rows);
        Assert.Equal(ModelGame.Rise, row.Game);
        Assert.Equal(ModelIdentity.RiseArmor(1, female: true), row.Identity);
        Assert.Equal(2, row.FileCount);
        Assert.True(row.HasPrefab);
        Assert.Equal("rise:pl/f/001", row.IdentityForm);
        Assert.Equal("pl001", row.RewriteId);
    }

    [Fact]
    public void Detect_RiseWeaponKeepsDifferentTypesApart()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            ("natives/stm/weapon/Bow/Bow001/Bow001.mdf2.23", null),
            ("natives/stm/weapon/GreatSword/G_Swd001/G_Swd001.mdf2.23", null),
            ("natives/stm/player/prefab/weapon/Bow/B_Bow001.pfb.17", null),
        ]);

        Assert.Equal(2, rows.Count);
        DetectedModel bow = Assert.Single(rows, r => r.Identity.Type == ModelTypes.Bow);
        Assert.Equal(2, bow.FileCount);
        Assert.True(bow.HasPrefab);
        Assert.Equal("BOW001", bow.RewriteId);
    }

    [Fact]
    public void Detect_WorldRows_RequireModDirectory()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            (WorldArmorMesh, null),
            (WorldWeaponMesh, null),
            ("nativePC/wp/bow/bow001/epv/bow001.pfb.18", null),
            ("nativePC/wp/bow/parts/op_bow005", null),
            ("nativePC/pl/m_equip/pl001_0000/body/mod/m_body001_0000_ALBD.tex.1", null),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ModelGame.World, row.Game));
        Assert.Contains(rows, row => row.Identity.Kind == ModelKind.Armor);
        Assert.Contains(rows, row => row.Identity.Kind == ModelKind.Weapon);
    }

    [Fact]
    public void Detect_AutoDetectsGamePerPath()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            ("natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1", null),
            (RiseArmorMesh, null),
            (WorldWeaponMesh, null),
        ]);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row.Game == ModelGame.Wilds);
        Assert.Contains(rows, row => row.Game == ModelGame.Rise);
        Assert.Contains(rows, row => row.Game == ModelGame.World);
    }

    [Fact]
    public void Detect_ExplicitGame_FiltersOtherGames()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect([(RiseArmorMesh, null)], ModelGame.Wilds);

        Assert.Empty(rows);
    }

    [Fact]
    public void Detect_WorldBsWeaponWithOpParts_AddsOpRows()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            ("nativePC/wp/bow/bs_bow001/mod/bs_bow001.mdf2.45", null),
            ("nativePC/wp/bow/parts/op_bow005", null),
            ("nativePC/wp/bow/parts/op_bow008", null),
            ("nativePC/wp/swo/swo003/mod/swo003.mod3", null),
            ("nativePC/wp/swo/parts/op_swo001", null),
        ]);

        // 基础弓 + 两个 op 叠层行 + 太刀基础行；太刀基础武器不是 bs_，不扩展叠层
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, r => r.RewriteId == "bs_bow001");
        DetectedModel opFive = Assert.Single(rows, r => r.RewriteId == "bs_bow001:op_bow005");
        Assert.Equal(1, opFive.FileCount);
        Assert.Null(opFive.PakFile);
        Assert.Equal("搔鸟弓", ModelNameResolver.Resolve(opFive.SamplePath));
        Assert.Contains(rows, r => r.RewriteId == "bs_bow001:op_bow008");
        Assert.DoesNotContain(rows, r => r.RewriteId.Contains("op_swo", StringComparison.Ordinal));
    }

    [Fact]
    public void Detect_ReportsPrefabAndDisplayType()
    {
        DetectedModel riseArmor = Assert.Single(ModelDetector.Detect([(RiseArmorPrefab, null)]));
        Assert.True(riseArmor.HasPrefab);
        Assert.Equal("护甲(女款)", riseArmor.TypeDisplayName);

        DetectedModel wildsWeapon = Assert.Single(ModelDetector.Detect(
        [
            ("natives/stm/art/model/item/it11/00/0000/it1100_0000_0.mdf2.45", null),
            ("natives/stm/gamedesign/equip/_prefab/weapon/wp11/00/0000/it1100_0000_0.pfb.18", null),
        ]));
        Assert.True(wildsWeapon.HasPrefab);
        Assert.Equal("弓", wildsWeapon.TypeDisplayName);

        DetectedModel worldWeapon = Assert.Single(ModelDetector.Detect([(WorldWeaponMesh, null)]));
        Assert.False(worldWeapon.HasPrefab);
        Assert.Equal("弓", worldWeapon.TypeDisplayName);
    }

    [Fact]
    public void Detect_SameWorldModelInTwoPaks_SeparateRows()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            (WorldWeaponMesh, "a.pak"),
            (WorldWeaponMesh, "b.pak"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.PakFile));
    }

    [Fact]
    public void CollectEntries_WorldDoesNotExpandPak()
    {
        int reads = 0;

        IReadOnlyList<(string Path, string? PakFile)> world = ModelDetector.CollectEntries(
            ["mod.pak", WorldWeaponMesh],
            _ => { reads++; return ["nativePC/wp/bow/bow002/mod/bow002.mdf2"]; },
            ModelGame.World);
        Assert.Equal(0, reads);
        Assert.Equal(2, world.Count);

        IReadOnlyList<(string Path, string? PakFile)> rise = ModelDetector.CollectEntries(
            ["mod.pak"],
            _ => { reads++; return ["natives/stm/weapon/Bow/Bow001/Bow001.mdf2.23"]; },
            ModelGame.Rise);
        Assert.Equal(1, reads);
        Assert.Equal(2, rise.Count);
    }

    [Fact]
    public void ModelEditorPlanner_BuildTargetForm_KeepsGameAndType()
    {
        ModelIdentity riseArmor = ModelIdentity.RiseArmor(1, female: true);
        ModelChoice riseArmorTarget = new("pl002", "测试", false, ModelIdentity.RiseArmor(2, female: false));
        Assert.Equal("rise:pl/f/002", ModelEditorPlanner.BuildTargetForm(riseArmor, riseArmorTarget));

        ModelIdentity worldWeapon = ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, false, 0);
        ModelChoice worldWeaponTarget = new(
            "bs_bow002",
            "测试",
            false,
            ModelIdentity.WorldWeapon(ModelTypes.Bow, 2, true, 0));
        Assert.Equal("world:wp/bs_bow002", ModelEditorPlanner.BuildTargetForm(worldWeapon, worldWeaponTarget));

        ModelChoice wildsTarget = new("012_003", "测试", false, ModelIdentity.Armor(2, 12, 3));
        Assert.Throws<ArgumentException>(() => ModelEditorPlanner.BuildTargetForm(riseArmor, wildsTarget));
    }

    // ---------------------------------------------------------------- 散装改写

    [Fact]
    public void LooseRewrite_RiseArmor_MovesModelPrefabAndBone_KeepsContent()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        const string gimmick = "natives/stm/weapon/Bow/Gimmick/PartsGimmick/Bow_PartsGmk001.user.2";
        string[] files = [RiseArmorMesh, RiseArmorPrefab, RiseBone, RiseArmorTex, gimmick];
        ModRecord mod = CreateMod(paths, 1, files);
        byte[] before = File.ReadAllBytes(Full(paths, 1, RiseArmorMesh));

        LooseModelRewriteReport report = new LooseModelRewriteService(paths, Fs)
            .Rewrite(mod, "rise:pl/f/001", "rise:pl/f/002");

        Assert.Equal(3, report.FilesMoved);
        Assert.Contains("natives/stm/player/mod/f/pl002/f_arm002.mdf2.23", mod.RelativeFiles);
        Assert.Contains("natives/stm/player/prefab/mod/f/pl002/f_arm002.pfb.17", mod.RelativeFiles);
        Assert.Contains("reframework/data/bonesystem/f_arm002", mod.RelativeFiles);
        Assert.Contains(RiseArmorTex, mod.RelativeFiles);
        Assert.Contains(gimmick, mod.RelativeFiles);
        Assert.True(File.Exists(Full(paths, 1, "natives/stm/player/mod/f/pl002/f_arm002.mdf2.23")));
        Assert.Equal(
            before,
            File.ReadAllBytes(Full(paths, 1, "natives/stm/player/mod/f/pl002/f_arm002.mdf2.23")));
        Assert.Contains("pl002", report.ModelIds);
    }

    [Fact]
    public void LooseRewrite_RiseWeapon_MovesCodePartsAndPrefab()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string[] files =
        [
            "natives/stm/weapon/Bow/Bow001/Bow001.mdf2.23",
            "natives/stm/weapon/Bow/Bow001/B_Ydt001.mdf2.23",
            "natives/stm/player/prefab/weapon/Bow/B_Bow001.pfb.17",
            "natives/stm/weapon/Bow/Bow001/Bow001_ALBD.tex.28",
        ];
        ModRecord mod = CreateMod(paths, 1, files);

        LooseModelRewriteReport report = new LooseModelRewriteService(paths, Fs)
            .Rewrite(mod, "rise:wp/Bow/001", "rise:wp/Bow/002");

        Assert.Equal(3, report.FilesMoved);
        Assert.Contains(mod.RelativeFiles, f => f.EndsWith("Bow002.mdf2.23", StringComparison.Ordinal));
        Assert.Contains(mod.RelativeFiles, f => f.EndsWith("B_Ydt002.mdf2.23", StringComparison.Ordinal));
        Assert.Contains(mod.RelativeFiles, f => f.EndsWith("B_Bow002.pfb.17", StringComparison.Ordinal));
        Assert.Contains(mod.RelativeFiles, f => f.EndsWith("Bow001_ALBD.tex.28", StringComparison.Ordinal));
    }

    [Fact]
    public void LooseRewrite_WorldArmorAndWeapon_MovesModelFiles()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string[] files =
        [
            WorldArmorMesh,
            "nativePC/pl/m_equip/pl001_0000/arm/mod/m_arm001_0000.mod3",
            "nativePC/pl/m_equip/pl001_0000/body/mod/m_body001_0000_ALBD.tex.1",
        ];
        ModRecord armor = CreateMod(paths, 1, files);
        LooseModelRewriteReport armorReport = new LooseModelRewriteService(paths, Fs)
            .Rewrite(armor, "world:pl/m/001_0000", "world:pl/m/002_0010");
        Assert.Equal(2, armorReport.FilesMoved);
        Assert.Contains(armor.RelativeFiles, f => f.Contains("pl002_0010", StringComparison.Ordinal));
        Assert.Contains(armor.RelativeFiles, f => f.EndsWith("_ALBD.tex.1", StringComparison.Ordinal));

        string filesDir = paths.FilesDirectory(2);
        Directory.CreateDirectory(filesDir);
        string[] weaponFiles =
        [
            "nativePC/wp/gun/bs_gun001/mod/bs_gun001.mdf2.45",
            "nativePC/wp/gun/bs_gun001/mod/bs_sld001.mdf2.45",
            "nativePC/wp/gun/bs_gun001/mod/bs_gun001_ALBD.tex.1",
        ];
        ModRecord weapon = CreateMod(paths, 2, weaponFiles);
        LooseModelRewriteReport weaponReport = new LooseModelRewriteService(paths, Fs)
            .Rewrite(weapon, "world:wp/bs_gun001", "world:wp/bs_gun002");
        Assert.Equal(2, weaponReport.FilesMoved);
        Assert.Contains(weapon.RelativeFiles, f => f.Contains("bs_gun002", StringComparison.Ordinal));
        Assert.Contains(weapon.RelativeFiles, f => f.Contains("bs_sld002", StringComparison.Ordinal));
        Assert.Contains(weapon.RelativeFiles, f => f.EndsWith("bs_gun001_ALBD.tex.1", StringComparison.Ordinal));
    }

    [Fact]
    public void LooseRewrite_WorldOpPart_OnlyForOpIdentity()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        const string opPart = "nativePC/wp/bow/parts/op_bow005";
        ModRecord mod = CreateMod(paths, 1, [opPart]);

        LooseModelRewriteReport report = new LooseModelRewriteService(paths, Fs)
            .Rewrite(mod, "world:wp/bow001/op005", "world:wp/bow002/op006");

        Assert.Equal(1, report.FilesMoved);
        Assert.Contains("nativePC/wp/bow/parts/op_bow006", mod.RelativeFiles);
    }

    [Fact]
    public void LooseRewrite_CrossGameIdentity_Throws()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = CreateMod(paths, 1, [RiseArmorMesh]);
        LooseModelRewriteService service = new(paths, Fs);

        Assert.Throws<ArgumentException>(() => service.Rewrite(mod, "rise:pl/f/001", "ch02/012/003"));
        Assert.Throws<ArgumentException>(() => service.Rewrite(mod, "rise:pl/f/001", "rise:pl/m/002"));
    }

    // ---------------------------------------------------------------- pak 改写

    [Fact]
    public void Coordinator_RisePak_RenamesTocHashesOnly()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        const string mesh = "natives/stm/weapon/Bow/Bow001/Bow001.mdf2.23";
        const string part = "natives/stm/weapon/Bow/Bow001/B_Ydt001.mdf2.23";
        const string meshNew = "natives/stm/weapon/Bow/Bow002/Bow002.mdf2.23";
        const string partNew = "natives/stm/weapon/Bow/Bow002/B_Ydt002.mdf2.23";
        WritePak(Full(paths, 1, "mod.pak"), 0,
            (mesh, Encoding.ASCII.GetBytes("mesh"), 0L),
            (part, Encoding.ASCII.GetBytes("part"), 0L));
        byte[] original = File.ReadAllBytes(Full(paths, 1, "mod.pak"));
        ModRecord mod = new() { Id = 1, Index = 1, GameId = "mhr", RelativeFiles = ["mod.pak"] };
        PakNameIndex index = PakPathHasher.BuildIndex([mesh, part]);
        ModelRewriteCoordinator coordinator = new(paths, CreatePakService(), Fs);

        ModelRewriteSummary summary = coordinator.Rewrite(
            mod,
            [("rise:wp/Bow/001", "rise:wp/Bow/002")],
            index,
            ModelGame.Rise);

        Assert.Equal(1, summary.PaksScanned);
        Assert.Equal(2, summary.EntriesChanged);
        Assert.Empty(summary.UnsupportedPaks);
        Assert.True(File.Exists(Full(paths, 1, "mod.pak") + ".toc.bak"));

        PakArchiveInfo archive = new RePakReader().Read(Full(paths, 1, "mod.pak"));
        Assert.Equal(EngineHash(meshNew), archive.Entries[0].Hash);
        Assert.Equal(EngineHash(partNew), archive.Entries[1].Hash);

        byte[] updated = File.ReadAllBytes(Full(paths, 1, "mod.pak"));
        Assert.Equal(original.Length, updated.Length);
        for (int i = 0; i < original.Length; i++)
        {
            bool hashByte = i is >= 16 and < 24 or >= 64 and < 72;
            Assert.True(hashByte || original[i] == updated[i], $"offset={i} 不应变化");
        }
    }

    [Fact]
    public void Coordinator_OldOverload_ResolvesGameFromMod()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        const string mesh = "natives/stm/weapon/Bow/Bow001/Bow001.mdf2.23";
        WritePak(Full(paths, 1, "mod.pak"), 0, (mesh, Encoding.ASCII.GetBytes("mesh"), 0L));
        ModRecord mod = new() { Id = 1, Index = 1, GameId = "mhr", RelativeFiles = ["mod.pak"] };
        PakNameIndex index = PakPathHasher.BuildIndex([mesh]);
        ModelRewriteCoordinator coordinator = new(paths, CreatePakService(), Fs);

        ModelRewriteSummary summary = coordinator.Rewrite(
            mod,
            [("rise:wp/Bow/001", "rise:wp/Bow/002")],
            index,
            ModelGame.Rise);

        Assert.Equal(1, summary.EntriesChanged);
    }

    [Fact]
    public void Coordinator_WorldSkipsPaks_ButMovesLooseFiles()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        const string mesh = "nativePC/wp/bow/bow001/mod/bow001.mdf2.45";
        const string meshNew = "nativePC/wp/bow/bow002/mod/bow002.mdf2.45";
        WritePak(Full(paths, 1, "mod.pak"), 0, (mesh, Encoding.ASCII.GetBytes("unused"), 0L));
        byte[] original = File.ReadAllBytes(Full(paths, 1, "mod.pak"));
        WriteLoose(paths, 1, mesh);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = "mhw",
            RelativeFiles = ["mod.pak", mesh],
        };
        ModelRewriteCoordinator coordinator = new(paths, CreatePakService(), Fs);

        ModelRewriteSummary summary = coordinator.Rewrite(
            mod,
            [("world:wp/bow001", "world:wp/bow002")],
            new PakNameIndex(),
            ModelGame.World);

        Assert.Equal(0, summary.PaksScanned);
        Assert.Equal(0, summary.EntriesChanged);
        Assert.Equal(1, summary.FilesMoved);
        Assert.Contains(summary.Warnings, w => w.Contains("pak", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(original, File.ReadAllBytes(Full(paths, 1, "mod.pak")));
        Assert.False(File.Exists(Full(paths, 1, "mod.pak") + ".toc.bak"));
        Assert.True(File.Exists(Full(paths, 1, meshNew)));
    }

    // ---------------------------------------------------------------- 测试辅助

    private static ModRecord CreateMod(BoxPaths paths, int id, IEnumerable<string> files)
    {
        List<string> relative = [.. files];
        ModRecord mod = new() { Id = id, Index = id, RelativeFiles = relative };
        foreach (string file in relative)
        {
            string full = Full(paths, id, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "path=" + file);
        }

        return mod;
    }

    private static string Full(BoxPaths paths, int id, string relative) =>
        Path.Combine(paths.FilesDirectory(id), relative.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteLoose(BoxPaths paths, int id, string relative)
    {
        string full = Full(paths, id, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "path=" + relative);
    }

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

    private static void WritePak(
        string pakPath,
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

        Directory.CreateDirectory(Path.GetDirectoryName(pakPath)!);
        File.WriteAllBytes(pakPath, memory.ToArray());
    }
}
