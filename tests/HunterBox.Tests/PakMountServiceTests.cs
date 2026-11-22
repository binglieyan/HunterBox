using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

public class PakMountServiceTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string GameId = "mhws";

    private static readonly PakProfile NumberedProfile = new(WildsPrefix, 6, PakNamingMode.PatchNumbered);
    private static readonly PakProfile KeepNameProfile = new(WildsPrefix, 6, PakNamingMode.KeepOriginalName);

    private static (BoxPaths Paths, string GameRoot, ModRecord Mod, PakMountService Service) Setup(
        TempDir temp,
        string pakName = "cool.pak",
        string content = "pak-bytes",
        PakNamingMode mode = PakNamingMode.PatchNumbered,
        params ModRecord[] others)
    {
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.PakFiles,
            RelativeFiles = [pakName],
        };
        WritePak(paths, mod.Id, pakName, content);

        List<ModRecord> all = [mod, .. others];
        PakMountService service = new(paths, gameRoot, GameId, all, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        _ = mode;
        return (paths, gameRoot, mod, service);
    }

    private static void WritePak(BoxPaths paths, int modId, string pakName, string content)
    {
        string dir = paths.FilesDirectory(modId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, pakName), content);
    }

    [Fact]
    public void Enable_Numbered_WritesIntoPakModsWithNextPatchNumber()
    {
        using TempDir temp = TempDir.Create();
        (_, string gameRoot, ModRecord mod, PakMountService service) = Setup(temp);

        PakMountReport report = service.Enable(mod, NumberedProfile);

        Assert.Equal(1, report.Written);
        Assert.True(File.Exists(Path.Combine(gameRoot, "pak_mods", WildsPrefix + "007.pak")));
    }

    [Fact]
    public void Enable_Numbered_ContinuesAfterExistingRootPatch()
    {
        using TempDir temp = TempDir.Create();
        (_, string gameRoot, ModRecord mod, PakMountService service) = Setup(temp);
        File.WriteAllText(Path.Combine(gameRoot, WildsPrefix + "015.pak"), "game");

        service.Enable(mod, NumberedProfile);

        Assert.True(File.Exists(Path.Combine(gameRoot, "pak_mods", WildsPrefix + "016.pak")));
    }

    [Fact]
    public void Enable_KeepName_ThrowsOnSameNameDifferentContent()
    {
        using TempDir temp = TempDir.Create();
        ModRecord other = new()
        {
            Id = 2,
            Index = 1,
            GameId = GameId,
            Enabled = true,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["cool.pak"],
        };

        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        WritePak(paths, 2, "cool.pak", "other-content");

        ModRecord mod = new()
        {
            Id = 1,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["cool.pak"],
        };
        WritePak(paths, 1, "cool.pak", "my-content");

        PakMountService service = new(paths, gameRoot, GameId, [other, mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());

        PakConflictException ex = Assert.Throws<PakConflictException>(() => service.Enable(mod, KeepNameProfile));
        Assert.Single(ex.Conflicts);
        Assert.Equal("cool.pak", ex.Conflicts[0].FileName);
    }

    [Fact]
    public void Enable_KeepName_IgnoresCrossGamePakWithSameName()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        // 另一个游戏的记录（内容不同、同名 pak）不能造成本游戏的 pak 冲突。
        ModRecord otherGame = new()
        {
            Id = 2,
            Index = 1,
            GameId = "mhw",
            Enabled = true,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["cool.pak"],
        };
        WritePak(paths, 2, "cool.pak", "other-game-content");

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["cool.pak"],
        };
        WritePak(paths, 1, "cool.pak", "my-content");

        PakMountService service = new(paths, gameRoot, GameId, [otherGame, mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());

        PakMountReport report = service.Enable(mod, KeepNameProfile);

        Assert.Equal(1, report.Written);
        Assert.True(File.Exists(Path.Combine(gameRoot, "pak_mods", "cool.pak")));
    }

    /// <summary>pak_mods 已有同名不同内容时不能静默覆盖，报告必须给出明确 warning。</summary>
    [Fact]
    public void Enable_KeepName_OverwritesResidualPakWithWarning()
    {
        using TempDir temp = TempDir.Create();
        (_, string gameRoot, ModRecord mod, PakMountService service) = Setup(temp);
        string pakMods = Directory.CreateDirectory(Path.Combine(gameRoot, "pak_mods")).FullName;
        string residual = Path.Combine(pakMods, "cool.pak");
        File.WriteAllText(residual, "user-placed-content");

        PakMountReport report = service.Enable(mod, KeepNameProfile);

        Assert.Equal(1, report.Written);
        Assert.Contains(report.Warnings, warning => warning.Contains("同名但内容不同"));
        Assert.Equal("pak-bytes", File.ReadAllText(residual));
    }

    [Fact]
    public void Enable_Numbered_DeduplicatesIdenticalContent()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord first = new() { Id = 1, Index = 1, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["a.pak"] };
        WritePak(paths, 1, "a.pak", "same-bytes");
        PakMountService service1 = new(paths, gameRoot, GameId, [first], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service1.Enable(first, NumberedProfile);

        ModRecord second = new() { Id = 2, Index = 2, GameId = GameId, Kind = ModKind.PakFiles, RelativeFiles = ["b.pak"] };
        WritePak(paths, 2, "b.pak", "same-bytes");
        PakMountService service2 = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service2.Enable(second, NumberedProfile);

        string[] files = [.. Directory.EnumerateFiles(Path.Combine(gameRoot, "pak_mods"))];
        Assert.Single(files);
    }

    [Fact]
    public void Disable_RemovesOnlyItsOwnPak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord first = new() { Id = 1, Index = 1, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["a.pak"] };
        ModRecord second = new() { Id = 2, Index = 2, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["b.pak"] };
        WritePak(paths, 1, "a.pak", "content-a");
        WritePak(paths, 2, "b.pak", "content-b");

        PakMountService service = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(first, NumberedProfile);
        service.Enable(second, NumberedProfile);

        string pakMods = Path.Combine(gameRoot, "pak_mods");
        Assert.Equal(2, Directory.EnumerateFiles(pakMods).Count());

        first.Enabled = false;
        PakMountReport report = service.Disable(first, NumberedProfile);

        Assert.Equal(1, report.Removed);
        Assert.Single(Directory.EnumerateFiles(pakMods));
    }

    [Fact]
    public void CrossGameMod_IsRejected()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord otherGame = new() { Id = 1, Index = 1, GameId = "mhw", Kind = ModKind.PakFiles, RelativeFiles = ["a.pak"] };
        WritePak(paths, 1, "a.pak", "content-a");
        PakMountService service = new(paths, gameRoot, GameId, [otherGame], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());

        Assert.Throws<InvalidOperationException>(() => service.Enable(otherGame, NumberedProfile));
    }

    [Fact]
    public void Disable_WithMissingSource_KeepsUnprovenNumberedPaks()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord first = new() { Id = 1, Index = 1, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["a.pak"] };
        ModRecord second = new() { Id = 2, Index = 2, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["b.pak"] };
        WritePak(paths, 1, "a.pak", "content-a");
        WritePak(paths, 2, "b.pak", "content-b");

        PakMountService service = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(second, NumberedProfile);

        string pakMods = Path.Combine(gameRoot, "pak_mods");
        string[] before = [.. Directory.EnumerateFiles(pakMods).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
        Assert.NotEmpty(before);

        // 模拟本 MOD 的源文件被杀软隔离/手工删除：禁用时没有肯定证据，绝不能删未证明归属的 pak。
        File.Delete(Path.Combine(paths.FilesDirectory(1), "a.pak"));
        PakMountReport report = service.Disable(first, NumberedProfile);

        Assert.Equal(0, report.Removed);
        Assert.Equal(before, Directory.EnumerateFiles(pakMods).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Contains(report.Warnings, w => w.Contains("无法确认"));
    }

    [Fact]
    public void Disable_KeepName_WithMissingRemainingSource_KeepsOtherModsSameNamedPak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = Path.Combine(gameRoot, "pak_mods");

        ModRecord target = new() { Id = 1, Index = 1, GameId = GameId, Kind = ModKind.PakFiles, RelativeFiles = ["cool.pak"] };
        ModRecord other = new() { Id = 2, Index = 2, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["cool.pak"] };
        WritePak(paths, 1, "cool.pak", "target-content");
        WritePak(paths, 2, "cool.pak", "other-content");

        PakMountService service = new(paths, gameRoot, GameId, [target, other], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(other, KeepNameProfile);
        string deployed = Path.Combine(pakMods, "cool.pak");
        Assert.Equal("other-content", File.ReadAllText(deployed));

        // 故障注入：仍启用的 B 的源文件被杀软隔离/手工删除，且与要禁用的 A 的 pak 同名。
        // 此时 pak_mods/cool.pak 无法证明不属于 B（保留原名模式下 A 的计划会把它算成自己的），
        // 禁用 A 绝不能删掉 B 已部署的 pak。
        File.Delete(Path.Combine(paths.FilesDirectory(2), "cool.pak"));
        target.Enabled = true;
        PakMountReport report = service.Disable(target, KeepNameProfile);

        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(deployed));
        Assert.Equal("other-content", File.ReadAllText(deployed));
        Assert.Contains(report.Warnings, w => w.Contains("保守保留"));
    }

    [Fact]
    public void Disable_Numbered_WithMissingRemainingSource_KeepsSharedNumberedPak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = Path.Combine(gameRoot, "pak_mods");

        ModRecord target = new() { Id = 1, Index = 1, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["a.pak"] };
        ModRecord other = new() { Id = 2, Index = 2, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["b.pak"] };
        WritePak(paths, 1, "a.pak", "shared-bytes");
        WritePak(paths, 2, "b.pak", "shared-bytes");

        PakMountService service = new(paths, gameRoot, GameId, [target, other], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(target, NumberedProfile);
        service.Enable(other, NumberedProfile);
        string[] before = [.. Directory.EnumerateFiles(pakMods)];
        Assert.Single(before);

        // B 的源缺失 ⇒ 无法证明这个编号 pak 只属于 A；B 仍启用，禁用 A 时必须保留共用文件。
        File.Delete(Path.Combine(paths.FilesDirectory(2), "b.pak"));
        PakMountReport report = service.Disable(target, NumberedProfile);

        Assert.Equal(0, report.Removed);
        Assert.Equal(before, Directory.EnumerateFiles(pakMods).ToArray());
        Assert.Contains(report.Warnings, w => w.Contains("保守保留"));
    }

    [Fact]
    public void Disable_KeepName_WithCompleteSources_RemovesOnlyItsOwnPak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = Path.Combine(gameRoot, "pak_mods");

        ModRecord target = new() { Id = 1, Index = 1, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["a.pak"] };
        ModRecord other = new() { Id = 2, Index = 2, GameId = GameId, Enabled = true, Kind = ModKind.PakFiles, RelativeFiles = ["b.pak"] };
        WritePak(paths, 1, "a.pak", "content-a");
        WritePak(paths, 2, "b.pak", "content-b");

        PakMountService service = new(paths, gameRoot, GameId, [target, other], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(target, KeepNameProfile);
        service.Enable(other, KeepNameProfile);
        Assert.Equal(2, Directory.EnumerateFiles(pakMods).Count());

        // 剩余 MOD 的源完整时不能过度保守：仍要删掉本 MOD 自己的 pak。
        PakMountReport report = service.Disable(target, KeepNameProfile);

        Assert.Equal(1, report.Removed);
        Assert.False(File.Exists(Path.Combine(pakMods, "a.pak")));
        Assert.True(File.Exists(Path.Combine(pakMods, "b.pak")));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("保守保留"));
    }

    // ---- A-07：写入/删除中途失败由 FileUndoJournal 逆序回滚，不留下半套 pak ----

    [Fact]
    public void Enable_WriteFailure_RollsBackOverwrittenPakAndCleansUndoRoot()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        // 第一个文件覆盖前已存在（旧内容必须先备份再覆盖）；第二个文件的落点被同名目录占住，写入必失败。
        File.WriteAllText(Path.Combine(pakMods, "a.pak"), "old-a");
        Directory.CreateDirectory(Path.Combine(pakMods, "b.pak"));

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["a.pak", "b.pak"],
        };
        WritePak(paths, 1, "a.pak", "new-a");
        WritePak(paths, 1, "b.pak", "new-b");
        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());

        Assert.ThrowsAny<IOException>(() => service.Enable(mod, KeepNameProfile));

        Assert.Equal("old-a", File.ReadAllText(Path.Combine(pakMods, "a.pak")));
        Assert.True(Directory.Exists(Path.Combine(pakMods, "b.pak")), "失败现场（占位目录）不应被误删");
        Assert.Empty(Directory.EnumerateDirectories(pakMods, ".hb-undo-*"));
    }

    [Fact]
    public void Disable_MoveFailure_KeepsEveryPakAndCleansUndoRoot()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["a.pak", "b.pak"],
        };
        WritePak(paths, 1, "a.pak", "content-a");
        WritePak(paths, 1, "b.pak", "content-b");
        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(mod, KeepNameProfile);
        string pakA = Path.Combine(pakMods, "a.pak");
        string pakB = Path.Combine(pakMods, "b.pak");
        Assert.True(File.Exists(pakA));
        Assert.True(File.Exists(pakB));

        // 独占打开 b.pak：撤出时移走它必然失败。无论 HashSet 迭代顺序如何，
        // 已移走的 pak 都必须移回、不能少文件。
        using (new FileStream(pakB, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => service.Disable(mod, KeepNameProfile));
        }

        Assert.True(File.Exists(pakA), "已移入撤销目录的 pak 必须移回原位");
        Assert.True(File.Exists(pakB));
        Assert.Empty(Directory.EnumerateDirectories(pakMods, ".hb-undo-*"));
    }
}
