using System.Diagnostics;
using HunterBox.Application.Activation;
using HunterBox.Application.Cleanup;
using HunterBox.Domain.Games;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.App;

/// <summary>
/// 维护命令的后台服务：卸载、清理、重新复制、启动游戏。
/// </summary>
/// <remarks>
/// 通过 App 层组合根 <see cref="AppServices"/> 装配需要磁盘布局/记录/设置的服务；
/// 游戏/游戏目录/pak 命名模式由调用方按当前状态传入。
/// 不打开对话框、不访问控件，UI 确认与结果提示仍留在 MainWindow。
/// </remarks>
internal sealed class MaintenanceWorkflow
{
    private readonly AppServices _services;

    /// <summary>装配维护后台服务。</summary>
    /// <param name="services">App 层组合根（持有HunterBox布局、MOD 记录与设置，并统一装配用例）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> 为 <see langword="null"/>。</exception>
    public MaintenanceWorkflow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>卸载 MOD：已启用时先安全禁用，再删除目录与记录。</summary>
    /// <param name="mod">目标 MOD。</param>
    /// <param name="game">当前游戏。</param>
    /// <param name="gameRoot">游戏根目录。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>是否成功删除记录/目录。</returns>
    public bool Uninstall(
        ModRecord mod,
        GameProfile game,
        string gameRoot,
        PakNamingMode pakMode,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(game);

        if (mod.Enabled)
        {
            _services.CreateActivation(game, gameRoot).Disable(mod, pakMode, token);
        }

        return _services.CreateInstaller(game).Uninstall(mod.Id);
    }

    /// <summary>清理当前游戏的 MOD 文件/残留。</summary>
    /// <param name="game">当前游戏。</param>
    /// <param name="gameRoot">游戏根目录。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="scope">清理范围。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="progress">进度回调。</param>
    /// <returns>清理结果。</returns>
    public CleanupReport Cleanup(
        GameProfile game,
        string gameRoot,
        PakNamingMode pakMode,
        CleanupScope scope,
        CancellationToken token,
        IProgress<(int Done, int Total)>? progress)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _services.CreateCleanup(game, gameRoot, pakMode).Cleanup(scope, token, progress);
    }

    /// <summary>重新复制当前游戏全部已启用 MOD。</summary>
    /// <param name="enabled">已启用 MOD。</param>
    /// <param name="game">当前游戏。</param>
    /// <param name="gameRoot">游戏根目录。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="progress">进度回调。</param>
    /// <returns>批量结果。</returns>
    public BatchActivationResult Recopy(
        IReadOnlyList<ModRecord> enabled,
        GameProfile game,
        string gameRoot,
        PakNamingMode pakMode,
        CancellationToken token,
        IProgress<(int Done, int Total)>? progress)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(game);
        return new ModBatchActivator(_services.CreateActivation(game, gameRoot)).Recopy(enabled, pakMode, token, progress);
    }

    /// <summary>通过 Steam 启动当前游戏（不负责最小化窗口）。</summary>
    /// <param name="game">当前游戏。</param>
    /// <exception cref="InvalidOperationException">没有 Steam AppId。</exception>
    public static void Launch(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        // Steam AppId 的唯一来源是 GameProfile（D-25）。
        int appId = game.SteamAppId;
        if (appId <= 0)
        {
            throw new InvalidOperationException($"{game.DisplayName} 没有 Steam AppId，无法通过 Steam 启动");
        }

        Process.Start(new ProcessStartInfo($"steam://run/{appId}") { UseShellExecute = true });
    }
}
