using HunterBox.Domain.Models;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Mods;
using HunterBox.Infrastructure.Paks;
using DomainIdentity = HunterBox.Domain.Models.ModelIdentity;
using ModelIdentity = HunterBox.Domain.Models.ModelIdentity;

namespace HunterBox.Tests;

/// <summary>三作名称表、目标候选、名称解析与 pak 路径清单。</summary>
public class ModelGameDataTests
{
    // ---------------------------------------------------------------- 名称表

    [Fact]
    public void EmbeddedNameTables_LoadRiseAndWorldTables()
    {
        IReadOnlyList<NameTableRow> riseArmors = EmbeddedNameTables.LoadArmors(ModelGame.Rise);
        IReadOnlyList<NameTableRow> riseWeapons = EmbeddedNameTables.LoadWeapons(ModelGame.Rise);
        IReadOnlyList<NameTableRow> worldArmors = EmbeddedNameTables.LoadArmors(ModelGame.World);
        IReadOnlyList<NameTableRow> worldWeapons = EmbeddedNameTables.LoadWeapons(ModelGame.World);

        Assert.Equal(239, riseArmors.Count);
        Assert.Equal(1055, riseWeapons.Count);
        Assert.Equal(274, worldArmors.Count);
        Assert.Equal(1387, worldWeapons.Count);
        Assert.All(riseArmors, row => Assert.Matches(@"^pl\d{3}$", row.Id));
        Assert.All(worldArmors, row => Assert.Matches(@"^pl\d{3}_\d{4}$", row.Id));
        Assert.All(riseWeapons, row => Assert.Matches(@"^\d{3,4}$", row.Id));
        Assert.Contains(worldWeapons, row => row.Id == "bow001" && row.OpCode is null);
        Assert.Contains(worldWeapons, row => row.Id == "bs_bow001" && row.OpCode == "op_bow001");

        // 猫装备表只有荒野有
        Assert.NotEmpty(EmbeddedNameTables.LoadCats(ModelGame.Wilds));
        Assert.Empty(EmbeddedNameTables.LoadCats(ModelGame.Rise));
        Assert.Empty(EmbeddedNameTables.LoadCats(ModelGame.World));
    }

    // ---------------------------------------------------------------- pak 清单

    [Fact]
    public void EmbeddedPakNameIndex_LoadRise_IndexesRisePaths()
    {
        PakNameIndex index = EmbeddedPakNameIndex.LoadRise();

        Assert.True(index.Count > 20_000, $"崛起清单条目过少：{index.Count}");
        const string path = "natives/STM/player/mod/f/pl001/f_arm001.mdf2.23";
        Assert.Equal(path, index.TryResolve(PakPathHasher.HashPath(path)));
    }

    [Fact]
    public void EmbeddedPakNameIndex_WorldReturnsEmpty()
    {
        Assert.Equal(0, EmbeddedPakNameIndex.Load(ModelGame.World).Count);
        Assert.Equal(
            EmbeddedPakNameIndex.LoadWilds().Count,
            EmbeddedPakNameIndex.Load(ModelGame.Wilds).Count);
        Assert.Equal(
            EmbeddedPakNameIndex.LoadRise().Count,
            EmbeddedPakNameIndex.Load(ModelGame.Rise).Count);
    }

    // ---------------------------------------------------------------- 候选表

    [Fact]
    public void ModelCatalog_RiseTargets()
    {
        ModelCatalog catalog = new(ModelGame.Rise);
        Assert.Equal(ModelGame.Rise, catalog.Game);

        IReadOnlyList<ModelChoice> armors = catalog.ArmorTargets;
        Assert.Equal(239, armors.Count);
        Assert.All(armors, t => Assert.Equal(ModelGame.Rise, t.Identity!.Game));
        Assert.All(armors, t => Assert.Matches(@"^pl\d{3}$", t.Code));
        Assert.Contains(armors, t => t.Code == "pl001" && t.Name == "皮制/皮制S/皮制X");
        Assert.All(armors, t => Assert.False(t.HasChain));

        Assert.Equal(80, catalog.WeaponTargets(ModelTypes.Bow).Count);
        Assert.Equal(69, catalog.WeaponTargets(ModelTypes.Rod).Count);
        Assert.Equal(26, catalog.WeaponTargets(ModelTypes.RodInsect).Count);

        ModelChoice bow = catalog.WeaponTargets(ModelTypes.Bow).First(t => t.Code == "BOW001");
        Assert.Equal("钢龙射手弓", bow.Name);
        Assert.Equal(ModelTypes.Bow, bow.Identity!.Type);
        Assert.Equal("rise:wp/Bow/001", bow.Identity.PathForm);

        Assert.Empty(catalog.CatTargets());
    }

    [Fact]
    public void ModelCatalog_WorldTargets()
    {
        ModelCatalog catalog = new(ModelGame.World);
        Assert.Equal(ModelGame.World, catalog.Game);

        IReadOnlyList<ModelChoice> armors = catalog.ArmorTargets;
        Assert.Equal(274, armors.Count);
        Assert.All(armors, t => Assert.Equal(ModelGame.World, t.Identity!.Game));
        Assert.All(armors, t => Assert.Matches(@"^pl\d{3}_\d{4}$", t.Code));
        Assert.Contains(armors, t => t.Code == "pl001_0000" && t.Name == "【皮制】服装");

        IReadOnlyList<ModelChoice> bows = catalog.WeaponTargets(ModelTypes.Bow);
        Assert.Equal(94, bows.Count);
        Assert.Equal(93, catalog.WeaponTargets(ModelTypes.Rod).Count);
        Assert.Equal(29, catalog.WeaponTargets(ModelTypes.RodInsect).Count);
        Assert.All(bows, t => Assert.Equal(ModelGame.World, t.Identity!.Game));

        ModelChoice baseBow = bows.First(t => t.Code == "bow001");
        Assert.Equal("钢冰马弓/钢龙强弓/钢龙射手弓", baseBow.Name);
        Assert.Equal("world:wp/bow001", baseBow.Identity!.PathForm);

        // op 变体以完整 ID 区分（同一基础代码的不同叠层不合并）
        ModelChoice opBow = bows.First(t => t.Code.Contains(":op_", StringComparison.Ordinal));
        Assert.Contains("op_", opBow.Code, StringComparison.Ordinal);
        Assert.Empty(catalog.CatTargets());
    }

    [Fact]
    public void ModelCatalog_WildsDefaultsRemainUnchanged()
    {
        ModelCatalog catalog = new();
        Assert.Equal(ModelGame.Wilds, catalog.Game);
        Assert.Equal(217, catalog.ArmorTargets.Count);
        Assert.Equal(32, catalog.WeaponTargets(ModelTypes.Bow).Count);
        Assert.Equal(34, catalog.WeaponTargets(ModelTypes.Rod).Count);
        Assert.Equal(21, catalog.WeaponTargets(ModelTypes.RodInsect).Count);
        Assert.Equal(EmbeddedNameTables.LoadCats().Count, catalog.CatTargets().Count);
    }

    [Fact]
    public void ModelCatalog_TargetCountsMatchTableRows()
    {
        AssertCatalogTotals(ModelGame.Wilds);
        AssertCatalogTotals(ModelGame.Rise);
        AssertCatalogTotals(ModelGame.World);
    }

    [Fact]
    public void ModelGameInfo_RoundTripsStableIds()
    {
        Assert.Equal("mhws", ModelGameInfo.GetId(ModelGame.Wilds));
        Assert.Equal("mhr", ModelGameInfo.GetId(ModelGame.Rise));
        Assert.Equal("mhw", ModelGameInfo.GetId(ModelGame.World));
        Assert.Equal(string.Empty, ModelGameInfo.GetId(ModelGame.Unknown));

        Assert.True(ModelGameInfo.TryParseId("mhr", out ModelGame rise));
        Assert.Equal(ModelGame.Rise, rise);
        Assert.True(ModelGameInfo.TryParseId("mhws", out ModelGame wilds));
        Assert.Equal(ModelGame.Wilds, wilds);
        Assert.True(ModelGameInfo.TryParseId("MHW", out ModelGame world));
        Assert.Equal(ModelGame.World, world);
        Assert.False(ModelGameInfo.TryParseId("xx", out _));
        Assert.False(ModelGameInfo.TryParseId(null, out _));
    }

    private static void AssertCatalogTotals(ModelGame game)
    {
        ModelCatalog catalog = new(game);
        int armorRows = EmbeddedNameTables.LoadArmors(game).Count;
        Assert.Equal(armorRows, catalog.ArmorTargets.Count);

        int weaponRows = EmbeddedNameTables.LoadWeapons(game).Count;
        int weaponTargets = 0;
        for (int type = 0; type <= ModelTypes.RodInsect; type++)
        {
            weaponTargets += catalog.WeaponTargets(type).Count;
        }

        Assert.Equal(weaponRows, weaponTargets);
    }

    // ---------------------------------------------------------------- 名称解析

    [Theory]
    [InlineData(
        "natives/STM/player/mod/f/pl001/f_arm001.mdf2.23",
        ModelGame.Rise, "皮制/皮制S/皮制X")]
    [InlineData(
        "natives/STB/player/mod/f/pl001/f_arm001.mdf2.23",
        ModelGame.Unknown, null)]
    public void ModelNameResolver_RiseArmor(string path, ModelGame game, string? expected)
    {
        if (game == ModelGame.Unknown)
        {
            Assert.Null(ModelNameResolver.Resolve(path));
            return;
        }

        Assert.True(ModelNameResolver.TryParse(path, out ModelIdentity identity));
        Assert.Equal(ModelGame.Rise, identity.Game);
        Assert.Equal("pl001", identity.Code);
        Assert.Equal(expected, ModelNameResolver.Resolve(path));
    }

    [Fact]
    public void ModelNameResolver_RiseWeapon()
    {
        const string path = "natives/STM/weapon/GreatSword/G_Swd001/G_Swd001.mdf2.23";

        Assert.True(ModelNameResolver.TryParse(path, out ModelIdentity identity));
        Assert.Equal(ModelGame.Rise, identity.Game);
        Assert.Equal("G_Swd001", identity.Code);
        Assert.Equal("钢龙寒冰大剑", ModelNameResolver.Resolve(path));
    }

    [Fact]
    public void ModelNameResolver_WorldArmorAndWeapon()
    {
        const string armor = "nativePC/pl/m_equip/pl001_0000/body/mod/m_body001_0000.mod3";
        Assert.Equal("【皮制】服装", ModelNameResolver.Resolve(armor));

        const string weapon = "nativePC/wp/bow/bow001/mod/bow001.mdf2.45";
        Assert.True(ModelNameResolver.TryParse(weapon, out ModelIdentity identity));
        Assert.Equal(ModelGame.World, identity.Game);
        Assert.Equal("bow001", identity.Code);
        Assert.Equal("钢冰马弓/钢龙强弓/钢龙射手弓", ModelNameResolver.Resolve(weapon));
    }

    [Fact]
    public void ModelNameResolver_WorldOpPart_UsesTableLayerName()
    {
        const string path = "nativePC/wp/bow/parts/op_bow005";

        Assert.True(ModelPathRules.TryParseWorldWeaponOpPart(path, out int weaponType, out int opId));
        Assert.Equal(ModelTypes.Bow, weaponType);
        Assert.Equal(5, opId);
        Assert.Equal("op_bow005", ModelPathRules.FormatWorldWeaponOpCode(weaponType, opId));
        Assert.Equal("搔鸟弓", ModelNameResolver.Resolve(path));
        Assert.False(ModelPathRules.TryParseWorldWeaponOpPart("nativePC/wp/bow/mod/bow001", out _, out _));

        // 匹配到 op 路径但末尾不是数字时，两个 out 都必须回到哨兵值。
        Assert.False(ModelPathRules.TryParseWorldWeaponOpPartNormalized(
            "nativepc/wp/bow/parts/op_bow", out int failedType, out int failedOp));
        Assert.Equal(-1, failedType);
        Assert.Equal(0, failedOp);
    }

    // ---------------------------------------------------------------- 三作识别器

    [Fact]
    public void PathRecognizer_DetectsGamesAndIdentities()
    {
        IReadOnlyList<DomainIdentity> rise = ModelPathRecognizer.FindIdentities(
            ["natives/stm/player/mod/m/pl002/m_body002.mdf2.23"], ModelGame.Rise);
        Assert.Equal(DomainIdentity.RiseArmor(2, female: false), Assert.Single(rise));

        IReadOnlyList<string> worldIds = ModelPathRecognizer.FindModelIds(
            ["nativePC/wp/bow/bs_bow001/mod/bs_bow001.mdf2"], ModelGame.World);
        Assert.Equal(["bs_bow001"], worldIds);
    }
}
