using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Migration;
using HunterBox.Infrastructure.Settings;

namespace HunterBox.Tests;

/// <summary>
/// 旧 INI 兼容：旧 <c>sjmodbox.ini</c> 键/段迁移到 <c>hunterbox.ini</c>、旧键别名读取、死键清理。
/// </summary>
public class IniSettingsLegacyMigrationTests
{
    private static string NewIniPath(TempDir temp) => Path.Combine(temp.Path, "hunterbox.ini");

    /// <summary>按真实启动顺序加载新配置并执行一次旧配置迁移（A-13：迁移归 LegacyMigrationService）。</summary>
    private static IniSettings LoadAndMigrate(TempDir temp)
    {
        BoxPaths paths = new(temp.Path);
        IniSettings settings = new(paths.SettingsFile);
        LegacyMigrationService.MigrateSettings(paths, settings);
        return settings;
    }

    [Fact]
    public void Migrate_LegacySjmodboxKeys_ToNewIni()
    {
        using TempDir temp = TempDir.Create();
        string legacy = temp.File("sjmodbox.ini", """
            [settings]
            game=2246340
            install_option=2
            check_game_running=false
            check_pak_files=true
            enable_fix_pak_num=false
            ignore_caimogu=true
            enable_filter_mhmodel=true
            [2246340]
            gamepath=C:\Games\MHWS
            savepath=D:\Saves\MHWS
            [582010]
            gamepath=C:\Games\MHW
            """);
        string legacyContent = File.ReadAllText(legacy);
        string newPath = NewIniPath(temp);

        IniSettings settings = LoadAndMigrate(temp);

        Assert.Equal("mhws", settings.Get("settings", "game"));
        Assert.Equal("2", settings.Get("settings", "install_option"));
        Assert.False(settings.GetBool("settings", "check_game_running", fallback: true));
        Assert.True(settings.GetBool("settings", "check_pak_files"));
        Assert.False(settings.GetBool("settings", "enable_fix_pak_num", fallback: true));
        Assert.Equal(@"C:\Games\MHWS", settings.Get("mhws", "gamepath"));
        Assert.Equal(@"D:\Saves\MHWS", settings.Get("mhws", "savepath"));
        Assert.Equal(@"C:\Games\MHW", settings.Get("mhw", "gamepath"));
        Assert.Equal("", settings.Get("mhr", "gamepath"));

        // 迁移结果已落盘，且不再写出已移除的死设置。
        Assert.True(File.Exists(newPath));
        string written = File.ReadAllText(newPath);
        Assert.Contains("[mhws]", written);
        Assert.Contains("game=mhws", written);
        Assert.DoesNotContain("ignore_caimogu", written);
        Assert.DoesNotContain("enable_filter_mhmodel", written);
        Assert.DoesNotContain("file_list", written);

        // 旧文件只读，迁移不修改它。
        Assert.Equal(legacyContent, File.ReadAllText(legacy));
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void Migrate_DoesNotOverwriteExistingNewValues()
    {
        using TempDir temp = TempDir.Create();
        temp.File("hunterbox.ini", """
            [settings]
            game=mhr
            install_option=1
            [mhws]
            gamepath=D:\Existing
            """);
        temp.File("sjmodbox.ini", """
            [settings]
            game=2246340
            install_option=2
            [2246340]
            gamepath=C:\Old
            savepath=D:\OldSaves
            """);

        IniSettings settings = LoadAndMigrate(temp);

        Assert.Equal("mhr", settings.Get("settings", "game"));
        Assert.Equal("1", settings.Get("settings", "install_option"));
        Assert.Equal(@"D:\Existing", settings.Get("mhws", "gamepath"));
        // 新值缺失的键仍然会被迁移。
        Assert.Equal(@"D:\OldSaves", settings.Get("mhws", "savepath"));
    }

    [Theory]
    [InlineData("mhr", "mhr")]
    [InlineData("1446780", "mhr")]
    [InlineData("mhw", "mhw")]
    [InlineData("582010", "mhw")]
    [InlineData("mhws", "mhws")]
    [InlineData("2246340", "mhws")]
    public void Migrate_GameValue_MapsToStableId(string legacyValue, string expected)
    {
        using TempDir temp = TempDir.Create();
        string newPath = NewIniPath(temp);
        temp.File("sjmodbox.ini", $"[settings]\ngame={legacyValue}\n");

        IniSettings settings = LoadAndMigrate(temp);

        Assert.Equal(expected, settings.Get("settings", "game"));
        Assert.Equal(expected, new IniSettings(newPath).Get("settings", "game"));
    }

    [Fact]
    public void Migrate_OldInstallOptionKey_IsAliased()
    {
        using TempDir temp = TempDir.Create();
        temp.File("sjmodbox.ini", "[settings]\ninstall_optn=2\n");

        IniSettings settings = LoadAndMigrate(temp);

        Assert.Equal("2", settings.Get("settings", "install_option"));
    }

    [Fact]
    public void Migrate_NoLegacyFile_DoesNotCreateNewIni()
    {
        using TempDir temp = TempDir.Create();
        string newPath = NewIniPath(temp);

        IniSettings settings = LoadAndMigrate(temp);

        Assert.Equal("fallback", settings.Get("settings", "game", "fallback"));
        Assert.False(File.Exists(newPath), "没有旧配置时不应凭空写出新 INI");
    }

    [Fact]
    public void Migrate_CorruptLegacyFile_DoesNotThrowAndSkipsMigration()
    {
        using TempDir temp = TempDir.Create();
        string newPath = NewIniPath(temp);
        temp.File("sjmodbox.ini", "not an ini\n[broken\n=no-key\n");

        IniSettings settings = LoadAndMigrate(temp);

        Assert.Equal("", settings.Get("settings", "game"));
        Assert.False(File.Exists(newPath));
    }

    [Fact]
    public void Migrate_IsIdempotent_AndKeepsLaterChanges()
    {
        using TempDir temp = TempDir.Create();
        temp.File("sjmodbox.ini", "[settings]\ncheck_game_running=false\ninstall_option=2\n");

        IniSettings first = LoadAndMigrate(temp);
        Assert.False(first.GetBool("settings", "check_game_running", fallback: true));
        first.SetBool("settings", "check_game_running", true);

        // 旧文件里的值不会在后续启动时覆盖用户后来改过的新值。
        IniSettings second = LoadAndMigrate(temp);
        Assert.True(second.GetBool("settings", "check_game_running", fallback: false));
        Assert.Equal("2", second.Get("settings", "install_option"));
    }

    [Fact]
    public void Read_OldKeyNames_AreAliased()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", """
            [settings]
            check_pak_inner=true
            install_optn=1
            """);

        IniSettings settings = new(path);

        Assert.True(settings.GetBool("settings", "check_pak_files"));
        Assert.Equal("1", settings.Get("settings", "install_option"));
        Assert.True(settings.Contains("settings", "check_pak_files"));
    }

    [Fact]
    public void Read_CanonicalKeyWinsOverLegacyAlias()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", """
            [settings]
            check_pak_files=false
            check_pak_inner=true
            """);

        IniSettings settings = new(path);

        Assert.False(settings.GetBool("settings", "check_pak_files", fallback: true));
    }

    [Fact]
    public void Remove_DeletesKeyAndPersists()
    {
        using TempDir temp = TempDir.Create();
        string path = NewIniPath(temp);
        IniSettings settings = new(path);
        settings.Set("settings", "file_list", "C:\\list.txt");
        settings.Set("settings", "game", "mhws");

        settings.Remove("settings", "file_list");

        Assert.Equal("", settings.Get("settings", "file_list"));
        IniSettings reloaded = new(path);
        Assert.Equal("", reloaded.Get("settings", "file_list"));
        Assert.Equal("mhws", reloaded.Get("settings", "game"));
        Assert.DoesNotContain("file_list", File.ReadAllText(path));
    }

    [Fact]
    public void Remove_MissingKey_DoesNotCreateFile()
    {
        using TempDir temp = TempDir.Create();
        string path = NewIniPath(temp);
        IniSettings settings = new(path);

        settings.Remove("settings", "file_list");

        Assert.False(File.Exists(path));
    }
}
