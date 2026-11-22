using HunterBox.Application.Install;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;

namespace HunterBox.Tests;

/// <summary>
/// A-06：设置读写协调器把 INI 键名、默认值、批量保存与旧键清理收敛到 Application，
/// 对话框只负责控件值；这里覆盖读取、写入与回退语义。
/// </summary>
public class SettingsCoordinatorTests
{
    private static readonly GameProfile Game = GameCatalog.Wilds;

    [Fact]
    public void Read_EmptySettings_ReturnsDocumentedDefaults()
    {
        SettingsCoordinator coordinator = new(new FakeSettings());

        GameSettingsSnapshot snapshot = coordinator.Read(Game);

        Assert.Equal(string.Empty, snapshot.GamePath);
        Assert.Equal(string.Empty, snapshot.SavePath);
        Assert.Equal(InstallSourceMode.None, snapshot.InstallOption);
        Assert.True(snapshot.FixPakNumber);
        Assert.False(snapshot.CheckPakFiles);
        Assert.True(snapshot.CheckGameRunning);
        Assert.Equal(SettingsDefaults.Theme, snapshot.Theme);
    }

    [Fact]
    public void Read_StoredValues_ReturnsConfiguredValues()
    {
        FakeSettings settings = new();
        settings.Set(Game.Id, SettingsKeys.GamePath, @"D:\Games\Wilds");
        settings.Set(Game.Id, SettingsKeys.SavePath, @"C:\Saves\Wilds");
        settings.Set(InstallSourceSettings.Section, InstallSourceSettings.Key, "2");
        settings.SetBool(SettingsKeys.Section, SettingsKeys.FixPakNumber, false);
        settings.SetBool(SettingsKeys.Section, SettingsKeys.CheckPakFiles, true);
        settings.SetBool(SettingsKeys.Section, SettingsKeys.CheckGameRunning, false);
        settings.Set(SettingsKeys.Section, SettingsKeys.Theme, "dark");
        SettingsCoordinator coordinator = new(settings);

        GameSettingsSnapshot snapshot = coordinator.Read(Game);

        Assert.Equal(@"D:\Games\Wilds", snapshot.GamePath);
        Assert.Equal(@"C:\Saves\Wilds", snapshot.SavePath);
        Assert.Equal(InstallSourceMode.Move, snapshot.InstallOption);
        Assert.False(snapshot.FixPakNumber);
        Assert.True(snapshot.CheckPakFiles);
        Assert.False(snapshot.CheckGameRunning);
        Assert.Equal("dark", snapshot.Theme);
    }

    [Fact]
    public void Read_InvalidInstallOption_FallsBackToNone()
    {
        FakeSettings settings = new();
        settings.Set(InstallSourceSettings.Section, InstallSourceSettings.Key, "9");

        GameSettingsSnapshot snapshot = new SettingsCoordinator(settings).Read(Game);

        Assert.Equal(InstallSourceMode.None, snapshot.InstallOption);
    }

    [Fact]
    public void Save_WritesSnapshotInSingleBatchAndRemovesLegacyKeys()
    {
        FakeSettings settings = new();
        settings.Set("settings", "file_list", "legacy");
        settings.Set(SettingsKeys.Section, SettingsKeys.LegacyCheckPakInner, "true");
        settings.Set("settings", "install_optn", "1");
        settings.Set("settings", "ignore_caimogu", "true");
        settings.Set("settings", "enable_filter_mhmodel", "true");
        SettingsCoordinator coordinator = new(settings);
        int savesBefore = settings.SaveCount;
        GameSettingsSnapshot snapshot = new(
            @"E:\Games\Wilds",
            @"D:\Saves",
            InstallSourceMode.Copy,
            FixPakNumber: false,
            CheckPakFiles: true,
            CheckGameRunning: false,
            Theme: "light");

        coordinator.Save(Game, snapshot);

        Assert.Equal(@"E:\Games\Wilds", settings.Get(Game.Id, SettingsKeys.GamePath));
        Assert.Equal(@"D:\Saves", settings.Get(Game.Id, SettingsKeys.SavePath));
        Assert.Equal("1", settings.Get(InstallSourceSettings.Section, InstallSourceSettings.Key));
        Assert.False(settings.GetBool(SettingsKeys.Section, SettingsKeys.FixPakNumber, fallback: true));
        Assert.True(settings.GetBool(SettingsKeys.Section, SettingsKeys.CheckPakFiles));
        Assert.False(settings.GetBool(SettingsKeys.Section, SettingsKeys.CheckGameRunning, fallback: true));
        Assert.Equal("light", settings.Get(SettingsKeys.Section, SettingsKeys.Theme));
        Assert.Equal(string.Empty, settings.Get("settings", "file_list"));
        Assert.Equal(string.Empty, settings.Get(SettingsKeys.Section, SettingsKeys.LegacyCheckPakInner));
        Assert.Equal(string.Empty, settings.Get("settings", "install_optn"));
        Assert.Equal(string.Empty, settings.Get("settings", "ignore_caimogu"));
        Assert.Equal(string.Empty, settings.Get("settings", "enable_filter_mhmodel"));
        Assert.Equal(savesBefore + 1, settings.SaveCount);
    }

    [Fact]
    public void IsReadOnly_DelegatesToSettingsPort()
    {
        FakeSettings settings = new()
        {
            IsReadOnly = true,
            LoadWarning = "配置文件只读",
        };
        SettingsCoordinator coordinator = new(settings);

        Assert.True(coordinator.IsReadOnly);
        Assert.Equal("配置文件只读", coordinator.LoadWarning);
    }

    [Fact]
    public void ResolveCurrentGame_UnknownGameId_FallsBackToWilds()
    {
        FakeSettings settings = new();
        settings.Set(SettingsKeys.Section, SettingsKeys.Game, "not-a-game");

        GameProfile game = new SettingsCoordinator(settings).ResolveCurrentGame();

        Assert.Equal(GameCatalog.Wilds.Id, game.Id);
    }

    /// <summary>游戏目录键名只在协调器内定义，读取解析走协调器。</summary>
    [Fact]
    public void GamePath_ResolvesThroughCoordinator()
    {
        FakeSettings settings = new();
        settings.Set(Game.Id, SettingsKeys.GamePath, @"D:\Games\Wilds");
        SettingsCoordinator coordinator = new(settings);

        Assert.Equal(@"D:\Games\Wilds", coordinator.ResolveGamePath(Game));
        Assert.Equal(@"D:\Games\Wilds", coordinator.ResolveGamePath(Game.Id));
    }

    /// <summary>存档目录键名同样只在协调器内定义。</summary>
    [Fact]
    public void SavePath_RoundTripsThroughCoordinator()
    {
        FakeSettings settings = new();
        SettingsCoordinator coordinator = new(settings);

        coordinator.SetSavePath(Game.Id, @"C:\Saves\Wilds");

        Assert.Equal(@"C:\Saves\Wilds", coordinator.ResolveSavePath(Game));
        Assert.Equal(@"C:\Saves\Wilds", coordinator.ResolveSavePath(Game.Id));
    }
}
