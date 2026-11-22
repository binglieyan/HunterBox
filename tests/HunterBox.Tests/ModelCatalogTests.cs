using HunterBox.Domain.Models;
using HunterBox.Infrastructure.Mods;

namespace HunterBox.Tests;

/// <summary>候选表恢复「完整模型 ID」语义。</summary>
public class ModelCatalogTests
{
    private readonly ModelCatalog _catalog = new();

    [Fact]
    public void ArmorTargets_KeepsEveryTableRow_WithFullIdAndGenderNames()
    {
        IReadOnlyList<ModelChoice> targets = _catalog.ArmorTargets;

        Assert.Equal(217, targets.Count);
        Assert.Equal(217, targets.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(targets, t => Assert.Matches(@"^\d{3}_\d{3}$", t.Code));

        // 男/女款是不同 CSV 行，需分别作为候选
        Assert.Contains(targets, t => t.Code == "000_000" && t.Name == "身体(男款)");
        Assert.Contains(targets, t => t.Code == "000_001" && t.Name == "身体(女款)");
        Assert.Contains(targets, t => t.Code == "000_900"); // 骨骼等非衣服变体也保留
        Assert.Contains(targets, t => t.Display.Contains("000_001", StringComparison.Ordinal));
    }

    [Fact]
    public void ArmorTargets_GenerateBothSexIdentitiesFromTableAnnotations()
    {
        IReadOnlyList<ModelChoice> targets = _catalog.ArmorTargets;

        ModelChoice male = targets.First(t => t.Code == "000_000");
        ModelChoice female = targets.First(t => t.Code == "000_001");
        Assert.Equal(2, male.Identity!.Type);
        Assert.Equal(3, female.Identity!.Type);
        Assert.Equal("ch02/000/000", male.Identity.PathForm);
        Assert.Equal("ch03/000/001", female.Identity.PathForm);

        // 未标注性别的行按男款生成；表格行数不变（不会为同一行生成两条重复候选）。
        ModelChoice neutral = targets.First(t => t.Code == "000_900");
        Assert.Equal(2, neutral.Identity!.Type);
    }

    [Fact]
    public void WeaponTargets_BowRestoresThirtyTwoFullIds()
    {
        IReadOnlyList<ModelChoice> bows = _catalog.WeaponTargets(ModelTypes.Bow);

        Assert.Equal(32, bows.Count);
        Assert.Equal(32, bows.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(bows, t => Assert.Matches(@"^\d{2}_\d{4}$", t.Code));
        Assert.Contains(bows, t => t.Code == "10_0000" && t.Name == "白炎弓/冕耀之弓"); // 原按 BBBB 去重会丢失
        Assert.Contains(bows, t => t.Display.Contains("10_0000", StringComparison.Ordinal));
    }

    [Fact]
    public void WeaponTargets_KeepsRodAndRodInsectSeparate()
    {
        Assert.Equal(34, _catalog.WeaponTargets(ModelTypes.Rod).Count);
        IReadOnlyList<ModelChoice> insects = _catalog.WeaponTargets(ModelTypes.RodInsect);

        Assert.Equal(21, insects.Count);
        Assert.All(insects, t => Assert.StartsWith("03_", t.Code));
        Assert.Contains(insects, t => t.Code == "03_0000" && t.Name == "绿金龟");
        Assert.DoesNotContain(_catalog.WeaponTargets(ModelTypes.Rod), t => t.Code.StartsWith("03_", StringComparison.Ordinal));
    }

    [Fact]
    public void CatTargets_RestoresCatTable_AndFiltersBySid()
    {
        IReadOnlyList<NameTableRow> rows = EmbeddedNameTables.LoadCats();
        Assert.True(rows.Count >= 200, $"猫装备表行数过少：{rows.Count}");

        IReadOnlyList<ModelChoice> all = _catalog.CatTargets();
        Assert.Equal(rows.Count, all.Count);
        Assert.Equal(rows.Count, all.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(all, t => t.Code == "001_0000" && t.Name == "希望猫(身)");
        Assert.All(all, t => Assert.Matches(@"^\d{3}_\d{4}$", t.Code));

        IReadOnlyList<ModelChoice> sameSid = _catalog.CatTargets(secondId: 0);
        Assert.NotEmpty(sameSid);
        Assert.All(sameSid, t => Assert.EndsWith("_0000", t.Code));
    }

    [Fact]
    public void Targets_ChainFlagComesFromGameList()
    {
        Assert.Contains(_catalog.ArmorTargets, t => t.HasChain && t.Display.Contains("(chain)", StringComparison.Ordinal));
        Assert.Contains(_catalog.WeaponTargets(ModelTypes.Bow), t => t.HasChain);
        Assert.Contains(_catalog.CatTargets(), t => t.HasChain);
    }

    [Fact]
    public void ArmorAndWeaponTargets_CarryIdentityForRewriting()
    {
        ModelChoice armor = _catalog.ArmorTargets.First(t => t.Code == "012_000");
        Assert.NotNull(armor.Identity);
        Assert.Equal(12, armor.Identity!.FirstId);
        Assert.Equal(0, armor.Identity.SecondId);

        ModelChoice weapon = _catalog.WeaponTargets(ModelTypes.Bow).First(t => t.Code == "10_0000");
        Assert.NotNull(weapon.Identity);
        Assert.Equal(ModelKind.Weapon, weapon.Identity!.Kind);
        Assert.Equal(10, weapon.Identity.FirstId);
        Assert.Equal(0, weapon.Identity.SecondId);
    }
}
