using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>反复禁用/启用不能把编号一路推高（曾经 007→009→011 无限增长）。</summary>
public class PakNumberStabilityTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string GameId = "mhws";
    private static readonly PakProfile Profile = new(WildsPrefix, 6, PakNamingMode.PatchNumbered);

    private static ModRecord AddMod(BoxPaths paths, int id, string pakName, string content)
    {
        ModRecord mod = new() { Id = id, Index = id, GameId = GameId, Kind = ModKind.PakFiles, RelativeFiles = [pakName] };
        string dir = paths.FilesDirectory(id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, pakName), content);
        return mod;
    }

    [Fact]
    public void RepeatedDisableEnable_KeepsSameNumber()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        ModRecord first = AddMod(paths, 1, "a.pak", "content-a");
        ModRecord second = AddMod(paths, 2, "b.pak", "content-b");
        first.Enabled = true;
        second.Enabled = true;

        PakMountService service = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(first, Profile);
        service.Enable(second, Profile);

        string firstFile = Path.Combine(pakMods, WildsPrefix + "007.pak");
        Assert.True(File.Exists(firstFile));
        Assert.True(File.Exists(Path.Combine(pakMods, WildsPrefix + "008.pak")));

        // 反复禁用/启用第一个 MOD，编号必须回到 007，而不是继续增长
        for (int i = 0; i < 3; i++)
        {
            first.Enabled = false;
            service.Disable(first, Profile);
            first.Enabled = true;
            service.Enable(first, Profile);
        }

        Assert.True(File.Exists(firstFile), "重新启用应复用最小可用编号 007");
        string[] files = [.. Directory.EnumerateFiles(pakMods).Select(Path.GetFileName).OfType<string>()];
        Assert.Equal(2, files.Length);
        Assert.DoesNotContain(WildsPrefix + "009.pak", files);
    }

    [Fact]
    public void GameRootPatches_AreNeverTaken()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string pakMods = temp.Dir("game", "pak_mods");

        // 游戏自带 007–010
        for (int n = 7; n <= 10; n++)
        {
            File.WriteAllText(Path.Combine(gameRoot, WildsPrefix + n.ToString("000") + ".pak"), "game");
        }

        ModRecord mod = AddMod(paths, 1, "a.pak", "content-a");
        PakMountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new FileContentFingerprint());
        service.Enable(mod, Profile);

        Assert.True(File.Exists(Path.Combine(pakMods, WildsPrefix + "011.pak")));
        Assert.False(File.Exists(Path.Combine(pakMods, WildsPrefix + "007.pak")));
    }
}
