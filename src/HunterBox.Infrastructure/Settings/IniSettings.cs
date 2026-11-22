using HunterBox.Application.Common;
using HunterBox.Application.Settings;
using System.Runtime.ExceptionServices;
using System.Text;
using HunterBox.Infrastructure.Logging;

using HunterBox.Infrastructure.IO;
namespace HunterBox.Infrastructure.Settings;

/// <summary>
/// 极简 INI 配置读写（<c>[settings]</c> / <c>[&lt;gameId&gt;]</c> 两段够用即可）。
/// </summary>
/// <remarks>
/// <para>
/// 只在 UI 层组合根使用。保存采用「先写 .tmp 再原子替换」的方式，失败时抛 <see cref="IOException"/>
/// 以便界面提示用户（不会留下半截 INI 文件覆盖原配置）。
/// </para>
/// <para>
/// 读取失败（文件被占用、无权限等）时进入 <see cref="IsReadOnly"/> 降级：内存按默认值工作、
/// 原文件原样保留，<see cref="Set"/> / <see cref="Remove"/> 只改内存不写盘（也不抛异常，
/// 避免界面大量没有 try/catch 的调用点崩溃），并通过 <see cref="LoadWarning"/> 给出可读提示。
/// </para>
/// <para>
/// 本类只负责 INI 读写与旧键名读取兼容；旧 <c>sjmodbox.ini</c> 到 <c>hunterbox.ini</c>
/// 的一次性数据迁移由 <c>LegacyMigrationService.MigrateSettings</c> 负责（A-13）。
/// </para>
/// </remarks>
public sealed class IniSettings : ISettings
{
    /// <summary>同一语义的键（新键在前）；旧键只做读取兼容，不再写出。</summary>
    private static readonly string[][] KeyAliases =
    [
        [SettingsKeys.CheckPakFiles, SettingsKeys.LegacyCheckPakInner],
        ["install_option", "install_optn"],
    ];

    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>串行化读写与落盘：后台探测与 UI 设置可能并发保存（C-17）。</summary>
    private readonly object _gate = new();

    /// <summary>加载失败的可读提示；正常加载时为 <see langword="null"/>。</summary>
    private string? _loadWarning;

    /// <summary>只读降级后是否已提示过「修改不会写盘」，避免每次 Set 都刷日志。</summary>
    private bool _writeIgnoredLogged;

    /// <summary>批量更新嵌套深度；大于 0 时 Set/Remove 只改内存，结束时统一落盘一次。</summary>
    private int _batchDepth;

    /// <summary>
    /// 是否处于只读降级（配置文件存在但读取失败）：为 <see langword="true"/> 时
    /// <see cref="Set"/> / <see cref="Remove"/> 只改内存、不写磁盘，原文件保持不变。
    /// </summary>
    public bool IsReadOnly => _loadWarning is not null;

    /// <summary>
    /// 加载失败的用户可读提示（含文件路径与失败原因），便于界面提示用户「本次修改不会保存」；
    /// 正常加载时为 <see langword="null"/>。
    /// </summary>
    public string? LoadWarning => _loadWarning;

    /// <summary>加载（或创建）配置文件。</summary>
    /// <param name="path">INI 文件绝对路径。</param>
    /// <param name="logger">日志端口；为 <see langword="null"/> 时使用进程级文件日志（A-23）。</param>
    public IniSettings(string path, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger ?? AppLogAdapter.Instance;
        Load();
    }

    /// <summary>读取字符串配置（自动兼容旧键名）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="fallback">缺省值。</param>
    /// <returns>配置值或 <paramref name="fallback"/>。</returns>
    public string Get(string section, string key, string fallback = "")
    {
        lock (_gate)
        {
            foreach (string candidate in KeyCandidates(key))
            {
                if (_sections.TryGetValue(section, out Dictionary<string, string>? values)
                    && values.TryGetValue(candidate, out string? value)
                    && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return fallback;
        }
    }

    /// <summary>读取布尔配置（<c>true</c>/<c>1</c> 视为真）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="fallback">缺省值。</param>
    /// <returns>布尔值。</returns>
    public bool GetBool(string section, string key, bool fallback = false)
    {
        string raw = Get(section, key, fallback ? "true" : "false");
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    /// <summary>判断某个键是否存在非空值（自动兼容旧键名）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <returns>存在非空值时返回 <see langword="true"/>。</returns>
    public bool Contains(string section, string key) => Get(section, key).Length > 0;

    /// <summary>写入并立即落盘。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="value">值；<see langword="null"/> 视为空串。</param>
    /// <remarks>
    /// 只读降级（<see cref="IsReadOnly"/>）期间只改内存、不写磁盘，也不抛异常：
    /// 否则启动流程/设置页大量没有 try/catch 的调用点会直接崩溃，而且会把读不出来的用户原配置覆盖成空文件。
    /// </remarks>
    public void Set(string section, string key, string? value)
    {
        lock (_gate)
        {
            SetInMemory(section, key, value);
            if (IsReadOnly)
            {
                WarnWriteIgnored();
                return;
            }

            SaveIfNeeded();
        }
    }

    /// <summary>写入布尔值。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <param name="value">布尔值。</param>
    public void SetBool(string section, string key, bool value) => Set(section, key, value ? "true" : "false");

    /// <summary>删除一个键（不存在时为空操作，不重写文件）。</summary>
    /// <param name="section">段名。</param>
    /// <param name="key">键名。</param>
    /// <remarks>只读降级期间只改内存、不写磁盘，也不抛异常。</remarks>
    public void Remove(string section, string key)
    {
        lock (_gate)
        {
            if (!_sections.TryGetValue(section, out Dictionary<string, string>? values) || !values.Remove(key))
            {
                return;
            }

            if (IsReadOnly)
            {
                WarnWriteIgnored();
                return;
            }

            SaveIfNeeded();
        }
    }

    /// <summary>
    /// 在内存中批量执行多次 Set/Remove，结束后只落盘一次；中途异常也会尝试落盘已有修改。
    /// </summary>
    /// <param name="update">批量修改委托。</param>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> 为 <see langword="null"/>。</exception>
    /// <remarks>只读降级期间仍只改内存、不写盘。</remarks>
    public void UpdateBatch(Action<ISettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            // 批量修改先在内存生效；落盘失败时恢复到批量前，避免「提示保存失败、后续 Set 却把半套新值写盘」。
            Dictionary<string, Dictionary<string, string>> snapshot = SnapshotSections();
            _batchDepth++;
            Exception? updateError = null;
            Exception? saveError = null;
            try
            {
                update(this);
            }
            catch (Exception ex)
            {
                // 先暂存更新异常，等落盘尝试结束后统一重抛，避免在 finally 中抛异常（CA2219）。
                updateError = ex;
            }
            finally
            {
                _batchDepth--;
                if (_batchDepth == 0 && !IsReadOnly)
                {
                    try
                    {
                        Save();
                    }
                    catch (Exception ex)
                    {
                        RestoreSections(snapshot);
                        saveError = ex;
                    }
                }
            }

            if (updateError is not null)
            {
                if (saveError is not null)
                {
                    // 保存失败不能覆盖批量更新本身的异常：把原异常保留在 InnerException（C-23）。
                    throw new IOException(
                        "保存设置失败，且批量更新本身也失败",
                        new AggregateException(updateError, saveError));
                }

                ExceptionDispatchInfo.Capture(updateError).Throw();
                return;
            }

            if (saveError is not null)
            {
                ExceptionDispatchInfo.Capture(saveError).Throw();
            }
        }
    }

    /// <summary>克隆当前配置字典（批量更新失败回滚用）。</summary>
    private Dictionary<string, Dictionary<string, string>> SnapshotSections() =>
        _sections.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>把配置内存恢复到快照状态。</summary>
    private void RestoreSections(Dictionary<string, Dictionary<string, string>> snapshot)
    {
        _sections.Clear();
        foreach ((string section, Dictionary<string, string> values) in snapshot)
        {
            _sections[section] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveIfNeeded()
    {
        if (_batchDepth == 0)
        {
            Save();
        }
    }

    private void SetInMemory(string section, string key, string? value)
    {
        if (!_sections.TryGetValue(section, out Dictionary<string, string>? values))
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[section] = values;
        }

        values[key] = value ?? string.Empty;
    }

    private static IEnumerable<string> KeyCandidates(string key)
    {
        yield return key;
        foreach (string[] group in KeyAliases)
        {
            bool hit = group.Any(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!hit)
            {
                continue;
            }

            foreach (string candidate in group)
            {
                if (!candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }
    }

    private void Load()
    {
        string[]? lines = null;
        // 瞬时共享冲突（杀软/其它进程短暂占用）重试 1～2 次，持续失败才进入只读降级。
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                // 不先用 File.Exists 判断：ACL 拒绝读取时 File.Exists 返回 false，
                // 会被误当成「文件不存在」而绕过只读降级（N-11）。
                lines = File.ReadAllLines(_path);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EnterReadOnly(ex);
                return;
            }
        }

        if (lines is null)
        {
            EnterReadOnly(new IOException("多次重试后仍无法读取配置文件"));
            return;
        }

        // 先解析到局部字典：中途失败时整体丢弃，绝不把「读到一半的配置」当成完整配置。
        Dictionary<string, Dictionary<string, string>> parsed = new(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                parsed.TryAdd(current, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (current is null || separator <= 0)
            {
                continue;
            }

            parsed[current][line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        _sections.Clear();
        foreach ((string section, Dictionary<string, string> values) in parsed)
        {
            _sections[section] = values;
        }
    }

    /// <summary>进入只读降级：内存按默认值工作，但禁止回写，避免覆盖读不出来的用户原配置。</summary>
    /// <param name="ex">读取失败原因。</param>
    private void EnterReadOnly(Exception ex)
    {
        _sections.Clear();
        _loadWarning =
            $"读取配置文件（{_path}）失败，已进入只读模式以保护原文件，本次会话的修改不会保存：{ex.Message}"
            + "。请关闭占用该文件的程序或修复文件权限后重启。";
        _logger.Error($"读取配置文件失败，已进入只读模式：{_path}", ex);
    }

    /// <summary>只读降级后提示一次「修改不会写盘」，避免高频 Set 刷日志。</summary>
    private void WarnWriteIgnored()
    {
        if (_writeIgnoredLogged)
        {
            return;
        }

        _writeIgnoredLogged = true;
        _logger.Warn($"配置文件处于只读降级状态，本次会话的修改不会写入磁盘：{_path}（{_loadWarning}）");
    }

    /// <summary>把当前配置原子写回磁盘。</summary>
    /// <exception cref="IOException">写入失败（目录不可写、文件被占用、没有权限等），消息含目标路径。</exception>
    private void Save()
    {
        if (IsReadOnly)
        {
            // 双保险：只读降级期间任何内部路径都不得回写，避免覆盖读不出来的用户原配置。
            WarnWriteIgnored();
            return;
        }

        StringBuilder builder = new();
        foreach ((string section, Dictionary<string, string> values) in _sections)
        {
            builder.Append('[').Append(section).Append(']').AppendLine();
            foreach ((string key, string value) in values)
            {
                builder.Append(key).Append('=').Append(value).AppendLine();
            }

            builder.AppendLine();
        }

        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temp, builder.ToString());

            // 先写临时文件再原子替换：中途失败时旧配置仍然完整可用。
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SystemFileSystem.Instance.TryDeleteFile(temp);
            throw new IOException($"保存设置失败: {_path}", ex);
        }
    }

}
