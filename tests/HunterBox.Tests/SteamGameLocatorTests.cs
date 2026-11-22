using HunterBox.Infrastructure.Games;

namespace HunterBox.Tests;

public class SteamGameLocatorTests
{
    [Fact]
    public void ParseEntries_ExtractsLibraryPaths()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" }
                "1" { "path" "D:\\SteamLibrary" }
                "2" { "path" "D:\\SteamLibrary" }
            }
            """;

        IReadOnlyList<string> libraries = [.. SteamLibraryIndex.ParseEntries(vdf).Select(entry => entry.Path)];

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], libraries);
    }

    [Fact]
    public void ParseEntries_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(SteamLibraryIndex.ParseEntries(null));
        Assert.Empty(SteamLibraryIndex.ParseEntries("   "));
    }

    [Fact]
    public void ReadInstallDir_ReadsManifest()
    {
        using TempDir temp = TempDir.Create();
        string steamApps = temp.Dir("steamapps");
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_2246340.acf"),
            "\"AppState\"\n{\n\t\"installdir\"\t\t\"MonsterHunterWilds\"\n}");

        Assert.Equal("MonsterHunterWilds", SteamGameLocator.ReadInstallDir(steamApps, 2246340));
        Assert.Null(SteamGameLocator.ReadInstallDir(steamApps, 1));
    }

    [Fact]
    public void Find_UsesManifestInstallDir()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string steamApps = temp.Dir("steam", "steamapps");
        string gameDir = temp.Dir("steam", "steamapps", "common", "MonsterHunterWilds");
        File.WriteAllText(Path.Combine(gameDir, "MonsterHunterWilds.exe"), string.Empty);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_2246340.acf"),
            "\"AppState\"\n{\n\t\"installdir\"\t\t\"MonsterHunterWilds\"\n}");

        string? found = SteamGameLocator.Find([root], 2246340, "MonsterHunterWilds", "MonsterHunterWilds.exe");

        Assert.Equal(gameDir, found);
    }

    [Fact]
    public void Find_FallsBackToFolderName()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        temp.Dir("steam", "steamapps");
        string gameDir = temp.Dir("steam", "steamapps", "common", "Monster Hunter World");
        File.WriteAllText(Path.Combine(gameDir, "MonsterHunterWorld.exe"), string.Empty);

        string? found = SteamGameLocator.Find([root], 582010, "Monster Hunter World", "MonsterHunterWorld.exe");

        Assert.Equal(gameDir, found);
    }

    [Fact]
    public void Find_WrongExecutable_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        temp.Dir("steam", "steamapps", "common", "MonsterHunterWilds");

        Assert.Null(SteamGameLocator.Find([root], 2246340, "MonsterHunterWilds", "MonsterHunterWilds.exe"));
    }
}
