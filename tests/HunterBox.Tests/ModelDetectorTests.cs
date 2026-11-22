using HunterBox.Application.Models;
using HunterBox.Domain.Models;

namespace HunterBox.Tests;

/// <summary>模型行去重必须按「完整身份 + pak」分行。</summary>
public class ModelDetectorTests
{
    private const string ArmorMale = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";
    private const string ArmorFemale = "natives/stm/art/model/character/ch03/012/003/5/ch03_012_0035.mesh.1";
    private const string ArmorPrefab = "natives/stm/gamedesign/equip/_prefab/armor/male/012/003/body/ch02_012_0032.pfb.18";
    private const string BowZero = "natives/stm/art/model/item/it11/00/0000/it1100_0000_0.mdf2.45";
    private const string BowTen = "natives/stm/art/model/item/it11/10/0000/it1110_0000_0.mdf2.45";
    private const string GreatSwordZero = "natives/stm/art/model/item/it00/00/0000/it0000_0000_0.mdf2.45";

    [Fact]
    public void Detect_ArmorMaleAndFemaleOfSameFid_SeparateRows()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect([(ArmorMale, null), (ArmorFemale, null)]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Identity == ModelIdentity.Armor(2, 12, 3));
        Assert.Contains(rows, r => r.Identity == ModelIdentity.Armor(3, 12, 3));
    }

    [Fact]
    public void Detect_SameBbbbDifferentWeaponTypes_SeparateRows()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
            [(BowZero, null), (GreatSwordZero, null), (BowTen, null)]);

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Identity.Type == ModelTypes.Bow && r.Identity.FirstId == 0);
        Assert.Contains(rows, r => r.Identity.Type == ModelTypes.Bow && r.Identity.FirstId == 10);
        Assert.Contains(rows, r => r.Identity.Type == ModelTypes.LongSword);
    }

    [Fact]
    public void Detect_SameIdentityInTwoPaks_SeparateRowsWithPakName()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
            [(ArmorMale, "a.pak"), (ArmorMale, "b.pak"), (ArmorFemale, null)]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.PakFile is not null));
        Assert.Contains(rows, r => r.PakFile == "a.pak");
        Assert.Contains(rows, r => r.PakFile == "b.pak");
    }

    [Fact]
    public void Detect_CountsFilesAndFlags()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            (ArmorMale, null),
            ("natives/stm/art/model/character/ch02/012/003/5/textures/ch02_012_0035_ALBD.tex.1", null),
            ("natives/stm/art/model/character/ch02/012/003/0/ch02_012_003.chain2.13", null),
            (ArmorPrefab, null),
        ]);

        DetectedModel row = Assert.Single(rows);
        Assert.Equal(4, row.FileCount);
        Assert.True(row.HasPrefab);
        Assert.True(row.HasChain);
        Assert.Equal("012_003", row.RewriteId);
        Assert.Equal("ch02/012/003", row.IdentityForm);
        Assert.Equal("护甲(男款)", row.TypeDisplayName);
    }

    [Fact]
    public void Detect_CatEquipRow_IsRecognizedWithCatKind()
    {
        const string cat = "natives/stm/art/model/character/ch05/003/0001/ch05_003_0001.mesh.1";

        DetectedModel row = Assert.Single(ModelDetector.Detect([(cat, null)]));
        Assert.Equal(ModelKind.CatEquip, row.Identity.Kind);
        Assert.Equal("003_0001", row.RewriteId);
        Assert.Equal("ch05/003/0001", row.IdentityForm);
        Assert.Equal("猫装备", row.TypeDisplayName);
    }

    [Fact]
    public void Detect_PrefabOnlyMod_StillCreatesRowWithPrefabFlag()
    {
        const string prefab = "natives/stm/gamedesign/equip/_prefab/armor/male/012/003/body/ch02_012_0032.pfb.18";

        DetectedModel row = Assert.Single(ModelDetector.Detect([(prefab, null)]));
        Assert.True(row.HasPrefab);
        Assert.Equal(ModelIdentity.Armor(2, 12, 3), row.Identity);
    }

    [Fact]
    public void Detect_BoneOnlyEntries_DoNotCreateRows()
    {
        Assert.Empty(ModelDetector.Detect(
            [("reframework/data/bonesystem/ch02_012_0031", null)]));
    }

    [Fact]
    public void Detect_UsesPakSourceAndKeepsStableOrder()
    {
        IReadOnlyList<DetectedModel> rows = ModelDetector.Detect(
        [
            (BowZero, "z.pak"),
            (ArmorMale, "a.pak"),
            (BowZero, "z.pak"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ModelKind.Armor, rows[0].Identity.Kind); // 护甲排在武器前（排序稳定）
        Assert.Equal(2, rows.Single(r => r.Identity.IsWeapon).FileCount);
    }
}
