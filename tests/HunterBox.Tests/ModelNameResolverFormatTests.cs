using HunterBox.Infrastructure.Mods;

using HunterBox.Domain.Models;

namespace HunterBox.Tests;

/// <summary>猫装备（ch05）、prefab 与猎虫（RodInsect）名称解析。</summary>
public class ModelNameResolverFormatTests
{
    [Fact]
    public void TryParse_CatEquipPath_UsesFourDigitSid()
    {
        const string path = "natives/stm/art/model/character/ch05/003/0001/ch05_003_0001.mesh.241111606";

        Assert.True(ModelNameResolver.TryParse(path, out ModelIdentity identity));
        Assert.False(identity.IsWeapon);
        Assert.True(identity.IsCatEquip);
        Assert.Equal("003_0001", identity.Code);
        Assert.Equal(3, identity.FirstId);
        Assert.Equal(1, identity.SecondId);
    }

    [Fact]
    public void Resolve_CatEquipPath_ReturnsCatTableName()
    {
        const string path = "natives/STM/Art/Model/Character/ch05/001/0000/ch05_001_0000.mesh.1";

        Assert.Equal("希望猫(身)", ModelNameResolver.Resolve(path));
    }

    [Fact]
    public void TryParse_RodInsectPath_MapsToRodInsectType()
    {
        const string path = "natives/STM/Art/Model/Item/it10/03/0000/it1003_0000_0.mdf2.45";

        Assert.True(ModelNameResolver.TryParse(path, out ModelIdentity identity));
        Assert.True(identity.IsWeapon);
        Assert.Equal(14, identity.Type); // RodInsect
        Assert.Equal("03_0000", identity.Code);
        Assert.Equal("绿金龟", ModelNameResolver.Resolve(path));
    }

    [Fact]
    public void TryParse_WeaponPrefabPath_UsesWpTypeAndResolvesName()
    {
        const string path = "natives/STM/GameDesign/Equip/_Prefab/Weapon/Wp10/03/0000/it1003_0000_0.pfb.18";

        Assert.True(ModelNameResolver.TryParse(path, out ModelIdentity identity));
        Assert.Equal(14, identity.Type);
        Assert.Equal("03_0000", identity.Code);
        Assert.Equal("绿金龟", ModelNameResolver.Resolve(path));
    }

    [Fact]
    public void Resolve_ArmorPrefabAndBonePaths_UseArmorTable()
    {
        Assert.Equal("公会骑士", ModelNameResolver.Resolve(
            "natives/stm/gamedesign/equip/_prefab/armor/female/060/000/Arm/ch03_060_0001.pfb.18"));
        Assert.Equal("公会骑士", ModelNameResolver.Resolve(
            "reframework/data/bonesystem/ch03_060_0001"));
    }

}
