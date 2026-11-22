using HunterBox.Application.Common;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;

namespace HunterBox.Tests;

/// <summary>A-02：路径发现规则在 Application 层可测（缺省才写回、单款失败不影响其它游戏）。</summary>
public class GameDiscoveryServiceTests
{
    private const string DetectedGameRoot = @"C:\Games\Wilds";
    private const string DetectedSaveRoot = @"C:\Saves\Wilds";

    [Fact]
    public void EnsureDetected_MissingPaths_WritesBothDetectedValues()
    {
        FakeSettings settings = new();
        FakeProbe probe = new()
        {
            GameRoots = { [TestGames.Wilds.Id] = DetectedGameRoot },
            SaveRoots = { [TestGames.Wilds.Id] = DetectedSaveRoot },
        };
        GameDiscoveryService service = new(settings, probe, new RecordingLogger());

        bool changed = service.EnsureDetected(TestGames.Wilds);

        Assert.True(changed);
        Assert.Equal(DetectedGameRoot, settings.Get(TestGames.Wilds.Id, SettingsKeys.GamePath));
        Assert.Equal(DetectedSaveRoot, settings.Get(TestGames.Wilds.Id, SettingsKeys.SavePath));
        Assert.Equal(1, probe.GameCalls);
        Assert.Equal(1, probe.SaveCalls);
    }

    [Fact]
    public void EnsureDetected_ExistingPaths_KeepsUserValuesAndSkipsProbe()
    {
        FakeSettings settings = new();
        settings.Set(TestGames.Wilds.Id, SettingsKeys.GamePath, @"D:\MyGame");
        settings.Set(TestGames.Wilds.Id, SettingsKeys.SavePath, @"D:\MySave");
        FakeProbe probe = new()
        {
            GameRoots = { [TestGames.Wilds.Id] = DetectedGameRoot },
            SaveRoots = { [TestGames.Wilds.Id] = DetectedSaveRoot },
        };
        GameDiscoveryService service = new(settings, probe, new RecordingLogger());

        bool changed = service.EnsureDetected(TestGames.Wilds);

        Assert.False(changed);
        Assert.Equal(@"D:\MyGame", settings.Get(TestGames.Wilds.Id, SettingsKeys.GamePath));
        Assert.Equal(@"D:\MySave", settings.Get(TestGames.Wilds.Id, SettingsKeys.SavePath));
        Assert.Equal(0, probe.GameCalls);
        Assert.Equal(0, probe.SaveCalls);
    }

    [Fact]
    public void EnsureDetected_ProbeFindsNothing_DoesNotWrite()
    {
        FakeSettings settings = new();
        GameDiscoveryService service = new(settings, new FakeProbe(), new RecordingLogger());

        bool changed = service.EnsureDetected(TestGames.Wilds);

        Assert.False(changed);
        Assert.Equal(string.Empty, settings.Get(TestGames.Wilds.Id, SettingsKeys.GamePath));
        Assert.Equal(string.Empty, settings.Get(TestGames.Wilds.Id, SettingsKeys.SavePath));
    }

    [Fact]
    public void DetectAllMissing_OneGameThrows_ContinuesRemainingGames()
    {
        FakeSettings settings = new();
        FakeProbe probe = new()
        {
            GameRoots =
            {
                [TestGames.Wilds.Id] = DetectedGameRoot,
                [TestGames.World.Id] = @"C:\Games\World",
            },
            SaveRoots = { [TestGames.World.Id] = @"C:\Saves\World" },
            GameFailures = { [TestGames.Rise.Id] = new IOException("读取注册表失败") },
        };
        RecordingLogger logger = new();
        GameDiscoveryService service = new(settings, probe, logger);

        int changed = service.DetectAllMissing([TestGames.Wilds, TestGames.Rise, TestGames.World]);

        Assert.Equal(2, changed);
        Assert.Equal(DetectedGameRoot, settings.Get(TestGames.Wilds.Id, SettingsKeys.GamePath));
        Assert.Equal(@"C:\Games\World", settings.Get(TestGames.World.Id, SettingsKeys.GamePath));
        Assert.Contains(logger.Warnings, warning => warning.Contains(TestGames.Rise.DisplayName));
    }

    /// <summary>多账号歧义时 Application 不写回，探针只返回候选。</summary>
    [Fact]
    public void EnsureDetected_MultipleSaveCandidates_SkipsAmbiguousWrite()
    {
        FakeSettings settings = new();
        FakeProbe probe = new();
        probe.MultipleSaveCandidates[TestGames.Wilds.Id] = [@"C:\Saves\A", @"C:\Saves\B"];
        GameDiscoveryService service = new(settings, probe, new RecordingLogger());

        service.EnsureDetected(TestGames.Wilds);

        Assert.Equal(string.Empty, settings.Get(TestGames.Wilds.Id, SettingsKeys.SavePath));
    }

    /// <summary>Invalidate 只转发请求的缓存项，且空请求不调用探测。</summary>
    [Fact]
    public void Invalidate_ForwardsRequestedScopes()
    {
        FakeProbe probe = new();
        GameDiscoveryService service = new(new FakeSettings(), probe, new RecordingLogger());

        service.Invalidate(TestGames.Wilds.Id, gameRoot: true, saveRoots: false);
        service.Invalidate(TestGames.Wilds.Id, gameRoot: false, saveRoots: false);

        string forwarded = Assert.Single(probe.Invalidated);
        Assert.Equal($"{TestGames.Wilds.Id}:True:False", forwarded);
    }

    private sealed class FakeProbe : IGameInstallProbe
    {
        public Dictionary<string, string?> GameRoots { get; } = [];

        public Dictionary<string, string?> SaveRoots { get; } = [];

        public Dictionary<string, Exception> GameFailures { get; } = [];

        public int GameCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public string? FindGameRoot(GameProfile game)
        {
            GameCalls++;
            if (GameFailures.TryGetValue(game.Id, out Exception? error))
            {
                throw error;
            }

            return GameRoots.GetValueOrDefault(game.Id);
        }

        public Dictionary<string, IReadOnlyList<string>> MultipleSaveCandidates { get; } = [];

        public List<string> Invalidated { get; } = [];

        public void Invalidate(string gameId, bool gameRoot, bool saveRoots) =>
            Invalidated.Add($"{gameId}:{gameRoot}:{saveRoots}");

        public IReadOnlyList<string> FindSaveRootCandidates(GameProfile game)
        {
            SaveCalls++;
            if (MultipleSaveCandidates.TryGetValue(game.Id, out IReadOnlyList<string>? candidates))
            {
                return candidates;
            }

            return SaveRoots.TryGetValue(game.Id, out string? root) && root is not null ? [root] : [];
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void WarnBatch(IReadOnlyList<string> messages) => Warnings.AddRange(messages);

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
