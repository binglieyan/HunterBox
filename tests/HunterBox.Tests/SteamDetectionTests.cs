using System.Text;
using HunterBox.Application.Saves;
using HunterBox.Domain.Games;
using HunterBox.Infrastructure.Games;

namespace HunterBox.Tests;

/// <summary>
/// 游戏识别：libraryfolders.vdf 解析、config.vdf apps 映射、注册表探测输入组合、存档路径探测。
/// 注册表读取在 App 层；本测试覆盖纯逻辑（可注入的 VDF 内容 + 临时目录）。
/// </summary>
public class SteamDetectionTests
{
    private const int WildsAppId = 2246340;
    private const string WildsExe = "MonsterHunterWilds.exe";
    private const string WildsFolder = "MonsterHunterWilds";

    private static SteamLocateRequest Request(
        string? steamRoot,
        bool? installed = null,
        string? uninstall = null,
        string? libraryVdf = "",
        string? configVdf = "") => new()
        {
            AppId = WildsAppId,
            ExecutableName = WildsExe,
            DefaultFolderName = WildsFolder,
            SteamRoot = steamRoot,
            AppInstalled = installed,
            UninstallLocation = uninstall,
            LibraryFoldersVdf = libraryVdf,
            ConfigVdf = configVdf,
        };

    private static string LibraryFoldersVdf(params (string Path, int[] AppIds)[] libraries)
    {
        StringBuilder builder = new();
        builder.AppendLine("\"libraryfolders\"");
        builder.AppendLine("{");
        for (int i = 0; i < libraries.Length; i++)
        {
            builder.AppendLine($"\t\"{i}\"");
            builder.AppendLine("\t{");
            builder.AppendLine($"\t\t\"path\"\t\t\"{libraries[i].Path.Replace("\\", "\\\\")}\"");
            builder.AppendLine("\t\t\"apps\"");
            builder.AppendLine("\t\t{");
            foreach (int appId in libraries[i].AppIds)
            {
                builder.AppendLine($"\t\t\t\"{appId}\"\t\t\"123456\"");
            }

            builder.AppendLine("\t\t}");
            builder.AppendLine("\t}");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string WildsConfigVdf(bool installed)
    {
        string flag = installed ? "1" : "0";
        return $$"""
            "InstallConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "{{WildsAppId}}"
                                {
                                    "installed" "{{flag}}"
                                }
                            }
                        }
                    }
                }
            }
            """;
    }

    private static string CreateGameDir(TempDir temp, params string[] parts)
    {
        string directory = temp.Dir(parts);
        File.WriteAllText(Path.Combine(directory, WildsExe), string.Empty);
        return directory;
    }

    [Fact]
    public void ParseEntries_NewFormat_ReadsPathAndApps()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path" "C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "1" "2246340" "2" }
                }
                "1"
                {
                    "path" "D:\\SteamLibrary"
                    "apps" { "582010" "3" }
                }
            }
            """;

        IReadOnlyList<SteamLibraryEntry> entries = SteamLibraryIndex.ParseEntries(vdf);

        Assert.Equal(2, entries.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", entries[0].Path);
        Assert.Contains(WildsAppId, entries[0].AppIds);
        Assert.Equal(@"D:\SteamLibrary", entries[1].Path);
        Assert.Contains(582010, entries[1].AppIds);
    }

    [Fact]
    public void ParseEntries_OldFormat_ReadsNumericKeyPaths()
    {
        const string vdf = """
            "LibraryFolders"
            {
                "TimeNextStatsReport" "123"
                "1" "D:\\SteamLibrary"
                "2" "E:\\SteamLibrary"
            }
            """;

        IReadOnlyList<SteamLibraryEntry> entries = SteamLibraryIndex.ParseEntries(vdf);

        string[] expectedPaths = [@"D:\SteamLibrary", @"E:\SteamLibrary"];
        Assert.Equal(expectedPaths, entries.Select(e => e.Path));
        Assert.All(entries, e => Assert.Empty(e.AppIds));
    }

    [Fact]
    public void ParseEntries_BrokenText_ReturnsEmpty()
    {
        Assert.Empty(SteamLibraryIndex.ParseEntries("not a vdf {{{"));
        Assert.Empty(SteamLibraryIndex.ParseEntries(null));
        Assert.Null(SteamVdfParser.Parse("\"a\" {"));
    }

    [Fact]
    public void ConfigIndex_ReadsInstalledApps()
    {
        Assert.True(SteamConfigIndex.IsAppInstalled(WildsConfigVdf(installed: true), WildsAppId));
        Assert.False(SteamConfigIndex.IsAppInstalled(WildsConfigVdf(installed: false), WildsAppId));
        Assert.Null(SteamConfigIndex.IsAppInstalled(null, WildsAppId));
        Assert.Null(SteamConfigIndex.IsAppInstalled("broken {", WildsAppId));
    }

    [Fact]
    public void Locate_StandardDirectory_ReturnsGameDirectory()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string expected = CreateGameDir(temp, "steam", "steamapps", "common", WildsFolder);

        Assert.Equal(expected, SteamGameLocator.Locate(Request(root, installed: true)));
    }

    /// <summary>存档候选按游戏缓存，第二次探测复用同一个列表（不再枚举 userdata）。</summary>
    [Fact]
    public void FindSaveRootCandidates_SecondCall_ReturnsCachedList()
    {
        using TempDir temp = TempDir.Create();
        string accountApp = Path.Combine(
            temp.Path,
            "userdata",
            "12345",
            WildsAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(accountApp, "remote"));
        SteamGameProbe probe = new(temp.Path);

        IReadOnlyList<string> first = probe.FindSaveRootCandidates(GameCatalog.Wilds);
        IReadOnlyList<string> second = probe.FindSaveRootCandidates(GameCatalog.Wilds);

        Assert.Single(first);
        Assert.Same(first, second);
    }

    /// <summary>空候选按短 TTL 负缓存，手动 Invalidate 后能立即重新探测。</summary>
    [Fact]
    public void FindSaveRootCandidates_EmptyResultNegativeCache_InvalidateFindsDirectoryCreatedLater()
    {
        using TempDir temp = TempDir.Create();
        SteamGameProbe probe = new(temp.Path);
        Assert.Empty(probe.FindSaveRootCandidates(GameCatalog.Wilds));

        string accountApp = Path.Combine(
            temp.Path,
            "userdata",
            "12345",
            WildsAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(accountApp, "remote"));

        // 负缓存有效期内不重复枚举，返回空。
        Assert.Empty(probe.FindSaveRootCandidates(GameCatalog.Wilds));

        // 设置页手动刷新只失效缺失项（存档路径），下一次探测立即恢复。
        probe.Invalidate(GameCatalog.Wilds.Id, gameRoot: false, saveRoots: true);
        Assert.Single(probe.FindSaveRootCandidates(GameCatalog.Wilds));
    }

    /// <summary>负缓存 TTL 到期后无需手动失效也会重新探测。</summary>
    [Fact]
    public void FindSaveRootCandidates_NegativeCacheExpires_ProbesAgain()
    {
        using TempDir temp = TempDir.Create();
        SteamGameProbe probe = new(temp.Path, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Empty(probe.FindSaveRootCandidates(GameCatalog.Wilds));

        string accountApp = Path.Combine(
            temp.Path,
            "userdata",
            "12345",
            WildsAppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(accountApp, "remote"));

        Assert.Single(probe.FindSaveRootCandidates(GameCatalog.Wilds));
    }

    [Fact]
    public void Locate_UsesAppManifestInstallDir()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        temp.Dir("steam", "steamapps");
        File.WriteAllText(
            Path.Combine(root, "steamapps", $"appmanifest_{WildsAppId}.acf"),
            "\"AppState\"\n{\n\t\"installdir\"\t\t\"CustomWilds\"\n}");
        string expected = CreateGameDir(temp, "steam", "steamapps", "common", "CustomWilds");

        Assert.Equal(expected, SteamGameLocator.Locate(Request(root, installed: true)));
    }

    [Fact]
    public void Locate_NonDefaultLibrary_UsesLibraryFoldersAppsMapping()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string library = temp.Dir("library2");
        string expected = CreateGameDir(temp, "library2", "steamapps", "common", WildsFolder);

        string vdf = LibraryFoldersVdf((root, []), (library, [WildsAppId]));
        Assert.Equal(expected, SteamGameLocator.Locate(Request(root, installed: true, libraryVdf: vdf)));
    }

    [Fact]
    public void Locate_LibraryAppsMappingExcludesGame_SkipsLibrary()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string library = temp.Dir("library2");
        CreateGameDir(temp, "library2", "steamapps", "common", WildsFolder);

        string vdf = LibraryFoldersVdf((root, []), (library, [582010]));
        Assert.Null(SteamGameLocator.Locate(Request(root, installed: true, libraryVdf: vdf)));
    }

    [Fact]
    public void Locate_ConfigVdfSaysInstalled_RescansLibrariesIgnoringAppsMap()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string library = temp.Dir("library2");
        string expected = CreateGameDir(temp, "library2", "steamapps", "common", WildsFolder);

        string vdf = LibraryFoldersVdf((root, []), (library, [582010]));
        SteamLocateRequest request = Request(
            root,
            installed: null,
            libraryVdf: vdf,
            configVdf: WildsConfigVdf(installed: true));

        Assert.Equal(expected, SteamGameLocator.Locate(request));
    }

    [Fact]
    public void Locate_ConfigVdfSaysNotInstalled_SkipsSteamScan()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        CreateGameDir(temp, "steam", "steamapps", "common", WildsFolder);

        SteamLocateRequest request = Request(
            root,
            installed: null,
            libraryVdf: "",
            configVdf: WildsConfigVdf(installed: false));

        Assert.Null(SteamGameLocator.Locate(request));
    }

    [Fact]
    public void Locate_InstalledFlagFalse_SkipsSteamButUsesUninstallFallback()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        CreateGameDir(temp, "steam", "steamapps", "common", WildsFolder);
        string uninstall = CreateGameDir(temp, "custom", "MHWilds");

        Assert.Equal(
            uninstall,
            SteamGameLocator.Locate(Request(root, installed: false, uninstall: uninstall)));
    }

    [Fact]
    public void Locate_UninstallFallback_RequiresExecutable()
    {
        using TempDir temp = TempDir.Create();
        string uninstall = temp.Dir("custom", "MHWilds");

        Assert.Null(SteamGameLocator.Locate(Request(null, uninstall: uninstall)));
    }

    [Fact]
    public void Locate_MissingExecutable_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        temp.Dir("steam", "steamapps", "common", WildsFolder);

        Assert.Null(SteamGameLocator.Locate(Request(root, installed: true)));
    }

    [Fact]
    public void SavePath_FindsRemoteDirectory()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string expected = temp.Dir("steam", "userdata", "76561198000000000", WildsAppId.ToString(), "remote");

        Assert.Equal(expected, SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(root, WildsAppId)));
    }

    [Fact]
    public void SavePath_FallsBackToAppDirectoryWithoutRemote()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        string expected = temp.Dir("steam", "userdata", "76561198000000000", WildsAppId.ToString());

        Assert.Equal(expected, SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(root, WildsAppId)));
    }

    [Fact]
    public void SavePath_MultipleAccountsWithRemote_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        temp.Dir("steam", "userdata", "76561198000000001", WildsAppId.ToString(), "remote");
        temp.Dir("steam", "userdata", "76561198000000002", WildsAppId.ToString(), "remote");

        // 多个账号都有该游戏时不能静默选第一个并写入配置。
        Assert.Null(SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(root, WildsAppId)));
    }

    [Fact]
    public void SavePath_OneAccountWithGameAmongMany_ReturnsSingleCandidate()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");
        temp.Dir("steam", "userdata", "76561198000000001");
        string expected = temp.Dir("steam", "userdata", "76561198000000002", WildsAppId.ToString(), "remote");

        Assert.Equal(expected, SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(root, WildsAppId)));
    }

    [Fact]
    public void SavePath_UnknownOrMissingRoot_ReturnsNull()
    {
        using TempDir temp = TempDir.Create();
        string root = temp.Dir("steam");

        Assert.Null(SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(root, WildsAppId)));
        Assert.Null(SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(null, WildsAppId)));
        Assert.Null(SavePathSelection.SelectSingle(SteamSavePathLocator.FindCandidates(root, 0)));
    }

    /// <summary>零个或多个候选都不替用户选择，策略在 Application 单测覆盖。</summary>
    [Fact]
    public void SavePathSelection_ZeroOrMultipleCandidates_ReturnsNull()
    {
        Assert.Null(SavePathSelection.SelectSingle([]));
        Assert.Null(SavePathSelection.SelectSingle([@"C:\account-a", @"C:\account-b"]));
        Assert.Equal(@"C:\only", SavePathSelection.SelectSingle([@"C:\only"]));
    }

    [Fact]
    public void GameDirectoryProbe_IsGameDirectory_ChecksExecutable()
    {
        using TempDir temp = TempDir.Create();
        string empty = temp.Dir("empty");
        string withExe = CreateGameDir(temp, "game");
        GameProfile wilds = new("mhws", "怪物猎人荒野", null, 1, WildsAppId, WildsExe, WildsFolder);
        GameProfile noExe = new("mhw", "怪物猎人世界", null, 1);

        Assert.True(GameDirectoryProbe.IsGameDirectory(wilds, withExe));
        Assert.False(GameDirectoryProbe.IsGameDirectory(wilds, empty));
        Assert.False(GameDirectoryProbe.IsGameDirectory(wilds, null));
        Assert.NotNull(GameDirectoryProbe.FindExecutable(wilds, withExe));
        Assert.Null(GameDirectoryProbe.FindExecutable(wilds, empty));

        // 没有配置 exe 名的游戏退化为「目录存在」判断。
        Assert.True(GameDirectoryProbe.IsGameDirectory(noExe, empty));
        Assert.False(GameDirectoryProbe.IsGameDirectory(noExe, null));
    }

    /// <summary>只失效存档路径或只失效游戏目录，都必须重置 Steam 根目录探测。</summary>
    [Fact]
    public void Invalidate_AnyScope_ResetsSteamRootResolution()
    {
        SteamGameProbe probe = new(steamRoot: null);
        Assert.Empty(probe.FindSaveRootCandidates(GameCatalog.Wilds));
        Assert.Equal(1, probe.SteamRootResolutions);

        probe.Invalidate(GameCatalog.Wilds.Id, gameRoot: false, saveRoots: true);
        Assert.Empty(probe.FindSaveRootCandidates(GameCatalog.Wilds));
        Assert.Equal(2, probe.SteamRootResolutions);

        probe.Invalidate(GameCatalog.Wilds.Id, gameRoot: true, saveRoots: false);
        Assert.Null(probe.FindGameRoot(GameCatalog.Wilds));
        Assert.Equal(3, probe.SteamRootResolutions);
    }
}
