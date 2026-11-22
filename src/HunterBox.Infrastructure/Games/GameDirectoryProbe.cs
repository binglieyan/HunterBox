using HunterBox.Domain.Games;

namespace HunterBox.Infrastructure.Games;

/// <summary>游戏目录校验（含主程序检查）；把文件系统探测从 Domain 的 <see cref="GameProfile"/> 移出。</summary>
public static class GameDirectoryProbe
{
    /// <summary>在指定目录里查找游戏主程序。</summary>
    /// <param name="game">游戏配置。</param>
    /// <param name="directory">候选游戏根目录。</param>
    /// <returns>主程序绝对路径；目录为空、不存在、未配置主程序或找不到主程序时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public static string? FindExecutable(GameProfile game, string? directory)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(directory) || !game.HasExecutableName)
        {
            return null;
        }

        string path = Path.Combine(directory, game.ExecutableName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>判断目录是否为该游戏的有效安装目录（含主程序；未配置主程序时退化为目录存在）。</summary>
    /// <param name="game">游戏配置。</param>
    /// <param name="directory">候选游戏根目录。</param>
    /// <returns>目录存在且能找到主程序（或该游戏没有主程序配置）时为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="game"/> 为 <see langword="null"/>。</exception>
    public static bool IsGameDirectory(GameProfile game, string? directory)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        return !game.HasExecutableName || FindExecutable(game, directory) is not null;
    }
}
