using HunterBox.Domain.Games;

namespace HunterBox.Application.Settings;

/// <summary>
/// 当前游戏会话：集中保存「当前游戏 + 游戏根目录」并负责从设置读取/写回（A-06）。
/// </summary>
/// <remarks>
/// <para>
/// 从主窗口抽出的会话状态：窗口只绑定 <see cref="Game"/> / <see cref="GameRoot"/>，
/// 不再各自维护两份字段与「读设置 → 探测 → 写回」的重复分支。
/// </para>
/// <para>
/// 不依赖 UI：初始游戏与回退规则统一由 <see cref="SettingsCoordinator.ResolveCurrentGame"/> 决定；
/// 本类只读取设置，游戏目录的写回统一由 <see cref="GameDiscoveryService.EnsureDetected"/> 负责。
/// </para>
/// </remarks>
public sealed class GameSession
{
    private readonly SettingsCoordinator _settings;

    /// <summary>从设置读取初始游戏与游戏目录。</summary>
    /// <param name="settings">设置读写协调器。</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> 为 <see langword="null"/>。</exception>
    public GameSession(SettingsCoordinator settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        Game = settings.ResolveCurrentGame();
        GameRoot = settings.ResolveGamePath(Game);
    }

    /// <summary>当前游戏。</summary>
    public GameProfile Game { get; private set; }

    /// <summary>当前游戏根目录；未配置时为空白字符串。</summary>
    public string GameRoot { get; private set; }

    /// <summary>切换当前游戏并重新读取该游戏的根目录。</summary>
    /// <param name="game">目标游戏。</param>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public void Select(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        Game = game;
        ReloadRoot();
    }

    /// <summary>重新从设置读取当前游戏的根目录（设置对话框保存后调用）。</summary>
    public void ReloadRoot() => GameRoot = _settings.ResolveGamePath(Game);
}
