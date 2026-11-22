using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Infrastructure.Migration;
using HunterBox.Infrastructure.Settings;

namespace HunterBox.Tests;

/// <summary>
/// 配置读取失败后的只读降级（C-09）：读不出来时内存按默认值工作，但绝不能把用户原文件当成空配置回写覆盖。
/// </summary>
public class IniSettingsReadOnlyTests
{
    private const string OriginalIni = "[settings]\r\ngame=mhws\r\nsavepath=D:\\Saves\r\n";

    /// <summary>只记录错误的内存日志，用于验证设置端口的日志注入（A-23）。</summary>
    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Errors { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void WarnBatch(IReadOnlyList<string> messages)
        {
            // 本用例不应走批量告警；走到这里说明调用路径变了，直接失败而不是静默忽略。
            throw new InvalidOperationException("INI 只读用例不应批量记录警告");
        }

        public void Error(string message, Exception? exception = null) => Errors.Add(message);
    }

    [Fact]
    public void Load_LockedFile_EntersReadOnlyWithReadableWarning()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", OriginalIni);
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        IniSettings settings = new(path);

        Assert.True(settings.IsReadOnly);
        Assert.NotNull(settings.LoadWarning);
        Assert.Contains(path, settings.LoadWarning);
        Assert.Equal("", settings.Get("settings", "game"));
    }

    [Fact]
    public void Load_LockedFile_LogsErrorThroughInjectedLogger()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", OriginalIni);
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        RecordingLogger logger = new();

        IniSettings settings = new(path, logger);

        Assert.True(settings.IsReadOnly);
        Assert.Contains(logger.Errors, message => message.Contains("只读模式"));
    }

    [Fact]
    public void ReadOnly_SetAndRemove_DoNotOverwriteOriginalFile()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", OriginalIni);
        FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        IniSettings settings = new(path);
        Assert.True(settings.IsReadOnly);

        // App 里大量 Set/Remove 调用点没有 try/catch：只读降级期间必须不抛异常、不写文件。
        settings.Set("settings", "game", "mhr");
        settings.SetBool("settings", "check_game_running", true);
        settings.Remove("settings", "savepath");

        locked.Dispose();
        Assert.Equal(OriginalIni, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        // 本次会话内内存值仍可读（界面不会看到 Default 回退后又被覆盖的抖动）。
        Assert.Equal("mhr", settings.Get("settings", "game"));
        Assert.Equal("", settings.Get("settings", "savepath"));
    }

    [Fact]
    public void ReadOnly_MigrateLegacySettings_IsSkippedAndKeepsBothFiles()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", OriginalIni);
        string legacy = temp.File(
            "sjmodbox.ini",
            "[settings]\ngame=2246340\n[2246340]\ngamepath=C:\\Games\\MHWS\n");
        string legacyBefore = File.ReadAllText(legacy);
        FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        IniSettings settings = new(path);
        Assert.True(settings.IsReadOnly);

        // 只读降级时必须跳过迁移：否则会拿空内存配置整文件覆盖读不出来的 hunterbox.ini。
        Assert.False(LegacyMigrationService.MigrateSettings(new BoxPaths(temp.Path), settings));

        locked.Dispose();
        Assert.Equal(OriginalIni, File.ReadAllText(path));
        Assert.Equal(legacyBefore, File.ReadAllText(legacy));
    }

    [Fact]
    public void WritableLoad_ExposesNoWarningAndPersistsSets()
    {
        using TempDir temp = TempDir.Create();
        string path = temp.File("hunterbox.ini", "[settings]\ngame=mhws\n");

        IniSettings settings = new(path);

        Assert.False(settings.IsReadOnly);
        Assert.Null(settings.LoadWarning);

        settings.Set("settings", "game", "mhr");
        Assert.Equal("mhr", new IniSettings(path).Get("settings", "game"));
    }
}
