using HunterBox.Application.Common;
using HunterBox.Application.Saves;
using HunterBox.Domain.Games;

namespace HunterBox.Application.Settings;

/// <summary>
/// 启动/切换游戏时的路径发现用例（A-02）：探测交给 <see cref="IGameInstallProbe"/>，
/// 这里只负责「设置缺省才写回」与「单款游戏失败不影响其它游戏」。
/// </summary>
public sealed class GameDiscoveryService
{
    private readonly ISettings _settings;
    private readonly IGameInstallProbe _probe;
    private readonly IAppLogger _logger;

    /// <summary>装配路径发现用例。</summary>
    /// <param name="settings">设置端口。</param>
    /// <param name="probe">平台探测端口。</param>
    /// <param name="logger">日志端口。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public GameDiscoveryService(ISettings settings, IGameInstallProbe probe, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>尝试定位游戏安装目录（只读探测，不写配置）。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>找到的目录；找不到时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public string? FindGameRoot(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _probe.FindGameRoot(game);
    }

    /// <summary>尝试定位游戏存档目录（只读探测，不写配置）。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>找到的目录；找不到时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public string? FindSaveRoot(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return SavePathSelection.SelectSingle(_probe.FindSaveRootCandidates(game));
    }

    /// <summary>丢弃某游戏的探测缓存（按安装目录/存档路径分别失效），供设置页手动刷新时立即重新探测。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="gameRoot">是否失效安装目录缓存。</param>
    /// <param name="saveRoots">是否失效存档候选缓存。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public void Invalidate(string gameId, bool gameRoot, bool saveRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (!gameRoot && !saveRoots)
        {
            return;
        }

        _probe.Invalidate(gameId, gameRoot, saveRoots);
    }

    /// <summary>某游戏缺少游戏路径/存档路径时自动探测并写回配置（已设置的值一律保留）。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>写入了新路径时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public bool EnsureDetected(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);

        bool changed = false;
        if (string.IsNullOrWhiteSpace(_settings.Get(game.Id, SettingsKeys.GamePath)))
        {
            string? detected = _probe.FindGameRoot(game);
            if (detected is not null)
            {
                _settings.Set(game.Id, SettingsKeys.GamePath, detected);
                _logger.Info($"自动识别游戏目录（{game.DisplayName}）：{detected}");
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(_settings.Get(game.Id, SettingsKeys.SavePath)))
        {
            string? savePath = SavePathSelection.SelectSingle(_probe.FindSaveRootCandidates(game));
            if (savePath is not null)
            {
                _settings.Set(game.Id, SettingsKeys.SavePath, savePath);
                _logger.Info($"自动识别存档目录（{game.DisplayName}）：{savePath}");
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>对多款游戏逐款补齐缺失的路径（启动时全量探测）。</summary>
    /// <param name="games">待探测的游戏。</param>
    /// <returns>写入了新路径的游戏数量。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="games"/> 为 <see langword="null"/>。</exception>
    public int DetectAllMissing(IEnumerable<GameProfile> games)
    {
        ArgumentNullException.ThrowIfNull(games);

        int changed = 0;
        foreach (GameProfile game in games)
        {
            try
            {
                changed += EnsureDetected(game) ? 1 : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个游戏写配置失败不影响其它游戏探测。
                _logger.Warn($"自动识别游戏目录后写配置失败（{game.DisplayName}）：{ex.Message}");
            }
        }

        return changed;
    }
}
