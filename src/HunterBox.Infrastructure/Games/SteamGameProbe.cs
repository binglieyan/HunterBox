using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using HunterBox.Application.Settings;
using HunterBox.Domain.Games;
using Microsoft.Win32;

namespace HunterBox.Infrastructure.Games;

/// <summary>
/// 通过 Steam 注册表与 userdata 定位游戏安装目录/存档目录（A-02：平台探测收在 Infrastructure）。
/// </summary>
/// <remarks>
/// 探测缓存是实例字段（A-19）：随组合根创建一次，进程退出即释放，不再跨实例共享静态缓存。
/// 所有探测都尽力而为：读不到注册表/文件时返回 <see langword="null"/>，不抛异常。
/// </remarks>
public sealed class SteamGameProbe : IGameInstallProbe
{
    /// <summary>HKCU 下 Steam 的注册表根。</summary>
    private const string SteamRegistryPath = @"Software\Valve\Steam";

    /// <summary>HKCU SteamPath 值名。</summary>
    private const string SteamPathValue = "SteamPath";

    /// <summary>HKCU 下「应用是否安装」的注册表根。</summary>
    private const string SteamAppsRegistryPath = @"Software\Valve\Steam\Apps";

    /// <summary>HKLM 下 Steam 安装位置的注册表路径。</summary>
    private const string SteamInstallRegistryPath = @"SOFTWARE\Valve\Steam";

    /// <summary>HKLM Uninstall 下 Steam 游戏项的前缀（后接 appid）。</summary>
    private const string SteamUninstallPath = @"Microsoft\Windows\CurrentVersion\Uninstall\Steam App ";

    /// <summary>本次会话已识别到的游戏目录：切换游戏时直接复用，不重复读注册表/VDF（P-24）。</summary>
    private readonly ConcurrentDictionary<string, string> _gameRoots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次会话已枚举到的存档候选根：同一游戏第二次探测不再枚举 userdata。</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _saveRoots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>未找到游戏目录的负缓存时间戳：短时间内重复打开设置不重复探测。</summary>
    private readonly ConcurrentDictionary<string, long> _gameRootMisses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>存档候选为空时的短时负缓存：反复刷新设置不重复枚举 userdata。</summary>
    private readonly ConcurrentDictionary<string, long> _saveRootMisses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>负缓存有效期：游戏可能在会话内安装，过期后重新探测。</summary>
    private readonly TimeSpan _gameRootMissTtl;

    /// <summary>存档空结果的负缓存有效期：用户可能在会话内创建存档目录。</summary>
    private readonly TimeSpan _saveRootMissTtl;

    /// <summary>默认负缓存有效期：安装目录 2 分钟、存档候选 30 秒。</summary>
    private static readonly TimeSpan DefaultGameRootMissTtl = TimeSpan.FromMinutes(2);

    /// <summary>默认存档空结果负缓存有效期。</summary>
    private static readonly TimeSpan DefaultSaveRootMissTtl = TimeSpan.FromSeconds(30);

    /// <summary>Steam 根目录探测结果与「是否已探测」标志；<see cref="Invalidate"/> 可重置。</summary>
    private readonly object _steamRootGate = new();
    private readonly string? _fixedSteamRoot;
    private readonly bool _useRegistry;
    private string? _steamRoot;
    private bool _steamRootResolved;

    /// <summary>装配探测实例；Steam 根目录在首次使用时探测一次。</summary>
    public SteamGameProbe()
        : this(steamRoot: null, DefaultGameRootMissTtl, DefaultSaveRootMissTtl, useRegistry: true)
    {
    }

    /// <summary>用指定 Steam 根目录装配探测实例（测试用：避免读取真实注册表）。</summary>
    /// <param name="steamRoot">Steam 根目录；为 <see langword="null"/> 表示未找到。</param>
    internal SteamGameProbe(string? steamRoot)
        : this(steamRoot, DefaultGameRootMissTtl, DefaultSaveRootMissTtl, useRegistry: false)
    {
    }

    /// <summary>用指定 Steam 根目录与负缓存有效期装配探测实例（测试用：可推进 TTL 边界）。</summary>
    /// <param name="steamRoot">Steam 根目录；为 <see langword="null"/> 表示未找到。</param>
    /// <param name="gameRootMissTtl">安装目录未找到的负缓存有效期。</param>
    /// <param name="saveRootMissTtl">存档候选空结果的负缓存有效期。</param>
    internal SteamGameProbe(string? steamRoot, TimeSpan gameRootMissTtl, TimeSpan saveRootMissTtl)
        : this(steamRoot, gameRootMissTtl, saveRootMissTtl, useRegistry: false)
    {
    }

    private SteamGameProbe(string? steamRoot, TimeSpan gameRootMissTtl, TimeSpan saveRootMissTtl, bool useRegistry)
    {
        _gameRootMissTtl = gameRootMissTtl;
        _saveRootMissTtl = saveRootMissTtl;
        _fixedSteamRoot = steamRoot;
        _useRegistry = useRegistry;
    }

    /// <summary>Steam 根目录：首次使用时探测一次，<see cref="Invalidate"/> 后可重新探测。</summary>
    private string? SteamRoot
    {
        get
        {
            lock (_steamRootGate)
            {
                if (!_steamRootResolved)
                {
                    _steamRoot = _useRegistry ? ReadSteamRoot() : NormalizeDirectory(_fixedSteamRoot);
                    _steamRootResolved = true;
                    SteamRootResolutions++;
                }

                return _steamRoot;
            }
        }
    }

    /// <inheritdoc />
    public string? FindGameRoot(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!OperatingSystem.IsWindows() || game.SteamAppId <= 0)
        {
            return null;
        }

        if (_gameRoots.TryGetValue(game.Id, out string? cached))
        {
            return cached;
        }

        // 未找到的结果做短时负缓存，避免设置页反复刷新时重复读注册表/枚举目录。
        if (_gameRootMisses.TryGetValue(game.Id, out long missedAt)
            && Stopwatch.GetElapsedTime(missedAt) < _gameRootMissTtl)
        {
            return null;
        }

        SteamLocateRequest request = new()
        {
            AppId = game.SteamAppId,
            ExecutableName = game.ExecutableName,
            DefaultFolderName = game.DefaultFolderName,
            SteamRoot = SteamRoot,
            AppInstalled = ReadAppInstalled(game.SteamAppId),
            UninstallLocation = ReadUninstallLocation(game.SteamAppId),
        };
        string? detected = SteamGameLocator.Locate(request);
        if (detected is not null)
        {
            _gameRoots[game.Id] = detected;
            _gameRootMisses.TryRemove(game.Id, out _);
        }
        else
        {
            _gameRootMisses[game.Id] = Stopwatch.GetTimestamp();
        }

        return detected;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> FindSaveRootCandidates(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!OperatingSystem.IsWindows() || game.SteamAppId <= 0)
        {
            return [];
        }

        // 这里只枚举候选；多账号歧义策略由 Application 的 SavePathSelection 决定。
        // 非空候选按游戏缓存，设置对话框反复刷新不再重复枚举 userdata。
        if (_saveRoots.TryGetValue(game.Id, out IReadOnlyList<string>? cachedCandidates))
        {
            return cachedCandidates;
        }

        // 空结果做短时负缓存，反复打开设置不重复枚举，过期后自动恢复探测。
        if (_saveRootMisses.TryGetValue(game.Id, out long missedAt)
            && Stopwatch.GetElapsedTime(missedAt) < _saveRootMissTtl)
        {
            return [];
        }

        IReadOnlyList<string> candidates = SteamSavePathLocator.FindCandidates(SteamRoot, game.SteamAppId);
        if (candidates.Count > 0)
        {
            _saveRoots[game.Id] = candidates;
            _saveRootMisses.TryRemove(game.Id, out _);
        }
        else
        {
            _saveRootMisses[game.Id] = Stopwatch.GetTimestamp();
        }

        return candidates;
    }

    /// <summary>Steam 根目录已解析次数（测试/诊断用：验证失效后确实重新探测）。</summary>
    internal int SteamRootResolutions { get; private set; }

    /// <inheritdoc />
    public void Invalidate(string gameId, bool gameRoot, bool saveRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (gameRoot)
        {
            _gameRoots.TryRemove(gameId, out _);
            _gameRootMisses.TryRemove(gameId, out _);
        }

        if (saveRoots)
        {
            _saveRoots.TryRemove(gameId, out _);
            _saveRootMisses.TryRemove(gameId, out _);
        }

        // Steam 根目录探测失败会被缓存；任一失效请求都重置，便于会话内恢复。
        lock (_steamRootGate)
        {
            _steamRootResolved = false;
            _steamRoot = null;
        }
    }

    /// <summary>读取 HKCU 的 SteamPath，读不到时用 HKLM InstallPath 兜底。</summary>
    private static string? ReadSteamRoot()
    {
        string? steamPath = ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, SteamRegistryPath, SteamPathValue);
        if (Directory.Exists(steamPath))
        {
            return NormalizeDirectory(steamPath!);
        }

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? installPath = ReadRegistryString(RegistryHive.LocalMachine, view, SteamInstallRegistryPath, "InstallPath");
            if (Directory.Exists(installPath))
            {
                return NormalizeDirectory(installPath!);
            }
        }

        return null;
    }

    /// <summary>读取 HKCU <c>Apps\&lt;appid&gt;\Installed</c>；值为 0/1。</summary>
    private static bool? ReadAppInstalled(int appId)
    {
        if (appId <= 0)
        {
            return null;
        }

        object? value = ReadRegistryValue(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            $@"{SteamAppsRegistryPath}\{appId.ToString(CultureInfo.InvariantCulture)}",
            "Installed");
        string? text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取 HKLM Uninstall 里 Steam 游戏的安装目录（非标准安装兜底）。</summary>
    private static string? ReadUninstallLocation(int appId)
    {
        if (appId <= 0)
        {
            return null;
        }

        string subKey = SteamUninstallPath + appId.ToString(CultureInfo.InvariantCulture);
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? location = ReadRegistryString(RegistryHive.LocalMachine, view, subKey, "InstallLocation");
            if (!string.IsNullOrWhiteSpace(location))
            {
                return location;
            }
        }

        return null;
    }

    private static string? ReadRegistryString(RegistryHive hive, RegistryView view, string path, string valueName) =>
        ReadRegistryValue(hive, view, path, valueName) as string;

    private static object? ReadRegistryValue(RegistryHive hive, RegistryView view, string path, string valueName)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(path);
            return key?.GetValue(valueName);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            // 读不到注册表就放弃该探测来源。
            return null;
        }
    }

    private static string? NormalizeDirectory(string? path) =>
        SteamGameLocator.NormalizeDirectory(path, requireExists: false);
}
