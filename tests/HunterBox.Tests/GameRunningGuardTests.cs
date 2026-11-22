using HunterBox.Application.Activation;
using HunterBox.Application.Cleanup;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 「检测游戏运行」接线：守卫在启用/禁用/重新复制/清理前拦截运行中的游戏，且不破坏事务逻辑。
/// </summary>
public class GameRunningGuardTests
{
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";

    private sealed class FakeDetector(bool running) : IGameProcessDetector
    {
        public string? LastExecutableName { get; private set; }

        public bool IsRunning(string executableName)
        {
            LastExecutableName = executableName;
            return running;
        }
    }

    private sealed record Fixture(BoxPaths Paths, string GameRoot, ModRepository Store, ModRecord Mod);

    private static Fixture Create(TempDir temp)
    {
        TestBox<ModRepository> box = TestBox.Create(temp);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = box.Game.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        File.WriteAllText(box.SourceFile(1, LooseFile), "mod-content");
        box.Store.Add(mod);
        return new Fixture(box.Paths, box.GameRoot, box.Store, mod);
    }

    private static ModActivationService CreateService(
        Fixture fixture,
        GameRunningGuard? guard = null,
        bool checkPakInternalFiles = false,
        IPakArchiveReader? pakReader = null) => new(
            fixture.Paths,
            fixture.Store,
            fixture.GameRoot,
            TestGames.Wilds,
            new FileContentFingerprint(),
            new ActivationOptions
            {
                FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                GameRunningGuard = guard,
                CheckPakInternalFiles = checkPakInternalFiles,
                PakReader = pakReader,
            });

    private static string GameFile(Fixture fixture, string relative) =>
        Path.Combine(fixture.GameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Enable_GameRunning_ThrowsAndTouchesNothing()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = Create(temp);
        FakeDetector detector = new(running: true);
        ModActivationService service = CreateService(fixture, new GameRunningGuard(TestGames.Wilds, enabled: true, detector));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => service.Enable(fixture.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("游戏正在运行", ex.Message);
        Assert.Equal("MonsterHunterWilds.exe", detector.LastExecutableName);
        Assert.False(fixture.Mod.Enabled);
        Assert.False(File.Exists(GameFile(fixture, LooseFile)), "被拦截时不应复制任何文件");
    }

    [Fact]
    public void Enable_GameRunningButCheckDisabled_StillSucceeds()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = Create(temp);
        FakeDetector detector = new(running: true);
        ModActivationService service = CreateService(fixture, new GameRunningGuard(TestGames.Wilds, enabled: false, detector));

        service.Enable(fixture.Mod, PakNamingMode.PatchNumbered);

        Assert.True(fixture.Mod.Enabled);
        Assert.True(File.Exists(GameFile(fixture, LooseFile)));
        Assert.Null(detector.LastExecutableName);
    }

    [Fact]
    public void Enable_GameNotRunning_Succeeds()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = Create(temp);
        ModActivationService service = CreateService(
            fixture,
            new GameRunningGuard(TestGames.Wilds, enabled: true, new FakeDetector(running: false)));

        service.Enable(fixture.Mod, PakNamingMode.PatchNumbered);

        Assert.True(fixture.Mod.Enabled);
    }

    [Fact]
    public void Disable_GameRunning_ThrowsAndKeepsFiles()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = Create(temp);
        CreateService(fixture).Enable(fixture.Mod, PakNamingMode.PatchNumbered);
        ModActivationService guarded = CreateService(
            fixture,
            new GameRunningGuard(TestGames.Wilds, enabled: true, new FakeDetector(running: true)));

        Assert.Throws<InvalidOperationException>(() => guarded.Disable(fixture.Mod, PakNamingMode.PatchNumbered));

        Assert.True(fixture.Mod.Enabled);
        Assert.True(File.Exists(GameFile(fixture, LooseFile)));
    }

    [Fact]
    public void Recopy_GameRunning_Throws()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = Create(temp);
        CreateService(fixture).Enable(fixture.Mod, PakNamingMode.PatchNumbered);
        ModActivationService guarded = CreateService(
            fixture,
            new GameRunningGuard(TestGames.Wilds, enabled: true, new FakeDetector(running: true)));

        Assert.Throws<InvalidOperationException>(() => guarded.Recopy(fixture.Mod, PakNamingMode.PatchNumbered));
    }

    [Fact]
    public void Cleanup_GameRunning_ThrowsAndKeepsEnabledState()
    {
        using TempDir temp = TempDir.Create();
        Fixture fixture = Create(temp);
        CreateService(fixture).Enable(fixture.Mod, PakNamingMode.PatchNumbered);
        ModCleanupService cleanup = new(
            fixture.Paths,
            fixture.Store,
            fixture.GameRoot,
            TestGames.Wilds,
            new HunterBox.Application.Paks.PakFingerprintCache(
                new FileContentFingerprint(),
                HunterBox.Infrastructure.IO.SystemFileSystem.Instance),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            PakNamingMode.PatchNumbered,
            new GameRunningGuard(TestGames.Wilds, enabled: true, new FakeDetector(running: true)));

        Assert.Throws<InvalidOperationException>(() => cleanup.Cleanup(CleanupScope.BoxInstalledOnly));

        Assert.True(fixture.Mod.Enabled);
        Assert.True(File.Exists(GameFile(fixture, LooseFile)));
    }

    [Fact]
    public void Guard_GameWithoutExecutableName_NeverThrows()
    {
        GameProfile noExe = TestGames.World with { ExecutableName = string.Empty };
        GameRunningGuard guard = new(noExe, enabled: true, new FakeDetector(running: true));

        guard.EnsureGameNotRunning();
    }

    [Fact]
    public void Guard_ThrowsReadableMessage()
    {
        GameRunningGuard guard = new(TestGames.Wilds, enabled: true, new FakeDetector(running: true));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(guard.EnsureGameNotRunning);

        Assert.Equal("游戏正在运行，请先退出游戏", ex.Message);
    }
}
