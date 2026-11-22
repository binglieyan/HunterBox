using HunterBox.Application.Settings;
using HunterBox.Domain.Games;

namespace HunterBox.Tests;

/// <summary>游戏会话：初始游戏、目录读取、切换与探测结果写回（A-06）。</summary>
public class GameSessionTests
{
    [Fact]
    public void Constructor_ReadsSavedGameAndRoot()
    {
        MemorySettings settings = new();
        settings.Set(SettingsKeys.Section, SettingsKeys.Game, GameCatalog.Rise.Id);
        settings.Set(GameCatalog.Rise.Id, SettingsKeys.GamePath, @"D:\Games\Rise");

        GameSession session = new(new SettingsCoordinator(settings));

        Assert.Equal(GameCatalog.Rise.Id, session.Game.Id);
        Assert.Equal(@"D:\Games\Rise", session.GameRoot);
    }

    [Fact]
    public void Constructor_UnknownGame_FallsBackToWilds()
    {
        MemorySettings settings = new();
        settings.Set(SettingsKeys.Section, SettingsKeys.Game, "not-a-game");

        GameSession session = new(new SettingsCoordinator(settings));

        Assert.Equal(GameCatalog.Wilds.Id, session.Game.Id);
        Assert.Equal(string.Empty, session.GameRoot);
    }

    [Fact]
    public void Select_SwitchesGameAndReadsItsRoot()
    {
        MemorySettings settings = new();
        settings.Set(GameCatalog.World.Id, SettingsKeys.GamePath, @"D:\Games\World");
        GameSession session = new(new SettingsCoordinator(settings));

        session.Select(GameCatalog.World);

        Assert.Equal(GameCatalog.World.Id, session.Game.Id);
        Assert.Equal(@"D:\Games\World", session.GameRoot);
    }

    [Fact]
    public void ReloadRoot_PicksUpSettingsChange()
    {
        MemorySettings settings = new();
        GameSession session = new(new SettingsCoordinator(settings));
        settings.Set(GameCatalog.Wilds.Id, SettingsKeys.GamePath, @"E:\Steam\Wilds");

        session.ReloadRoot();

        Assert.Equal(@"E:\Steam\Wilds", session.GameRoot);
    }

    /// <summary>内存设置替身：不落盘，行为与只读降级无关。</summary>
    private sealed class MemorySettings : ISettings
    {
        private readonly Dictionary<(string Section, string Key), string> _values = [];

        public bool IsReadOnly => false;

        public string? LoadWarning => null;

        public string Get(string section, string key, string fallback = "") =>
            _values.TryGetValue((section, key), out string? value) ? value : fallback;

        public bool GetBool(string section, string key, bool fallback = false) =>
            Get(section, key, fallback ? "true" : "false") is "true" or "1";

        public void Set(string section, string key, string? value) =>
            _values[(section, key)] = value ?? string.Empty;

        public void SetBool(string section, string key, bool value) =>
            Set(section, key, value ? "true" : "false");

        public void Remove(string section, string key) => _values.Remove((section, key));

        public void UpdateBatch(Action<ISettings> update) => update(this);
    }
}
