using HunterBox.Application.Models;
using HunterBox.Domain.Models;

namespace HunterBox.Tests;

/// <summary>候选过滤必须强制保留当前模型，且目标提交完整身份。</summary>
public class ModelEditorPlannerTests
{
    private static ModelChoice[] ArmorChoices() =>
    [
        new("012_000", "刺花蜘蛛α/β(男款)", false, ModelIdentity.Armor(2, 12, 0)),
        new("012_001", "刺花蜘蛛α/β(女款)", false, ModelIdentity.Armor(2, 12, 1)),
        new("046_000", "公会骑士", false, ModelIdentity.Armor(2, 46, 0)),
    ];

    [Fact]
    public void BuildCandidates_FilteredByUsedIds_StillKeepsCurrentModel()
    {
        ModelIdentity current = ModelIdentity.Armor(3, 49, 0); // 当前模型不在名称表里
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase) { "046_000" };

        IReadOnlyList<ModelChoice> visible = ModelEditorPlanner.BuildCandidates(
            current, "护甲 049_000", false, ArmorChoices(), used, filterUnused: true);

        Assert.DoesNotContain(visible, c => c.Code == "046_000");
        ModelChoice forced = visible.First(c => c.Code == current.Code);
        Assert.Equal("护甲 049_000", forced.Name);
        Assert.Equal(current.Code, visible[0].Code); // 当前模型排在首位，避免空白选择
    }

    [Fact]
    public void BuildCandidates_RememberedTargetSurvivesFilterToggle()
    {
        ModelIdentity current = ModelIdentity.Armor(3, 12, 0);
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase) { "046_000" };

        IReadOnlyList<ModelChoice> visible = ModelEditorPlanner.BuildCandidates(
            current, "刺花蜘蛛α/β(男款)", false, ArmorChoices(), used, filterUnused: true,
            rememberedCode: "046_000");

        Assert.Contains(visible, c => c.Code == "046_000"); // 用户已选目标不被过滤掉
    }

    [Fact]
    public void BuildCandidates_NoFilter_KeepsAllAndCurrentPresentOnce()
    {
        ModelIdentity current = ModelIdentity.Armor(2, 12, 0);
        IReadOnlyList<ModelChoice> visible = ModelEditorPlanner.BuildCandidates(
            current, "男款", false, ArmorChoices(), new HashSet<string>(), filterUnused: false);

        Assert.Equal(ArmorChoices().Length, visible.Count);
        Assert.Single(visible, c => c.Code == current.Code);
    }

    [Theory]
    [InlineData(ModelKind.Armor, 3, 12, 0, "012_001", "ch03/012/001")]
    [InlineData(ModelKind.CatEquip, 5, 3, 1, "004_0002", "ch05/004/0002")]
    [InlineData(ModelKind.Weapon, 14, 3, 0, "05_0001", "it10/05/0001")]
    public void BuildTargetForm_KeepsRowGenderAndWeaponPath(
        ModelKind kind, int type, int first, int second, string targetCode, string expected)
    {
        ModelIdentity current = new(kind, type, first, second);
        ModelIdentity target = kind switch
        {
            ModelKind.CatEquip => ModelIdentity.Cat(4, 2),
            ModelKind.Weapon => ModelIdentity.Weapon(10, 5, 1),
            _ => ModelIdentity.Armor(2, 12, 1),
        };
        ModelChoice choice = new(targetCode, "目标", false, target);

        Assert.Equal(expected, ModelEditorPlanner.BuildTargetForm(current, choice));
    }

    [Fact]
    public void BuildTargetForm_FemaleOnlyModel_KeepsFemaleCh03Path()
    {
        // 只含女款路径的模型行：身份是 ch03（Type=3），选任何目标都保留 ch03，不会只命中 ch02。
        ModelIdentity femaleRow = ModelIdentity.Armor(3, 12, 0);
        ModelChoice maleIdentityChoice = new("012_000", "刺花蜘蛛α/β(男款)", false, ModelIdentity.Armor(2, 12, 0));

        Assert.Equal("ch03/012/000", ModelEditorPlanner.BuildTargetForm(femaleRow, maleIdentityChoice));

        ModelChoice femaleIdentityChoice = new("000_001", "身体(女款)", false, ModelIdentity.Armor(3, 0, 1));
        Assert.Equal("ch03/000/001", ModelEditorPlanner.BuildTargetForm(femaleRow, femaleIdentityChoice));
    }

    [Fact]
    public void BuildTargetForm_RejectsChoiceOfDifferentKind()
    {
        ModelIdentity current = ModelIdentity.Armor(2, 12, 0);
        ModelChoice weapon = new("03_0000", "绿金龟", false, ModelIdentity.Weapon(10, 3, 0));

        Assert.Throws<ArgumentException>(() => ModelEditorPlanner.BuildTargetForm(current, weapon));
    }

    [Fact]
    public void IsCurrent_ComparesCompleteCode()
    {
        ModelIdentity current = ModelIdentity.Armor(2, 12, 0);

        Assert.True(ModelEditorPlanner.IsCurrent(current, new ModelChoice("012_000", "x")));
        Assert.False(ModelEditorPlanner.IsCurrent(current, new ModelChoice("012_001", "y")));
        Assert.False(ModelEditorPlanner.IsCurrent(ModelIdentity.Weapon(11, 10, 0), new ModelChoice("10_0001", "z")));
    }

    // ---- 同 MOD 其它模型占用/自撞 ----

    [Fact]
    public void BuildCandidates_FiltersSelfOtherModelRow_ButKeepsCurrent()
    {
        // 当前 MOD 有两行：012_000 与 046_000。编辑第一行时，046_000 不能作为目标（会覆盖自身文件）。
        ModelIdentity current = ModelIdentity.Armor(2, 12, 0);
        ModelIdentity sibling = ModelIdentity.Armor(2, 46, 0);
        ModelChoice[] all =
        [
            new("012_000", "刺花蜘蛛α/β(男款)", false, current),
            new("046_000", "公会骑士", false, sibling),
            new("012_001", "刺花蜘蛛α/β(女款)", false, ModelIdentity.Armor(2, 12, 1)),
        ];

        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase) { sibling.Code };
        IReadOnlyList<ModelChoice> visible = ModelEditorPlanner.BuildCandidates(
            current, "刺花蜘蛛α/β(男款)", false, all, used, filterUnused: true);

        Assert.DoesNotContain(visible, c => c.Code == sibling.Code);
        Assert.Contains(visible, c => c.Code == current.Code);
        Assert.Contains(visible, c => c.Code == "012_001");
    }

    [Fact]
    public void DescribeSelfCollision_DetectsOnlyOtherRowsOfSameMod()
    {
        ModelIdentity current = ModelIdentity.Armor(2, 12, 0);
        ModelIdentity sibling = ModelIdentity.Armor(2, 46, 0);
        ModelIdentity[] own = [current, sibling];

        Assert.NotNull(ModelEditorPlanner.DescribeSelfCollision(
            own, current, new ModelChoice("046_000", "公会骑士", false, sibling)));
        Assert.Null(ModelEditorPlanner.DescribeSelfCollision(
            own, current, new ModelChoice("012_000", "男款", false, current)));
        Assert.Null(ModelEditorPlanner.DescribeSelfCollision(
            own, current, new ModelChoice("012_001", "女款", false, ModelIdentity.Armor(2, 12, 1))));
    }

    /// <summary>护甲同 fid_sid 的男/女款 Code 相同但路径不同，不能误报自撞。</summary>
    [Fact]
    public void DescribeSelfCollision_ArmorSameCodeDifferentSex_NoFalsePositive()
    {
        ModelIdentity male = ModelIdentity.Armor(2, 12, 0);
        ModelIdentity female = ModelIdentity.Armor(3, 12, 0);
        ModelIdentity[] own = [male, female];

        Assert.Null(ModelEditorPlanner.DescribeSelfCollision(
            own, male, new ModelChoice("012_000", "女款", false, female)));

        ModelIdentity maleSibling = ModelIdentity.Armor(2, 46, 0);
        Assert.NotNull(ModelEditorPlanner.DescribeSelfCollision(
            [male, maleSibling],
            male,
            new ModelChoice("046_000", "公会骑士", false, maleSibling)));
    }

    /// <summary>候选必须与当前行同类同游戏；空候选仍加回当前模型。</summary>
    [Fact]
    public void BuildCandidates_IncompatibleOrEmptyCatalog_StillKeepsCurrentModel()
    {
        ModelIdentity current = ModelIdentity.Armor(2, 12, 0);
        ModelChoice worldArmor = new("pl060", "世界护甲", false, ModelIdentity.WorldArmor(2, 60, 0));
        ModelChoice weapon = new("03_0000", "武器", false, ModelIdentity.Weapon(10, 3, 0));

        IReadOnlyList<ModelChoice> visible = ModelEditorPlanner.BuildCandidates(
            current, "男款", false, [worldArmor, weapon], new HashSet<string>(), filterUnused: false);

        Assert.Single(visible);
        Assert.Equal(current.Code, visible[0].Code);

        IReadOnlyList<ModelChoice> empty = ModelEditorPlanner.BuildCandidates(
            current, "男款", false, [], new HashSet<string>(), filterUnused: true);
        Assert.Single(empty);
        Assert.Equal(current.Code, empty[0].Code);
    }

    /// <summary>空/全空白查询按回车不应选取第一项。</summary>
    [Fact]
    public void CanChooseFirstOnEnter_BlankQuery_ReturnsFalse()
    {
        Assert.False(ModelEditorPlanner.CanChooseFirstOnEnter(null));
        Assert.False(ModelEditorPlanner.CanChooseFirstOnEnter(string.Empty));
        Assert.False(ModelEditorPlanner.CanChooseFirstOnEnter("   "));
        Assert.False(ModelEditorPlanner.CanChooseFirstOnEnter("　"));
        Assert.True(ModelEditorPlanner.CanChooseFirstOnEnter("刺花"));
    }

    /// <summary>同一模型出现在不同 pak 时必须得到不同的记忆键。</summary>
    [Fact]
    public void SelectionKey_SameIdentityDifferentPak_AreDistinct()
    {
        string first = ModelEditorPlanner.SelectionKey("chunk0.pak", "ch02/012/000");
        string second = ModelEditorPlanner.SelectionKey("chunk1.pak", "ch02/012/000");
        string loose = ModelEditorPlanner.SelectionKey(null, "ch02/012/000");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, loose);
        Assert.Equal(loose, ModelEditorPlanner.SelectionKey(string.Empty, "ch02/012/000"));
    }

    // ---- 目标模型面板的关键词过滤（按名称/完整 ID 模糊搜索） ----

    [Fact]
    public void Search_BlankQuery_ReturnsSameListWithoutCopying()
    {
        ModelChoice[] choices = ArmorChoices();

        Assert.Same(choices, ModelEditorPlanner.Search(choices, null));
        Assert.Same(choices, ModelEditorPlanner.Search(choices, "   "));
    }

    [Fact]
    public void Search_NameOrCompleteId_MatchesSubstring()
    {
        ModelChoice[] choices = ArmorChoices();

        string[] byName = [.. ModelEditorPlanner.Search(choices, "公会").Select(c => c.Code)];
        string[] byId = [.. ModelEditorPlanner.Search(choices, "012_00").Select(c => c.Code)];

        Assert.Equal(["046_000"], byName);
        Assert.Equal(["012_000", "012_001"], byId);
    }

    /// <summary>空格分隔的每个词都必须命中同一个候选，词的先后顺序不影响结果。</summary>
    [Theory]
    [InlineData("刺花", 2)]
    [InlineData("刺花 001", 1)]
    [InlineData("001 刺花", 1)]
    [InlineData("刺花 046", 0)]
    public void Search_EveryToken_MatchesSameChoice(string query, int expectedCount) =>
        Assert.Equal(expectedCount, ModelEditorPlanner.Search(ArmorChoices(), query).Count);

    [Fact]
    public void Search_FullWidthSpace_TreatedAsSeparator()
    {
        // 中文输入法下常打出全角空格：与半角空格一样当作分隔符。
        Assert.Single(ModelEditorPlanner.Search(ArmorChoices(), "刺花　001"));
        Assert.Single(ModelEditorPlanner.Search(ArmorChoices(), "  刺花 001  "));
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(ModelEditorPlanner.Search(ArmorChoices(), "不存在"));
        // 两个词分别命中不同候选时不算命中：必须落在同一个候选上。
        Assert.Empty(ModelEditorPlanner.Search(ArmorChoices(), "012_000 公会"));
    }

    [Fact]
    public void Search_LatinCodes_IgnoreCase()
    {
        ModelChoice[] choices = [new("bow001", "铁弓", false, ModelIdentity.WorldWeapon(3, 1))];

        Assert.Single(ModelEditorPlanner.Search(choices, "BOW001"));
        Assert.Single(ModelEditorPlanner.Search(choices, "bow"));
    }
}
