using System.Globalization;

namespace HunterBox.Infrastructure.Games;

/// <summary>从 Steam <c>userdata</c> 目录枚举游戏的默认存档目录候选。</summary>
/// <remarks>
/// Steam 存档目录探测：<c>&lt;Steam&gt;\userdata\&lt;账号&gt;\&lt;appid&gt;\remote</c>。
/// 账号目录里没有 <c>remote</c> 但存在 appid 目录时，退回该 appid 目录本身（部分游戏不用 remote 子目录）。
/// 本类只负责枚举候选；多账号歧义策略由 Application 的 <see cref="HunterBox.Application.Saves.SavePathSelection"/> 决定。
/// </remarks>
public static class SteamSavePathLocator
{
    /// <summary>在 Steam 的 userdata 里枚举某游戏的存档目录候选（按账号目录排序）。</summary>
    /// <param name="steamRoot">Steam 根目录；未知时为 <see langword="null"/>。</param>
    /// <param name="appId">Steam AppId。</param>
    /// <returns>候选目录绝对路径；找不到时为空列表。</returns>
    public static IReadOnlyList<string> FindCandidates(string? steamRoot, int appId)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || appId <= 0)
        {
            return [];
        }

        string root = steamRoot.Trim().Replace('/', '\\');
        string userdata = Path.Combine(root, "userdata");
        if (!Directory.Exists(userdata))
        {
            return [];
        }

        string[] accounts;
        try
        {
            accounts = [.. Directory.GetDirectories(userdata).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        List<string> candidates = [];
        foreach (string account in accounts)
        {
            string appDirectory = Path.Combine(account, appId.ToString(CultureInfo.InvariantCulture));
            if (!Directory.Exists(appDirectory))
            {
                continue;
            }

            string remote = Path.Combine(appDirectory, "remote");
            candidates.Add(Directory.Exists(remote) ? remote : appDirectory);
        }

        return candidates;
    }
}
