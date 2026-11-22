using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 编号顺延的回归测试：已装在 pak_mods 里的 pak 必须被计入编号起点，
/// 否则新 MOD 会覆盖旧文件（曾经发生过 016 被替换的问题）。
/// </summary>
public class PakNumberingRegressionTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string GameId = "mhws";
    private static readonly PakProfile NumberedProfile = new(WildsPrefix, 6, PakNamingMode.PatchNumbered);

    private static void WritePak(BoxPaths paths, int modId, string pakName, string content)
    {
        string dir = paths.FilesDirectory(modId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, pakName), content);
    }

    [Fact]
    public void Enable_ContinuesAfterPakAlreadyInPakMods()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        // 模拟：根目录有游戏自带 015，pak_mods 里已有此前安装的 016
        File.WriteAllText(Path.Combine(gameRoot, WildsPrefix + "015.pak"), "game");
        File.WriteAllText(Path.Combine(pakMods, WildsPrefix + "016.pak"), "already-installed");

        ModRecord mod = new() { Id = 1, Index = 1, GameId = GameId, Kind = ModKind.PakFiles, RelativeFiles = ["new.pak"] };
        WritePak(paths, 1, "new.pak", "new-content");

        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(mod, NumberedProfile);

        Assert.True(File.Exists(Path.Combine(pakMods, WildsPrefix + "017.pak")), "新 MOD 应顺延为 017");
        Assert.Equal("already-installed", File.ReadAllText(Path.Combine(pakMods, WildsPrefix + "016.pak")));
    }

    [Fact]
    public void Enable_DoesNotOverwriteAnyExistingNumber()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        for (int number = 7; number <= 16; number++)
        {
            string name = WildsPrefix + number.ToString("000") + ".pak";
            File.WriteAllText(Path.Combine(pakMods, name), "existing-" + number);
        }

        ModRecord mod = new() { Id = 1, Index = 1, GameId = GameId, Kind = ModKind.PakFiles, RelativeFiles = ["new.pak"] };
        WritePak(paths, 1, "new.pak", "new-content");

        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(mod, NumberedProfile);

        Assert.True(File.Exists(Path.Combine(pakMods, WildsPrefix + "017.pak")));
        Assert.Equal("existing-16", File.ReadAllText(Path.Combine(pakMods, WildsPrefix + "016.pak")));
    }
}
