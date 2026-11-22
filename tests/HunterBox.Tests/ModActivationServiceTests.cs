using HunterBox.Application.Activation;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

public class ModActivationServiceTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";

    private sealed record Box(BoxPaths Paths, string GameRoot, GameProfile Game, ModRepository Store, ModActivationService Service);

    private static Box Create(TempDir temp, GameProfile game)
    {
        TestBox<ModRepository> box = TestBox.Create(temp, game);
        ModActivationService service = new(box.Paths, box.Store, box.GameRoot, box.Game, new FileContentFingerprint(), new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        return new Box(box.Paths, box.GameRoot, box.Game, box.Store, service);
    }

    private static ModRecord AddMod(Box box, int id, params string[] files)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = box.Game.Id,
            Kind = files.All(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) ? ModKind.PakFiles : ModKind.LooseFiles,
            RelativeFiles = [.. files],
        };

        string filesDir = box.Paths.FilesDirectory(id);
        foreach (string file in files)
        {
            string target = Path.Combine(filesDir, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, $"content-of-{id}-{file}");
        }

        box.Store.Add(mod);
        return mod;
    }

    private static string GameFile(string gameRoot, string relative) =>
        Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Enable_LooseMod_CopiesFilesAndMarksEnabled()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, LooseFile);

        ActivationReport report = box.Service.Enable(mod, PakNamingMode.PatchNumbered);

        Assert.Equal(1, report.Loose.Copied);
        Assert.Null(report.Pak);
        Assert.True(mod.Enabled);
        Assert.True(File.Exists(GameFile(box.GameRoot, LooseFile)));
    }

    [Fact]
    public void Enable_PakMod_GoesToPakModsNotGameRoot()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, "cool.pak");

        ActivationReport report = box.Service.Enable(mod, PakNamingMode.PatchNumbered);

        Assert.NotNull(report.Pak);
        Assert.Equal(1, report.Pak!.Written);
        Assert.Equal(0, report.Loose.Copied);
        Assert.True(File.Exists(Path.Combine(box.GameRoot, "pak_mods", WildsPrefix + "016.pak")));
        Assert.False(File.Exists(Path.Combine(box.GameRoot, "cool.pak")), "pak 不应落到游戏根目录");
    }

    [Fact]
    public void Enable_PakConflict_ThrowsAndLeavesEverythingUntouched()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord first = AddMod(box, 1, "same.pak");
        ModRecord second = AddMod(box, 2, "same.pak", LooseFile);

        box.Service.Enable(first, PakNamingMode.KeepOriginalName);

        Assert.Throws<PakConflictException>(() => box.Service.Enable(second, PakNamingMode.KeepOriginalName));

        Assert.False(second.Enabled);
        Assert.False(File.Exists(GameFile(box.GameRoot, LooseFile)), "冲突时不应先铺散装文件");
    }

    [Fact]
    public void Enable_GameWithoutPakMods_KeepsPakAsLooseFile()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.World);
        ModRecord mod = AddMod(box, 1, "cool.pak");

        ActivationReport report = box.Service.Enable(mod, PakNamingMode.PatchNumbered);

        Assert.Null(report.Pak);
        Assert.True(File.Exists(Path.Combine(box.GameRoot, "cool.pak")));
        Assert.False(Directory.Exists(Path.Combine(box.GameRoot, "pak_mods")));
    }

    [Fact]
    public void Disable_RemovesBothLooseAndPakFiles()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, "cool.pak", LooseFile);

        box.Service.Enable(mod, PakNamingMode.PatchNumbered);
        ActivationReport report = box.Service.Disable(mod, PakNamingMode.PatchNumbered);

        Assert.NotNull(report.Pak);
        Assert.Equal(1, report.Pak!.Removed);
        Assert.Equal(1, report.Loose.Deleted);
        Assert.False(mod.Enabled);
        Assert.False(File.Exists(Path.Combine(box.GameRoot, "pak_mods", WildsPrefix + "016.pak")));
        Assert.False(File.Exists(GameFile(box.GameRoot, LooseFile)));
    }

    [Fact]
    public void Recopy_RequiresEnabledMod()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, LooseFile);

        Assert.Throws<InvalidOperationException>(() =>
            box.Service.Recopy(mod, PakNamingMode.PatchNumbered));
    }

    [Fact]
    public void Recopy_LooseFailure_KeepsPreviouslyWrittenPak()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, "cool.pak", LooseFile);
        box.Service.Enable(mod, PakNamingMode.PatchNumbered);

        string pakMods = Path.Combine(box.GameRoot, "pak_mods");
        string originalPak = Assert.Single(Directory.EnumerateFiles(pakMods))
            .Split(Path.DirectorySeparatorChar)[^1];

        // 已启用状态下制造散装写入失败：natives 变成同名文件，无法创建目录。
        string looseRoot = Path.Combine(box.GameRoot, "natives");
        if (Directory.Exists(looseRoot))
        {
            Directory.Delete(looseRoot, recursive: true);
        }

        File.WriteAllText(looseRoot, "block");

        Assert.ThrowsAny<IOException>(() => box.Service.Recopy(mod, PakNamingMode.PatchNumbered));

        // 本次 pak 内容未变（Enable 会 skip），回滚绝不能把它删掉——否则 MOD 显示启用但游戏内失效。
        Assert.True(mod.Enabled);
        Assert.True(
            File.Exists(Path.Combine(pakMods, originalPak)),
            "回滚误删了本次并未重写的既有 pak");
        Assert.False(File.Exists(GameFile(box.GameRoot, LooseFile)), "散装文件不应部分写入");
    }

    [Fact]
    public void Enable_PersistsEnabledState()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, LooseFile);

        box.Service.Enable(mod, PakNamingMode.PatchNumbered);

        ModRepository reloaded = ModRepository.Load(box.Paths);
        Assert.True(Assert.Single(reloaded.Mods).Enabled);
        Assert.Equal(TestGames.Wilds.Id, reloaded.Mods[0].GameId);
    }

    [Fact]
    public void Enable_OtherGameMod_IsRejectedWithClearMessage()
    {
        using TempDir temp = TempDir.Create();
        Box wildsBox = Create(temp, TestGames.Wilds);
        Box worldBox = Create(temp, TestGames.World);
        ModRecord worldMod = AddMod(worldBox, 1, LooseFile);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => wildsBox.Service.Enable(worldMod, PakNamingMode.PatchNumbered));

        Assert.Contains("不属于当前游戏", ex.Message);
        Assert.False(worldMod.Enabled);
        Assert.False(File.Exists(GameFile(wildsBox.GameRoot, LooseFile)), "跨游戏操作不能动文件");
    }

    [Fact]
    public void LegacyUnassignedMod_CannotBeEnabledOrDisabled()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord legacy = new()
        {
            Id = 99,
            Index = 99,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        box.Store.Add(legacy);

        Assert.Throws<InvalidOperationException>(() => box.Service.Enable(legacy, PakNamingMode.PatchNumbered));
        Assert.Throws<InvalidOperationException>(() => box.Service.Disable(legacy, PakNamingMode.PatchNumbered));
    }

    // ----：取消令牌在改动文件前生效（安全点取消，不留半套状态） ----

    [Fact]
    public void Enable_CanceledToken_TouchesNothing()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, "cool.pak", LooseFile);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => box.Service.Enable(mod, PakNamingMode.PatchNumbered, cts.Token));

        Assert.False(mod.Enabled);
        Assert.False(File.Exists(GameFile(box.GameRoot, LooseFile)));
        Assert.False(File.Exists(Path.Combine(box.GameRoot, "pak_mods", WildsPrefix + "016.pak")));
    }

    [Fact]
    public void Disable_CanceledToken_KeepsEnabledStateAndFiles()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp, TestGames.Wilds);
        ModRecord mod = AddMod(box, 1, "cool.pak", LooseFile);
        box.Service.Enable(mod, PakNamingMode.PatchNumbered);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => box.Service.Disable(mod, PakNamingMode.PatchNumbered, cts.Token));

        Assert.True(mod.Enabled);
        Assert.True(File.Exists(GameFile(box.GameRoot, LooseFile)));
        Assert.True(File.Exists(Path.Combine(box.GameRoot, "pak_mods", WildsPrefix + "016.pak")));
    }
}
