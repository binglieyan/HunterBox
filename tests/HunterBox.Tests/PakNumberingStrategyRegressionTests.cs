using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// D-04：编号策略合并前的回归基线。先钉住 <see cref="PakPlanner"/> 的「max+1」与
/// <see cref="PakMountService"/> 的「沿用 pak_mods 编号 + 最小空闲号」两套语义，
/// 再让两者共用 Domain 的编号 helper（重构后本文件必须继续绿）。
/// </summary>
public class PakNumberingStrategyRegressionTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string GameId = "mhws";
    private static readonly PakProfile NumberedProfile = new(WildsPrefix, 6, PakNamingMode.PatchNumbered);

    private static ModRecord Mod(int id, string pakName, bool enabled = false) => new()
    {
        Id = id,
        Index = id,
        GameId = GameId,
        Enabled = enabled,
        Kind = ModKind.PakFiles,
        RelativeFiles = [pakName],
    };

    private static void WritePak(BoxPaths paths, int modId, string pakName, string content)
    {
        string dir = paths.FilesDirectory(modId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, pakName), content);
    }

    private static string[] PakModsFiles(string gameRoot) =>
        [.. Directory.EnumerateFiles(Path.Combine(gameRoot, "pak_mods")).Select(Path.GetFileName).OfType<string>()];

    [Fact]
    public void MountService_FillsGapAboveGameRootMax()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        // pak_mods 里已有 007 与 009（008 是空档）：新 MOD 必须补 008，而不是一路涨到 010。
        File.WriteAllText(Path.Combine(pakMods, WildsPrefix + "007.pak"), "existing-7");
        File.WriteAllText(Path.Combine(pakMods, WildsPrefix + "009.pak"), "existing-9");

        ModRecord mod = Mod(1, "new.pak");
        WritePak(paths, 1, "new.pak", "new-content");
        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());

        service.Enable(mod, NumberedProfile);

        Assert.True(File.Exists(Path.Combine(pakMods, WildsPrefix + "008.pak")), "空档 008 必须被补上");
        Assert.Equal("existing-7", File.ReadAllText(Path.Combine(pakMods, WildsPrefix + "007.pak")));
        Assert.Equal("existing-9", File.ReadAllText(Path.Combine(pakMods, WildsPrefix + "009.pak")));
    }

    [Fact]
    public void MountService_GameRootMaxBeatsLowerPakModsGap()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        // 游戏根目录已有 010；pak_mods 里有 007/009。新编号必须从「根目录最高 + 1 = 011」起算，
        // 不能去补 008（低于游戏自带 patch 的编号不能复用）。
        File.WriteAllText(Path.Combine(gameRoot, WildsPrefix + "010.pak"), "game");
        File.WriteAllText(Path.Combine(pakMods, WildsPrefix + "007.pak"), "existing-7");
        File.WriteAllText(Path.Combine(pakMods, WildsPrefix + "009.pak"), "existing-9");

        ModRecord mod = Mod(1, "new.pak");
        WritePak(paths, 1, "new.pak", "new-content");
        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());

        service.Enable(mod, NumberedProfile);

        Assert.True(File.Exists(Path.Combine(pakMods, WildsPrefix + "011.pak")));
        Assert.False(File.Exists(Path.Combine(pakMods, WildsPrefix + "008.pak")));
    }

    [Fact]
    public void MountService_ReusesExistingNumberByNameForSameContent()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        ModRecord first = Mod(1, "a.pak", enabled: true);
        WritePak(paths, 1, "a.pak", "same-bytes");
        PakMountService service = new(paths, gameRoot, GameId, [first], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(first, NumberedProfile);
        string firstFile = Path.Combine(pakMods, WildsPrefix + "007.pak");
        Assert.True(File.Exists(firstFile));

        // 第二个 MOD 的 pak 内容与已部署的 007 相同：规划必须沿用 007，不能再分配 008。
        ModRecord second = Mod(2, "b.pak");
        WritePak(paths, 2, "b.pak", "same-bytes");
        PakMountService withSecond = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        PakPlan plan = withSecond.PlanEnable(second, NumberedProfile);

        Assert.Equal(WildsPrefix + "007.pak", Assert.Single(plan.Assignments, a => a.Source.ModId == 2).FinalName);
        Assert.Equal([WildsPrefix + "007.pak"], PakModsFiles(gameRoot));
    }

    [Fact]
    public void Planner_Numbered_UsesMaxPlusOneAndIgnoresLowerGaps()
    {
        // 纯规划器没有 pak_mods 状态：只按游戏根目录已有编号取 max+1，不补空档。
        FakeFingerprint print = new();
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = WildsPrefix,
            PatchNumberFloor = 6,
            ExistingGameRootFileNames = [WildsPrefix + "007.pak", WildsPrefix + "010.pak"],
        };

        PakPlan plan = PakPlanner.Plan(
            [new PakSource(1, "A", "a.pak", "/mods/1/a.pak"), new PakSource(2, "B", "b.pak", "/mods/2/b.pak")],
            options,
            print);

        Assert.Equal(
            [WildsPrefix + "011.pak", WildsPrefix + "012.pak"],
            plan.Assignments.Select(a => a.FinalName));
    }

    [Fact]
    public void Planner_Numbered_StartsAfterBaseNumber()
    {
        FakeFingerprint print = new();
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = WildsPrefix,
            PatchNumberFloor = 6,
            ExistingGameRootFileNames = ["unrelated.pak", WildsPrefix + "004.pak"],
        };

        PakPlan plan = PakPlanner.Plan([new PakSource(1, "A", "a.pak", "/mods/1/a.pak")], options, print);

        Assert.Equal(WildsPrefix + "007.pak", Assert.Single(plan.Assignments).FinalName);
    }

    [Fact]
    public void PlannerAndMountService_AgreeWhenPakModsIsEmpty()
    {
        // 等价性基线：pak_mods 为空、只有游戏根目录 patch 时，两套策略必须给出同一组文件名。
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string[] rootFiles =
        [
            Path.GetFileName(Path.Combine(gameRoot, WildsPrefix + "007.pak")),
            Path.GetFileName(Path.Combine(gameRoot, WildsPrefix + "010.pak")),
        ];
        foreach (string name in rootFiles)
        {
            File.WriteAllText(Path.Combine(gameRoot, name), "game");
        }

        ModRecord first = Mod(1, "a.pak", enabled: true);
        ModRecord second = Mod(2, "b.pak");
        WritePak(paths, 1, "a.pak", "content-a");
        WritePak(paths, 2, "b.pak", "content-b");
        PakMountService service = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        string[] mountNames = [.. service.PlanEnable(second, NumberedProfile).Assignments.Select(a => a.FinalName)];

        List<PakSource> sources =
        [
            new(1, first.DisplayName, "a.pak", Path.Combine(paths.FilesDirectory(1), "a.pak")),
            new(2, second.DisplayName, "b.pak", Path.Combine(paths.FilesDirectory(2), "b.pak")),
        ];
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = WildsPrefix,
            PatchNumberFloor = 6,
            ExistingGameRootFileNames = rootFiles,
        };
        string[] plannerNames = [.. PakPlanner.Plan(sources, options, new FileContentFingerprint()).Assignments.Select(a => a.FinalName)];

        Assert.Equal([WildsPrefix + "011.pak", WildsPrefix + "012.pak"], mountNames);
        Assert.Equal(mountNames, plannerNames);
    }

    [Fact]
    public void Planner_NextPatchNumber_AcceptsFourDigitPatch()
    {
        PakPlacementOptions options = new()
        {
            Mode = PakNamingMode.PatchNumbered,
            PatchPrefix = WildsPrefix,
            PatchNumberFloor = 6,
            ExistingGameRootFileNames = [WildsPrefix + "1000.pak", WildsPrefix + "015.pak"],
        };

        Assert.Equal(1001, PakPlanner.NextPatchNumber(options));
        Assert.Equal(WildsPrefix + "1001.pak", PakNumbering.FormatPatchName(WildsPrefix, 1001));
    }
}
