using HunterBox.Infrastructure.Mods;

using HunterBox.Domain.Models;

namespace HunterBox.Tests;

/// <summary>用真实 MOD的路径校准名称映射。</summary>
public class ModelNameResolverTests
{
    // 来自「睡衣优香 V1.3.0」：natives/stm/Art/Model/Character/ch03/...
    private const string ArmorPath = "natives/stm/Art/Model/Character/ch03/060/000/2/ch03_060_0002.mesh.241111606";

    // 示例路径（测试样本）：natives/STM/Art/Model/Item/it03/007/...
    private const string WeaponPath = "natives/STM/Art/Model/Item/it03/00/0017/it0300_0017_0.mesh.241111606";

    [Fact]
    public void TryParse_ArmorPath_UsesTwoNumberKey()
    {
        Assert.True(ModelNameResolver.TryParse(ArmorPath, out ModelIdentity identity));
        Assert.False(identity.IsWeapon);
        Assert.Equal("060_000", identity.Code);
    }

    [Fact]
    public void TryParse_WeaponPath_UsesTypeAndKey()
    {
        Assert.True(ModelNameResolver.TryParse(WeaponPath, out ModelIdentity identity));
        Assert.True(identity.IsWeapon);
        Assert.Equal(3, identity.Type);
        Assert.Equal("00_0017", identity.Code);
    }

    [Fact]
    public void Resolve_ArmorSample_ReturnsNameFromTable()
    {
        Assert.Equal("公会骑士", ModelNameResolver.Resolve(ArmorPath));
    }

    [Fact]
    public void Resolve_WeaponSample_ReturnsNameFromTable()
    {
        Assert.Equal("机械军刀/次元刀(满级)", ModelNameResolver.Resolve(WeaponPath));
    }

    [Theory]
    [InlineData("natives/stm/sound/bgm.bnk")]
    [InlineData("reframework/autorun/x.lua")]
    [InlineData("")]
    public void Resolve_NonModelPath_ReturnsNull(string path)
    {
        Assert.Null(ModelNameResolver.Resolve(path));
    }

}
