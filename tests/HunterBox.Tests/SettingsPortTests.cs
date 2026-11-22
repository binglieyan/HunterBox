using HunterBox.Application.Settings;
using HunterBox.Infrastructure.Settings;

namespace HunterBox.Tests;

/// <summary>
/// ISettings 端口契约：真实 INI 实现可经接口使用；消费者可用内存假实现不碰磁盘地打桩只读/写入行为。
/// </summary>
/// <remarks>
/// 测试项目不引用 App，无法直接单测 <c>MainWindow</c>/<c>SettingsDialog</c> 等 App 消费者；
/// 这里用「接口 + 假实现」验证端口本身可替换，并固定 App 消费者依赖的成员形状。
/// 消费者真正读写的键名/默认值仍由 App 层人工评审与手工启动验证覆盖。
/// </remarks>
public sealed class SettingsPortTests
{
    [Fact]
    public void IniSettings_ThroughPort_PersistsSingleAndBatchWrites()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "hunterbox.ini");
        ISettings settings = new IniSettings(path);

        SaveLikeConsumer(settings);

        Assert.False(settings.IsReadOnly);
        Assert.Null(settings.LoadWarning);
        Assert.True(settings.GetBool("settings", "check_pak_files"));
        Assert.Equal("mhws", settings.Get("settings", "game"));
        Assert.Equal("", settings.Get("settings", "removed_key"));

        // 磁盘格式与行为不变：重新构造真实实现仍能读到批量写入的值。
        IniSettings reloaded = new(path);
        Assert.Equal("mhws", reloaded.Get("settings", "game"));
        Assert.True(reloaded.GetBool("settings", "check_pak_files"));
        Assert.Equal("", reloaded.Get("settings", "removed_key"));
    }

    [Fact]
    public void IniSettings_ReadOnlyThroughPort_ExposesWarningAndSkipsWrites()
    {
        using TempDir temp = TempDir.Create();
        string original = "[settings]\r\ngame=mhws\r\n";
        string path = temp.File("hunterbox.ini", original);
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
        ISettings settings = new IniSettings(path);

        Assert.True(settings.IsReadOnly);
        Assert.NotNull(settings.LoadWarning);

        int applied = SaveLikeConsumer(settings);

        locked.Dispose();
        Assert.Equal(0, applied);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FakeSettings_StubsWritePathWithoutTouchingDisk()
    {
        using TempDir temp = TempDir.Create();
        string wouldBeIni = Path.Combine(temp.Path, "hunterbox.ini");
        FakeSettings settings = new();

        int applied = SaveLikeConsumer(settings);

        Assert.Equal(1, applied);
        Assert.Equal(1, settings.SaveCount);
        Assert.True(settings.GetBool("settings", "check_pak_files"));
        Assert.Equal("mhws", settings.Get("settings", "game"));
        Assert.False(File.Exists(wouldBeIni), "假实现不应在磁盘上创建配置文件");
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void FakeSettings_StubsReadOnlyBranchWithoutTouchingDisk()
    {
        using TempDir temp = TempDir.Create();
        FakeSettings settings = new() { IsReadOnly = true, LoadWarning = "配置文件被占用" };

        int applied = SaveLikeConsumer(settings);

        Assert.Equal(0, applied);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal("", settings.Get("settings", "game"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    /// <summary>模拟 App 消费者（如设置页保存）：只读降级时按契约跳过写入，否则批量写一次。</summary>
    /// <param name="settings">设置端口；真实实现或假实现均可。</param>
    /// <returns>实际提交的写入批次数量（只读降级或异常时为 0）。</returns>
    private static int SaveLikeConsumer(ISettings settings)
    {
        if (settings.IsReadOnly)
        {
            return 0;
        }

        int applied = 0;
        settings.UpdateBatch(port =>
        {
            port.Set("settings", "game", "mhws");
            port.SetBool("settings", "check_pak_files", true);
            port.Remove("settings", "removed_key");
            applied++;
        });
        return applied;
    }
}

/// <summary>
/// 测试用内存设置实现：不碰磁盘，可打桩只读降级；写入语义与 <see cref="ISettings"/> 契约一致
/// （只读时只改内存、不记落盘次数）。
/// </summary>
internal sealed class FakeSettings : ISettings
{
    private readonly Dictionary<(string Section, string Key), string> _values = [];
    private int _batchDepth;

    /// <inheritdoc />
    public bool IsReadOnly { get; set; }

    /// <inheritdoc />
    public string? LoadWarning { get; set; }

    /// <summary>落盘次数（只读降级期间不增加），用于验证消费者没有多余写盘。</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public string Get(string section, string key, string fallback = "") =>
        _values.TryGetValue((section, key), out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    /// <inheritdoc />
    public bool GetBool(string section, string key, bool fallback = false)
    {
        string raw = Get(section, key, fallback ? "true" : "false");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    /// <inheritdoc />
    public void Set(string section, string key, string? value)
    {
        _values[(section, key)] = value ?? string.Empty;
        SaveIfNeeded();
    }

    /// <inheritdoc />
    public void SetBool(string section, string key, bool value) => Set(section, key, value ? "true" : "false");

    /// <inheritdoc />
    public void Remove(string section, string key)
    {
        if (_values.Remove((section, key)))
        {
            SaveIfNeeded();
        }
    }

    /// <inheritdoc />
    public void UpdateBatch(Action<ISettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _batchDepth++;
        try
        {
            update(this);
        }
        finally
        {
            _batchDepth--;
            if (_batchDepth == 0)
            {
                SaveIfNeeded();
            }
        }
    }

    private void SaveIfNeeded()
    {
        if (_batchDepth == 0 && !IsReadOnly)
        {
            SaveCount++;
        }
    }
}
