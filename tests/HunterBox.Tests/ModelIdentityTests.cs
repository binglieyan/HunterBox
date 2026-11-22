using HunterBox.Domain.Models;

namespace HunterBox.Tests;

/// <summary>完整模型身份的解析与路径改写回归。</summary>
public class ModelIdentityTests
{
    // ------------------------------------------------------------------ 身份解析

    [Theory]
    [InlineData("ch03/012/003", ModelKind.Armor, 3, 12, 3)]
    [InlineData("ch02/000/900", ModelKind.Armor, 2, 0, 900)]
    [InlineData("ch05/003/0001", ModelKind.CatEquip, 5, 3, 1)]
    [InlineData("it10/03/0000", ModelKind.Weapon, 14, 3, 0)]
    [InlineData("wp10/03/0000", ModelKind.Weapon, 14, 3, 0)]
    [InlineData("it03/00/0017", ModelKind.Weapon, 3, 0, 17)]
    public void TryParse_ReadsCompleteIdentityForms(string text, ModelKind kind, int type, int first, int second)
    {
        Assert.True(ModelIdentity.TryParse(text, out ModelIdentity identity));
        Assert.Equal(kind, identity.Kind);
        Assert.Equal(type, identity.Type);
        Assert.Equal(first, identity.FirstId);
        Assert.Equal(second, identity.SecondId);
    }

    [Theory]
    [InlineData("012_003")]
    [InlineData("10_0000")]
    [InlineData("ch03/012")]
    [InlineData("ch04/012/003")]
    [InlineData("it14/01/0001")]
    [InlineData("it99/01/0001")]
    [InlineData("wp14/01/0001")]
    [InlineData("wp99/01/0001")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsPartialOrInvalidForms(string? text)
    {
        Assert.False(ModelIdentity.TryParse(text, out _));
    }

    [Theory]
    [InlineData("it14/01/0001")]
    [InlineData("it99/01/0001")]
    [InlineData("wp14/01/0001")]
    [InlineData("wp99/01/0001")]
    public void TryParse_OutOfRangeWeaponType_ReturnsFalseWithoutThrowing(string text)
    {
        Assert.False(ModelIdentity.TryParse(text, out ModelIdentity identity));
        Assert.Equal(ModelGame.Unknown, identity.Game);
    }

    [Fact]
    public void WeaponFactory_StillRejectsOutOfRangePathType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelIdentity.Weapon(14, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelIdentity.Weapon(99, 1, 1));
    }

    [Fact]
    public void PathForm_RoundTripsAndKeepsRodInsectPrefix()
    {
        Assert.Equal("ch03/012/003", ModelIdentity.Armor(3, 12, 3).PathForm);
        Assert.Equal("ch05/003/0001", ModelIdentity.Cat(3, 1).PathForm);
        // 猎虫身份类型是 14，但游戏路径仍是 it10（游戏内类型编号 特判）
        ModelIdentity insect = ModelIdentity.Weapon(10, 3, 0);
        Assert.Equal(ModelTypes.RodInsect, insect.Type);
        Assert.Equal("it10/03/0000", insect.PathForm);
        Assert.Equal("03_0000", insect.Code);
        Assert.Equal("10_0000", ModelIdentity.Weapon(10, 10, 0).Code);
    }

    [Theory]
    [InlineData("natives/STM/Art/Model/Character/ch03/012/003/5/ch03_012_0035.mesh.1")]
    [InlineData("natives/stm/art/model/character/ch02/012/003/x.mod3")]
    [InlineData("natives/STM/Art/Model/Item/it10/03/0000/it1003_0000_0.mdf2.45")]
    [InlineData("natives/stm/gamedesign/equip/_prefab/armor/female/012/003/012_003_avp.user.3")]
    [InlineData("natives/stm/gamedesign/equip/_prefab/weapon/wp10/03/0000/it1003_0000_0.pfb.18")]
    [InlineData("reframework/data/bonesystem/ch03_012_0031")]
    [InlineData("natives/stm/art/model/character/ch05/000/0000/ch05_000_0000.chain2.13")]
    public void TryParsePath_RecognizesWildsModelPaths(string path)
    {
        Assert.True(ModelIdentity.TryParsePath(path, out _));
    }

    [Theory]
    [InlineData("natives/stm/art/model/item/it14/01/0001/x.mdf2.45")]
    [InlineData("natives/stm/art/model/item/it99/01/0001/x.mdf2.45")]
    [InlineData("natives/stm/gamedesign/equip/_prefab/weapon/wp14/01/0001/x.pfb.18")]
    [InlineData("natives/stm/gamedesign/equip/_prefab/weapon/wp99/01/0001/x.pfb.18")]
    public void TryParsePath_OutOfRangeWeaponType_ReturnsFalseWithoutThrowing(string path)
    {
        Assert.False(ModelIdentity.TryParsePath(path, out _));
        Assert.False(ModelIdentity.TryParsePath(path, ModelGame.Wilds, out _));
        Assert.Empty(ModelPathRecognizer.FindIdentities([path]));
        Assert.Empty(ModelPathRecognizer.FindIdentities([path], ModelGame.Wilds));
    }

    [Theory]
    [InlineData("natives/stm/art/model/character/ch02/012/0034/x.mod3")]
    [InlineData("natives/stm/art/model/character/ch03/012/0034/x.mod3")]
    public void TryParsePath_WildsArmorWithFourDigitSid_IsRejectedInsteadOfTruncated(string path)
    {
        Assert.False(ModelIdentity.TryParsePath(path, ModelGame.Wilds, out ModelIdentity identity));
        Assert.Equal(ModelGame.Unknown, identity.Game);
    }

    [Theory]
    [InlineData("natives/stm/player/hair/x.mesh")]
    [InlineData("nativepc/plugins/x.lua")]
    [InlineData("natives/stm/sound/bgm.bnk")]
    [InlineData("reframework/autorun/x.lua")]
    [InlineData("")]
    public void TryParsePath_IgnoresNonModelPaths(string path)
    {
        Assert.False(ModelIdentity.TryParsePath(path, out _));
    }

    // -------------------------------------------------------------- 护甲完整身份改写

    [Fact]
    public void RewriteIdentityPath_Armor_RewritesBothIdsAndFileNames()
    {
        ModelIdentity from = ModelIdentity.Armor(2, 12, 3);
        ModelIdentity to = ModelIdentity.Armor(2, 15, 7);

        const string mesh = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.241111606";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string newMesh));
        Assert.Equal(
            "natives/stm/art/model/character/ch02/015/007/5/ch02_015_0075.mesh.241111606",
            newMesh);

        const string prefab = "natives/stm/gamedesign/equip/_prefab/armor/male/012/003/body/ch02_012_0032.pfb.18";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(prefab, from, to, out string newPrefab));
        Assert.Equal(
            "natives/stm/gamedesign/equip/_prefab/armor/male/015/007/body/ch02_015_0072.pfb.18",
            newPrefab);

        const string avp = "natives/stm/gamedesign/equip/_prefab/armor/male/012/003/012_003_avp.user.3";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(avp, from, to, out string newAvp));
        Assert.Equal(
            "natives/stm/gamedesign/equip/_prefab/armor/male/015/007/015_007_avp.user.3",
            newAvp);

        const string bone = "reframework/data/bonesystem/ch02_012_0031";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(bone, from, to, out string newBone));
        Assert.Equal("reframework/data/bonesystem/ch02_015_0071", newBone);
    }

    [Fact]
    public void RewriteIdentityPath_Armor_PreservesCaseAndOtherNumberSegments()
    {
        const string path =
            "natives/STM/Art/Model/Character/CH02/012/003/5/ch02_012_0035.mesh.1";

        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Armor(2, 12, 3),
            ModelIdentity.Armor(2, 15, 7),
            out string rewritten));
        Assert.Equal("natives/STM/Art/Model/Character/CH02/015/007/5/ch02_015_0075.mesh.1", rewritten);
    }

    [Fact]
    public void RewriteIdentityPath_SkipsTextureAndWrongSexPaths()
    {
        ModelIdentity from = ModelIdentity.Armor(2, 12, 3);
        ModelIdentity to = ModelIdentity.Armor(2, 15, 7);

        const string tex =
            "natives/stm/art/model/character/ch02/012/003/5/textures/ch02_012_0035_ALBD.tex.241106027";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, from, to, out string sameTex));
        Assert.Equal(tex, sameTex);

        // 女款路径不能被男款身份改写（同 fid/sid、不同 ch03 组）
        const string female = "natives/stm/art/model/character/ch03/012/003/5/ch03_012_0035.mesh.1";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(female, from, to, out string sameFemale));
        Assert.Equal(female, sameFemale);
    }

    [Fact]
    public void RewriteIdentityPath_DoesNotTouchNonModelPaths()
    {
        const string path = "natives/stm/art/model/character/ch02/012/003/readme.txt";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Armor(2, 12, 3),
            ModelIdentity.Armor(2, 15, 7),
            out string rewritten));
        Assert.Equal("natives/stm/art/model/character/ch02/015/007/readme.txt", rewritten);

        const string lua = "reframework/autorun/ch02_012_003.lua";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(
            lua,
            ModelIdentity.Armor(2, 12, 3),
            ModelIdentity.Armor(2, 15, 7),
            out string sameLua));
        Assert.Equal(lua, sameLua);
    }

    [Fact]
    public void RewriteIdentityPath_Armor_SidOnlyChangeMatches()
    {
        const string path = "natives/stm/art/model/character/ch03/010/012/5/ch03_010_0125.mesh.1";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Armor(3, 10, 12),
            ModelIdentity.Armor(3, 10, 15),
            out string rewritten));
        Assert.Equal("natives/stm/art/model/character/ch03/010/015/5/ch03_010_0155.mesh.1", rewritten);
    }

    [Fact]
    public void RewriteIdentityPath_Armor_RewritesLuaBoneSystemPaths()
    {
        const string path = "reframework/data/lua bone system/custom/ch03_012_0031";

        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Armor(3, 12, 3),
            ModelIdentity.Armor(3, 15, 7),
            out string rewritten));
        Assert.Equal("reframework/data/lua bone system/custom/ch03_015_0071", rewritten);
    }

    // -------------------------------------------------------------- 武器完整身份改写

    [Fact]
    public void RewriteIdentityPath_Weapon_RewritesSeriesAndFileName()
    {
        ModelIdentity from = ModelIdentity.Weapon(10, 3, 0); // 猎虫：类型 14、路径 it10
        ModelIdentity to = ModelIdentity.Weapon(10, 5, 1);
        Assert.Equal(14, from.Type);

        const string mesh = "natives/STM/Art/Model/Item/it10/03/0000/it1003_0000_0.mdf2.45";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string newMesh));
        Assert.Equal("natives/STM/Art/Model/Item/it10/05/0001/it1005_0001_0.mdf2.45", newMesh);

        const string prefab = "natives/STM/GameDesign/Equip/_Prefab/Weapon/Wp10/03/0000/it1003_0000_0.pfb.18";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(prefab, from, to, out string newPrefab));
        Assert.Equal(
            "natives/STM/GameDesign/Equip/_Prefab/Weapon/Wp10/05/0001/it1005_0001_0.pfb.18",
            newPrefab);

        const string tex = "natives/STM/Art/Model/Item/it10/03/0000/textures/it1003_0000_0_ALBD.tex.1";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, from, to, out _));
    }

    [Fact]
    public void RewriteIdentityPath_Weapon_NormalBowDoesNotUseRodInsectSlot()
    {
        const string path = "natives/STM/Art/Model/Item/it11/00/0000/it1100_0000_0.mdf2.45";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Weapon(11, 0, 0),
            ModelIdentity.Weapon(11, 10, 0),
            out string rewritten));
        Assert.Equal("natives/STM/Art/Model/Item/it11/10/0000/it1110_0000_0.mdf2.45", rewritten);

        // it10/03 是猎虫，不能按操虫棍（AA≠3）身份改写
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Weapon(10, 0, 0),
            ModelIdentity.Weapon(10, 1, 0),
            out _));
    }

    // -------------------------------------------------------------- 猫装备完整身份改写

    [Fact]
    public void RewriteIdentityPath_CatEquip_RewritesFourDigitSid()
    {
        const string path = "natives/stm/art/model/character/ch05/003/0001/ch05_003_0001.mesh.241111606";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(
            path,
            ModelIdentity.Cat(3, 1),
            ModelIdentity.Cat(4, 2),
            out string rewritten));
        Assert.Equal(
            "natives/stm/art/model/character/ch05/004/0002/ch05_004_0002.mesh.241111606",
            rewritten);

        const string tex = "natives/stm/art/model/character/ch05/003/0001/Textures/ch05_003_0001_ALBD.tex.1";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, ModelIdentity.Cat(3, 1), ModelIdentity.Cat(4, 2), out _));
    }

    // ------------------------------------------------------------------ 参数校验

    [Fact]
    public void TryRewriteIdentityPath_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModelIdRewriter.TryRewriteIdentityPath(
            string.Empty,
            ModelIdentity.Armor(2, 12, 3),
            ModelIdentity.Armor(2, 15, 7),
            out _));
    }

    [Fact]
    public void TryRewriteIdentityPath_KindOrTypeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModelIdRewriter.TryRewriteIdentityPath(
            "natives/stm/art/model/character/ch02/012/003/x",
            ModelIdentity.Armor(2, 12, 3),
            ModelIdentity.Armor(3, 15, 7), out _));

        Assert.Throws<ArgumentException>(() => ModelIdRewriter.TryRewriteIdentityPath(
            "natives/stm/art/model/item/it10/03/0000/x",
            ModelIdentity.Weapon(10, 3, 0),
            ModelIdentity.Weapon(11, 5, 0), out _));

        Assert.Throws<ArgumentException>(() => ModelIdRewriter.TryRewriteIdentityPath(
            "natives/stm/art/model/character/ch02/012/003/x",
            ModelIdentity.Armor(2, 12, 3),
            ModelIdentity.Cat(15, 7), out _));
    }

    // -------------------------------------------------------------- 识别器/三作判定

    [Fact]
    public void FindIdentities_MergesModelAndPrefabPathsOfSameIdentity()
    {
        string[] paths =
        [
            "natives/stm/art/model/character/ch03/012/003/5/ch03_012_0035.mesh.1",
            "natives/stm/gamedesign/equip/_prefab/armor/female/012/003/Arm/ch03_012_0031.pfb.18",
        ];

        ModelIdentity identity = Assert.Single(ModelPathRecognizer.FindIdentities(paths));
        Assert.Equal(ModelIdentity.Armor(3, 12, 3), identity);
    }

    [Fact]
    public void FindModelIds_StillAcceptsLegacyShortArmorPaths_AndAddsCat()
    {
        Assert.Equal(["ch03/012"], ModelPathRecognizer.FindModelIds(
            ["natives/stm/art/model/character/ch03/012/body.mod3"]));
        Assert.Equal(["ch05/003/0001"], ModelPathRecognizer.FindModelIds(
            ["natives/stm/art/model/character/ch05/003/0001/ch05_003_0001.mesh.1"]));
    }

    [Theory]
    [InlineData("natives/stm/art/model/character/ch03/012/003/x", ModelGame.Wilds)]
    [InlineData("natives/stm/player/mod/f/pl060/000/x", ModelGame.Rise)]
    [InlineData("nativePC/pl/f_equip/pl060_0000/body/mod/x.mod3", ModelGame.World)]
    public void TryParsePath_IdentifiesGameByPath(string path, ModelGame expected)
    {
        Assert.True(ModelIdentity.TryParsePath(path, out ModelIdentity identity));
        Assert.Equal(expected, identity.Game);
    }

    [Fact]
    public void TryParsePath_UnknownForNoModelPaths()
    {
        Assert.False(ModelIdentity.TryParsePath("reframework/autorun/x.lua", out _));
    }
}
