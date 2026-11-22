using HunterBox.Domain.Models;

namespace HunterBox.Tests;

/// <summary>三作（荒野/崛起/世界）模型路径识别、身份形式与 Keys 模板改写。</summary>
public class ModelGameRulesTests
{
    // ---------------------------------------------------------------- 崛起路径

    [Theory]
    [InlineData("natives/STM/player/mod/f/pl001/f_arm001.mdf2.23", ModelGame.Rise, ModelKind.Armor, 3, 1, 0)]
    [InlineData("natives/STM/player/prefab/mod/m/pl002/m_body002.pfb.17", ModelGame.Rise, ModelKind.Armor, 2, 2, 0)]
    [InlineData("natives/STM/weapon/LongSword/L_Swd001/L_Swd001.mdf2.23", ModelGame.Rise, ModelKind.Weapon, 3, 1, 0)]
    [InlineData("natives/STM/player/prefab/weapon/Bow/B_Bow001.pfb.17", ModelGame.Rise, ModelKind.Weapon, 11, 1, 0)]
    [InlineData("natives/STM/weapon/IG_Insect/IG_Ins001/IG_Ins001.mdf2.23", ModelGame.Rise, ModelKind.Weapon, 14, 1, 0)]
    [InlineData("reframework/data/bonesystem/f_arm001", ModelGame.Rise, ModelKind.Armor, 3, 1, 0)]
    [InlineData("reframework/data/lua bone system/custom/m_leg007", ModelGame.Rise, ModelKind.Armor, 2, 7, 0)]
    public void TryParsePath_RisePaths(
        string path,
        ModelGame game,
        ModelKind kind,
        int type,
        int first,
        int second)
    {
        Assert.True(ModelIdentity.TryParsePath(path, out ModelIdentity identity));
        Assert.Equal(game, identity.Game);
        Assert.Equal(kind, identity.Kind);
        Assert.Equal(type, identity.Type);
        Assert.Equal(first, identity.FirstId);
        Assert.Equal(second, identity.SecondId);
    }

    // ---------------------------------------------------------------- 世界路径

    [Theory]
    [InlineData(
        "nativePC/pl/f_equip/pl060_0000/body/mod/f_body060_0000.mod3",
        ModelGame.World, ModelKind.Armor, 3, 60, 0)]
    [InlineData(
        "nativePC/pl/m_equip/pl002_0010/arm/mod/m_arm002_0010.mod3",
        ModelGame.World, ModelKind.Armor, 2, 2, 10)]
    [InlineData(
        "nativePC/wp/bow/bs_bow001/mod/bs_bow001.mdf2.45",
        ModelGame.World, ModelKind.Weapon, 11, 1, 10001)]
    [InlineData(
        "nativePC/wp/swo/swo003/mod/swo003.mod3",
        ModelGame.World, ModelKind.Weapon, 3, 3, 30000)]
    public void TryParsePath_WorldPaths(
        string path,
        ModelGame game,
        ModelKind kind,
        int type,
        int first,
        int second)
    {
        Assert.True(ModelIdentity.TryParsePath(path, out ModelIdentity identity));
        Assert.Equal(game, identity.Game);
        Assert.Equal(kind, identity.Kind);
        Assert.Equal(type, identity.Type);
        Assert.Equal(first, identity.FirstId);
        Assert.Equal(second, identity.SecondId);
    }

    [Theory]
    [InlineData("nativepc/plugins/x.lua")]
    [InlineData("natives/stm/player/hair/x.mesh")]
    [InlineData("natives/stm/sound/bgm.bnk")]
    [InlineData("reframework/autorun/x.lua")]
    [InlineData("nativePC/wp/bow/parts/op_bow005")]
    [InlineData("")]
    public void TryParsePath_NonModelPaths_ReturnFalse(string path)
    {
        Assert.False(ModelIdentity.TryParsePath(path, out _));
    }

    // ---------------------------------------------------------------- 身份形式

    [Fact]
    public void PathForms_RoundTripForAllGames()
    {
        AssertRoundTrip(ModelIdentity.Armor(3, 12, 3));
        AssertRoundTrip(ModelIdentity.Cat(3, 1));
        AssertRoundTrip(ModelIdentity.Weapon(10, 3, 0));
        AssertRoundTrip(ModelIdentity.RiseArmor(60, female: false));
        AssertRoundTrip(ModelIdentity.RiseArmor(60, female: true));
        AssertRoundTrip(ModelIdentity.RiseWeapon(ModelTypes.Bow, 1));
        AssertRoundTrip(ModelIdentity.RiseWeapon(ModelTypes.RodInsect, 26));
        AssertRoundTrip(ModelIdentity.WorldArmor(2, 60, 0));
        AssertRoundTrip(ModelIdentity.WorldArmor(3, 2, 10));
        AssertRoundTrip(ModelIdentity.WorldWeapon(ModelTypes.Bow, 10001));
        AssertRoundTrip(ModelIdentity.WorldWeapon(ModelTypes.TwinSword, 5, withBs: true, opId: 4));

        Assert.Equal("rise:pl/m/060", ModelIdentity.RiseArmor(60, false).PathForm);
        Assert.Equal("rise:pl/f/060", ModelIdentity.RiseArmor(60, true).PathForm);
        Assert.Equal("rise:wp/Bow/001", ModelIdentity.RiseWeapon(ModelTypes.Bow, 1).PathForm);
        Assert.Equal("world:pl/m/060_0000", ModelIdentity.WorldArmor(2, 60, 0).PathForm);
        Assert.Equal("world:wp/bs_bow001/op005", ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, true, 5).PathForm);
    }

    [Fact]
    public void Codes_AreGameSpecific()
    {
        Assert.Equal("012_003", ModelIdentity.Armor(2, 12, 3).Code);
        Assert.Equal("pl060", ModelIdentity.RiseArmor(60, false).Code);
        Assert.Equal("pl060_0000", ModelIdentity.WorldArmor(2, 60, 0).Code);
        Assert.Equal("G_Swd001", ModelIdentity.RiseWeapon(ModelTypes.LongSword, 1).Code);
        Assert.Equal("IG_Ins026", ModelIdentity.RiseWeapon(ModelTypes.RodInsect, 26).Code);
        Assert.Equal("bs_bow001", ModelIdentity.WorldWeapon(ModelTypes.Bow, 10001).Code);
        Assert.Equal("bow001:op_bow005", ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, false, 5).Code);
    }

    [Fact]
    public void PathForm_RejectsMismatchedGameAndType()
    {
        Assert.Throws<ArgumentException>(() => ModelIdRewriter.TryRewriteIdentityPath(
            "nativepc/wp/bow/bow001/mod/bow001.mdf2",
            ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, false, 0),
            ModelIdentity.RiseWeapon(ModelTypes.Bow, 2),
            out _));
        Assert.Throws<ArgumentException>(() => ModelIdRewriter.TryRewriteIdentityPath(
            "natives/stm/weapon/Bow/Bow001/Bow001.mdf2",
            ModelIdentity.RiseWeapon(ModelTypes.Bow, 1),
            ModelIdentity.RiseWeapon(ModelTypes.LongSword, 2),
            out _));
    }

    // ---------------------------------------------------------------- 崛起改写

    [Fact]
    public void RewriteIdentityPath_RiseArmor_RewritesModelPrefabAndBones()
    {
        ModelIdentity from = ModelIdentity.RiseArmor(1, female: true);
        ModelIdentity to = ModelIdentity.RiseArmor(2, female: true);

        const string mesh = "natives/STM/player/mod/f/pl001/f_arm001.mdf2.23";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string newMesh));
        Assert.Equal("natives/STM/player/mod/f/pl002/f_arm002.mdf2.23", newMesh);

        const string body = "natives/STM/player/mod/f/pl001/f_body001.mesh.2109148288";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(body, from, to, out string newBody));
        Assert.Equal("natives/STM/player/mod/f/pl002/f_body002.mesh.2109148288", newBody);

        const string prefab = "natives/STM/player/prefab/mod/f/pl001/f_helm001.pfb.17";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(prefab, from, to, out string newPrefab));
        Assert.Equal("natives/STM/player/prefab/mod/f/pl002/f_helm002.pfb.17", newPrefab);

        const string bone = "reframework/data/bonesystem/f_leg001";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(bone, from, to, out string newBone));
        Assert.Equal("reframework/data/bonesystem/f_leg002", newBone);

        const string luaBone = "reframework/data/lua bone system/custom/f_wst001";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(luaBone, from, to, out string newLuaBone));
        Assert.Equal("reframework/data/lua bone system/custom/f_wst002", newLuaBone);
    }

    [Fact]
    public void RewriteIdentityPath_RiseArmor_SkipsTextureAndOtherSex()
    {
        ModelIdentity from = ModelIdentity.RiseArmor(1, female: true);
        ModelIdentity to = ModelIdentity.RiseArmor(2, female: true);

        const string tex = "natives/STM/player/mod/f/pl001/f_arm001_ALBD.tex.28";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, from, to, out string sameTex));
        Assert.Equal(tex, sameTex);

        const string male = "natives/STM/player/mod/m/pl001/m_arm001.mdf2.23";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(male, from, to, out string sameMale));
        Assert.Equal(male, sameMale);
    }

    [Fact]
    public void RewriteIdentityPath_RiseWeapon_RewritesCodePartsAndPrefab()
    {
        ModelIdentity from = ModelIdentity.RiseWeapon(ModelTypes.Bow, 1);
        ModelIdentity to = ModelIdentity.RiseWeapon(ModelTypes.Bow, 2);

        const string mesh = "natives/STM/weapon/Bow/Bow001/B_Bow001.mdf2.23";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string newMesh));
        Assert.Equal("natives/STM/weapon/Bow/Bow002/B_Bow002.mdf2.23", newMesh);

        const string part = "natives/STM/weapon/Bow/Bow001/B_Ydt001.mdf2.23";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(part, from, to, out string newPart));
        Assert.Equal("natives/STM/weapon/Bow/Bow002/B_Ydt002.mdf2.23", newPart);

        const string prefab = "natives/STM/player/prefab/weapon/Bow/B_Bow001.pfb.17";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(prefab, from, to, out string newPrefab));
        Assert.Equal("natives/STM/player/prefab/weapon/Bow/B_Bow002.pfb.17", newPrefab);

        const string gimmick = "natives/STM/weapon/Bow/Gimmick/PartsGimmick/Bow_PartsGmk001.user.2";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(gimmick, from, to, out string sameGimmick));
        Assert.Equal(gimmick, sameGimmick);

        const string tex = "natives/STM/weapon/Bow/Bow001/Bow001_ALBD.tex.28";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, from, to, out _));
    }

    // ---------------------------------------------------------------- 世界改写

    [Fact]
    public void RewriteIdentityPath_WorldArmor_RewritesCodeAndPartFileNames()
    {
        ModelIdentity from = ModelIdentity.WorldArmor(2, 1, 0);
        ModelIdentity to = ModelIdentity.WorldArmor(2, 2, 10);

        const string mesh = "nativePC/pl/m_equip/pl001_0000/body/mod/m_body001_0000.mod3";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string newMesh));
        Assert.Equal("nativePC/pl/m_equip/pl002_0010/body/mod/m_body002_0010.mod3", newMesh);

        const string tex = "nativePC/pl/m_equip/pl001_0000/body/mod/m_body001_0000_ALBD.tex.1";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, from, to, out string sameTex));
        Assert.Equal(tex, sameTex);

        const string female = "nativePC/pl/f_equip/pl001_0000/body/mod/f_body001_0000.mod3";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(female, from, to, out string sameFemale));
        Assert.Equal(female, sameFemale);
    }

    [Fact]
    public void RewriteIdentityPath_WorldWeapon_RewritesCodeAndPartTokens()
    {
        ModelIdentity from = ModelIdentity.WorldWeapon(ModelTypes.GunLance, 1, true, 0);
        ModelIdentity to = ModelIdentity.WorldWeapon(ModelTypes.GunLance, 2, true, 0);

        const string mesh = "nativePC/wp/gun/bs_gun001/mod/bs_gun001.mdf2.45";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string newMesh));
        Assert.Equal("nativePC/wp/gun/bs_gun002/mod/bs_gun002.mdf2.45", newMesh);

        const string shield = "nativePC/wp/gun/bs_gun001/mod/bs_sld001.mdf2.45";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(shield, from, to, out string newShield));
        Assert.Equal("nativePC/wp/gun/bs_gun002/mod/bs_sld002.mdf2.45", newShield);

        const string epv = "nativePC/wp/gun/bs_gun001/epv/bs_gun001.pfb.18";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(epv, from, to, out string newEpv));
        Assert.Equal("nativePC/wp/gun/bs_gun002/epv/bs_gun002.pfb.18", newEpv);

        const string tex = "nativePC/wp/gun/bs_gun001/mod/bs_gun001_ALBD.tex.1";
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(tex, from, to, out _));
    }

    [Fact]
    public void RewriteIdentityPath_WorldWeapon_OpPartsOnlyRewrittenForOpIdentity()
    {
        const string opPath = "nativePC/wp/bow/parts/op_bow005";
        ModelIdentity baseFrom = ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, true, 0);
        ModelIdentity baseTo = ModelIdentity.WorldWeapon(ModelTypes.Bow, 2, true, 0);
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(opPath, baseFrom, baseTo, out _));

        ModelIdentity from = ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, true, 5);
        ModelIdentity to = ModelIdentity.WorldWeapon(ModelTypes.Bow, 2, true, 6);
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(opPath, from, to, out string rewritten));
        Assert.Equal("nativePC/wp/bow/parts/op_bow006", rewritten);

        // 基础武器身份 → 带 op 的目标：只改已有代码路径，不会给 op 文件改名
        Assert.False(ModelIdRewriter.TryRewriteIdentityPath(opPath, baseFrom, to, out _));
    }

    [Fact]
    public void RewriteIdentityPath_WorldWeapon_BsToggleChangesCodePrefix()
    {
        ModelIdentity from = ModelIdentity.WorldWeapon(ModelTypes.Bow, 1, false, 0);
        ModelIdentity to = ModelIdentity.WorldWeapon(ModelTypes.Bow, 2, true, 0);

        const string mesh = "nativePC/wp/bow/bow001/mod/bow001.mdf2.45";
        Assert.True(ModelIdRewriter.TryRewriteIdentityPath(mesh, from, to, out string rewritten));
        Assert.Equal("nativePC/wp/bow/bs_bow002/mod/bs_bow002.mdf2.45", rewritten);
    }

    private static void AssertRoundTrip(ModelIdentity identity)
    {
        Assert.True(ModelIdentity.TryParse(identity.PathForm, out ModelIdentity parsed), identity.PathForm);
        Assert.Equal(identity, parsed);
    }
}
