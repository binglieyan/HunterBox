using HunterBox.Domain.Games;

namespace HunterBox.Application.Settings;

/// <summary>
/// 游戏安装目录/存档目录的平台探测端口（A-02）：Steam 注册表与 userdata 的具体读取在 Infrastructure。
/// </summary>
public interface IGameInstallProbe
{
    /// <summary>尝试定位游戏安装目录。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>找到的目录；找不到或平台不支持时返回 <see langword="null"/>。</returns>
    string? FindGameRoot(GameProfile game);

    /// <summary>枚举游戏存档目录候选（多账号时可能多于一个；歧义策略由 Application 决定）。</summary>
    /// <param name="game">目标游戏。</param>
    /// <returns>候选目录；找不到或平台不支持时为空列表。</returns>
    IReadOnlyList<string> FindSaveRootCandidates(GameProfile game);

    /// <summary>丢弃某个游戏的探测缓存，让下一次查找重新读取注册表/目录。</summary>
    /// <remarks>
    /// 按缓存项分别失效：设置页只对缺失的路径调用，已探测到的目录不重复读取；
    /// 未实现缓存的探测可忽略。
    /// </remarks>
    /// <param name="gameId">游戏稳定 ID。</param>
    /// <param name="gameRoot">是否丢弃安装目录缓存（含未找到负缓存）。</param>
    /// <param name="saveRoots">是否丢弃存档候选缓存（含空结果负缓存）。</param>
    void Invalidate(string gameId, bool gameRoot, bool saveRoots);
}
