using System.Globalization;
using HunterBox.Application.Install;
using HunterBox.Domain.Games;

namespace HunterBox.Application.Settings;

/// <summary>
/// 设置读写协调器：把「游戏设置」涉及的键名、默认值、批量保存与旧键清理收敛到 Application，
/// 对话框只负责控件与 <see cref="GameSettingsSnapshot"/> 的转换。
/// </summary>
/// <remarks>
/// <para>
/// 所有写入合并为一次 <see cref="ISettings.UpdateBatch"/>；中途失败时已应用的修改可能保留，调用方需重新读取；
/// 只读降级等语义完全由 <see cref="ISettings"/> 实现负责，本类不重复判断。
/// </para>
/// <para>
/// <b>路径键唯一写入规则：</b>游戏/存档路径键只允许经本类的
/// <see cref="Save"/>（设置对话框批量保存）、<see cref="SetSavePath"/>（用户手动选择存档目录）与
/// <see cref="GameDiscoveryService.EnsureDetected"/>（仅补缺失值）写入；
/// App 层不得直接调用 <see cref="ISettings.Set"/> 写路径键（LayerSourceGuardTests 门禁）。
/// </para>
/// </remarks>
public sealed class SettingsCoordinator
{
    private readonly ISettings _settings;

    /// <summary>装配协调器。</summary>
    /// <param name="settings">设置端口。</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> 为 <see langword="null"/>。</exception>
    public SettingsCoordinator(ISettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>配置是否处于只读降级（对话框据此禁用保存并提示原因）。</summary>
    public bool IsReadOnly => _settings.IsReadOnly;

    /// <summary>只读降级的用户可读提示；正常加载时为 <see langword="null"/>。</summary>
    public string? LoadWarning => _settings.LoadWarning;

    /// <summary>解析上次选择的游戏；未知/缺省时回退到荒野。</summary>
    /// <returns>当前游戏配置。</returns>
    public GameProfile ResolveCurrentGame()
    {
        string id = _settings.Get(SettingsKeys.Section, SettingsKeys.Game, GameCatalog.Wilds.Id);
        return GameCatalog.Find(id) ?? GameCatalog.Wilds;
    }

    /// <summary>读取指定游戏的游戏目录（键名只在协调器内定义）。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>游戏目录；未配置时为空串。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public string ResolveGamePath(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return ResolveGamePath(game.Id);
    }

    /// <summary>读取指定游戏的游戏目录（按稳定 ID）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>游戏目录；未配置时为空串。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public string ResolveGamePath(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return _settings.Get(gameId, SettingsKeys.GamePath);
    }

    /// <summary>读取指定游戏的存档目录。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>存档目录；未配置时为空串。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public string ResolveSavePath(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return ResolveSavePath(game.Id);
    }

    /// <summary>读取指定游戏的存档目录（按稳定 ID）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <returns>存档目录；未配置时为空串。</returns>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public string ResolveSavePath(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        return _settings.Get(gameId, SettingsKeys.SavePath);
    }

    /// <summary>pak 编号命名策略（界面只读取协调器暴露的开关，不直接读键名）。</summary>
    public bool FixPakNumber =>
        _settings.GetBool(SettingsKeys.Section, SettingsKeys.FixPakNumber, SettingsDefaults.FixPakNumber);

    /// <summary>旧数据是否已完成一次性导入（界面据此决定是否触引导入流程）。</summary>
    public bool IsLegacyImported =>
        _settings.GetBool(SettingsKeys.Section, SettingsKeys.LegacyImported, SettingsDefaults.LegacyImported);

    /// <summary>记住当前游戏（键名只在协调器内定义）。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <exception cref="ArgumentException"><paramref name="gameId"/> 为空。</exception>
    public void SetCurrentGame(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        _settings.Set(SettingsKeys.Section, SettingsKeys.Game, gameId);
    }

    /// <summary>写入某游戏的存档目录。</summary>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="savePath">存档目录绝对路径。</param>
    /// <exception cref="ArgumentException">任一参数为空。</exception>
    public void SetSavePath(string gameId, string savePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        _settings.Set(gameId, SettingsKeys.SavePath, savePath);
    }

    /// <summary>标记旧数据已完成一次性导入。</summary>
    public void MarkLegacyImported() =>
        _settings.SetBool(SettingsKeys.Section, SettingsKeys.LegacyImported, true);

    /// <summary>读取指定游戏的设置快照。</summary>
    /// <param name="game">目标游戏（游戏目录/存档目录按游戏 ID 分节存放）。</param>
    /// <returns>设置快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public GameSettingsSnapshot Read(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);

        return new GameSettingsSnapshot(
            _settings.Get(game.Id, SettingsKeys.GamePath),
            _settings.Get(game.Id, SettingsKeys.SavePath),
            InstallSourceSettings.Parse(
                _settings.Get(
                    InstallSourceSettings.Section,
                    InstallSourceSettings.Key,
                    ((int)InstallSourceMode.None).ToString(CultureInfo.InvariantCulture))),
            _settings.GetBool(SettingsKeys.Section, SettingsKeys.FixPakNumber, SettingsDefaults.FixPakNumber),
            _settings.GetBool(SettingsKeys.Section, SettingsKeys.CheckPakFiles, SettingsDefaults.CheckPakFiles),
            _settings.GetBool(SettingsKeys.Section, SettingsKeys.CheckGameRunning, SettingsDefaults.CheckGameRunning),
            _settings.Get(SettingsKeys.Section, SettingsKeys.Theme, SettingsDefaults.Theme));
    }

    /// <summary>把快照写回配置（一次批量落盘，并清理已删除功能的旧键）。</summary>
    /// <param name="game">目标游戏。</param>
    /// <param name="snapshot">要保存的设置快照。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public void Save(GameProfile game, GameSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(snapshot);

        _settings.UpdateBatch(settings =>
        {
            settings.Set(game.Id, SettingsKeys.GamePath, snapshot.GamePath);
            settings.Set(game.Id, SettingsKeys.SavePath, snapshot.SavePath);
            settings.Set(
                InstallSourceSettings.Section,
                InstallSourceSettings.Key,
                ((int)snapshot.InstallOption).ToString(CultureInfo.InvariantCulture));
            settings.SetBool(SettingsKeys.Section, SettingsKeys.FixPakNumber, snapshot.FixPakNumber);
            settings.SetBool(SettingsKeys.Section, SettingsKeys.CheckPakFiles, snapshot.CheckPakFiles);
            settings.SetBool(SettingsKeys.Section, SettingsKeys.CheckGameRunning, snapshot.CheckGameRunning);
            settings.Set(SettingsKeys.Section, SettingsKeys.Theme, snapshot.Theme);

            // 清理死设置与旧键（file.list UI 已删除；旧键名统一到新键）。
            settings.Remove(SettingsKeys.Section, "file_list");
            settings.Remove(SettingsKeys.Section, SettingsKeys.LegacyCheckPakInner);
            settings.Remove(InstallSourceSettings.Section, "install_optn");
            settings.Remove(SettingsKeys.Section, "ignore_caimogu");
            settings.Remove(SettingsKeys.Section, "enable_filter_mhmodel");
        });
    }
}
