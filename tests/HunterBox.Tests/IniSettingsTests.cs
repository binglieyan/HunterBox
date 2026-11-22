using HunterBox.Infrastructure.Settings;

namespace HunterBox.Tests;

/// <summary>INI 读写容错与原子落盘：写失败必须可诊断，读失败降级默认值。</summary>
public class IniSettingsTests
{
    [Fact]
    public void SaveAndReload_RoundTripsSectionsAndKeys()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "hunterbox.ini");
        IniSettings settings = new(path);
        settings.Set("settings", "game", "mhws");
        settings.SetBool("mhws", "check_game_running", true);

        IniSettings reloaded = new(path);

        Assert.Equal("mhws", reloaded.Get("settings", "game"));
        Assert.True(reloaded.GetBool("mhws", "check_game_running"));
        Assert.Equal("", reloaded.Get("settings", "missing"));
        Assert.Equal("fallback", reloaded.Get("settings", "missing", "fallback"));
        Assert.False(File.Exists(path + ".tmp"), "原子写的临时文件不应残留");
    }

    [Fact]
    public void Load_CorruptContent_FallsBackToDefaultsWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", "[broken\n=no-key\nkey-without-value\n[settings]\ngame=mhws");
        IniSettings settings = new(path);

        Assert.Equal("mhws", settings.Get("settings", "game"));
        Assert.Equal("", settings.Get("broken", "whatever"));
    }

    [Fact]
    public void Load_LockedFile_FallsBackToDefaultsWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", "[settings]\ngame=mhws");
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        IniSettings settings = new(path);

        Assert.Equal("", settings.Get("settings", "game"));
    }

    /// <summary>N-11：ACL 拒绝读取时 File.Exists 会返回 false；不能因此把配置当成不存在而绕过只读降级。</summary>
    [Fact]
    public void Load_PathIsDirectory_EntersReadOnlyInsteadOfTreatingAsMissing()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.Dir("hunterbox.ini");

        IniSettings settings = new(path);

        Assert.True(settings.IsReadOnly);
        Assert.NotNull(settings.LoadWarning);
        // 只读降级期间写入只改内存、不抛异常、不碰磁盘。
        settings.Set("settings", "game", "mhws");
        Assert.Equal("mhws", settings.Get("settings", "game"));
    }

    /// <summary>N-14：批量更新落盘失败时，内存必须恢复到批量前，不能留下半套新值。</summary>
    [Fact]
    public void UpdateBatch_SaveFailure_RollsBackMemory()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", "[settings]\ngame=old");
        IniSettings settings = new(path);
        Assert.Equal("old", settings.Get("settings", "game"));

        // 用同名目录占住目标文件，让原子替换失败；此时内存里已经写入了新值。
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.Throws<IOException>(() => settings.UpdateBatch(s =>
        {
            s.Set("settings", "game", "new");
            s.Set("settings", "theme", "dark");
        }));

        Assert.Equal("old", settings.Get("settings", "game"));
        Assert.Equal("", settings.Get("settings", "theme"));
    }

    [Fact]
    public void Save_WhenTargetDirectoryIsAFile_ThrowsDiagnosticIOException()
    {
        using TempDir temp = TempDir.Create();
        string blocker = temp.File("blocker", "not-a-directory");
        string path = Path.Combine(blocker, "hunterbox.ini");
        IniSettings settings = new(path);

        IOException ex = Assert.Throws<IOException>(() => settings.Set("settings", "game", "mhws"));

        Assert.Contains("保存设置失败", ex.Message);
        Assert.False(File.Exists(path));
    }
}
