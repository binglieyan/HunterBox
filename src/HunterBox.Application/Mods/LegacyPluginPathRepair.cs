using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.Application.Mods;

/// <summary>旧版本安装前缀修复结果。</summary>
/// <param name="Changed">是否有记录被修正。</param>
/// <param name="FilesMoved">从旧错误落点移动到新落点的已部署文件数。</param>
/// <param name="Warnings">需要写入日志的修复说明/失败原因。</param>
public sealed record LegacyPluginPathRepairResult(
    bool Changed,
    int FilesMoved,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 修复旧版本自动推断安装前缀后留下的错误记录：旧逻辑会把包内文件再套一层
/// <c>nativePC</c>/<c>reframework</c> 前缀（例如把 <c>winmm.dll</c>、<c>nativePC/plugins/...</c>
/// 塞进 <c>reframework/plugins/...</c>）。当前安装策略是按包内路径原样落盘，
/// 所以旧记录一律把 <see cref="ModRecord.TargetDirectory"/> 还原为 <see langword="null"/>。
/// </summary>
/// <remarks>
/// 只改记录与HunterBox安装时铺到游戏目录的文件；不碰游戏原始文件备份内容之外的其它目录。
/// 新安装由 <see cref="PackageInspector"/> 直接按包内路径复制，本类只负责一次性迁移旧数据。
/// </remarks>
public static class LegacyPluginPathRepair
{
    /// <summary>修正记录并移动已部署文件。</summary>
    /// <param name="mods">全部 MOD 记录（会就地修改 <see cref="ModRecord.TargetDirectory"/>）。</param>
    /// <param name="paths">HunterBox磁盘布局，用于旧备份路径迁移。</param>
    /// <param name="gameRootFor">按游戏 ID 取游戏根目录；找不到时返回 <see langword="null"/>。</param>
    /// <param name="fileSystem">文件系统端口（存在性判断、移动与空目录清理）。</param>
    /// <returns>修复结果。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static LegacyPluginPathRepairResult Repair(
        IEnumerable<ModRecord> mods,
        BoxPaths paths,
        Func<string, string?> gameRootFor,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(gameRootFor);
        ArgumentNullException.ThrowIfNull(fileSystem);

        bool changed = false;
        int moved = 0;
        List<string> warnings = [];

        foreach (ModRecord mod in mods)
        {
            if (!TryGetNewTarget(mod, out string? newTarget))
            {
                continue;
            }

            string? oldTarget = mod.TargetDirectory;
            string? gameRoot = string.IsNullOrWhiteSpace(mod.GameId) ? null : gameRootFor(mod.GameId);
            if (mod.Enabled)
            {
                if (string.IsNullOrWhiteSpace(gameRoot) || !fileSystem.DirectoryExists(gameRoot))
                {
                    warnings.Add($"{mod.DisplayName}：已启用但找不到游戏目录，记录已修正，重新启用后生效");
                }
                else
                {
                    try
                    {
                        moved += MoveDeployedFiles(gameRoot, mod, oldTarget, newTarget, paths, warnings, fileSystem);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        warnings.Add($"{mod.DisplayName}：移动已部署文件失败（{ex.Message}），记录已修正，请重新启用");
                    }
                }
            }

            mod.TargetDirectory = newTarget;
            changed = true;
            warnings.Add($"{mod.DisplayName}：安装前缀 {FormatTarget(oldTarget)} → {FormatTarget(newTarget)}");
        }

        return new LegacyPluginPathRepairResult(changed, moved, warnings);
    }

    /// <summary>旧版本只要写入了非空 TargetDirectory，就说明是自动推断出来的前缀，应还原为原路径。</summary>
    private static bool TryGetNewTarget(ModRecord mod, out string? newTarget)
    {
        newTarget = null;
        return !string.IsNullOrWhiteSpace(mod.TargetDirectory);
    }

    /// <summary>把HunterBox铺到游戏目录的错误路径文件移动到新前缀下，并迁移对应备份。</summary>
    private static int MoveDeployedFiles(
        string gameRoot,
        ModRecord mod,
        string? oldTarget,
        string? newTarget,
        BoxPaths paths,
        List<string> warnings,
        IFileSystem fileSystem)
    {
        int moved = 0;
        foreach (string file in mod.RelativeFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            string oldMapped = Map(oldTarget, file);
            string newMapped = Map(newTarget, file);
            if (string.Equals(oldMapped, newMapped, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string oldFull = BoxPaths.ResolveInside(gameRoot, oldMapped);
            string newFull = BoxPaths.ResolveInside(gameRoot, newMapped);
            if (!fileSystem.FileExists(oldFull))
            {
                continue;
            }

            string? newDirectory = Path.GetDirectoryName(newFull);
            if (!string.IsNullOrEmpty(newDirectory))
            {
                fileSystem.CreateDirectory(newDirectory);
            }

            fileSystem.MoveFile(oldFull, newFull, overwrite: true);
            moved++;
            MoveBackup(paths, mod.GameId, oldMapped, newMapped, warnings, fileSystem);
        }

        if (oldTarget is not null)
        {
            string oldRoot = BoxPaths.ResolveInside(gameRoot, oldTarget);
            DirectoryPruner.DeleteEmptyDirectories(oldRoot, fileSystem);
            PruneEmptyParents(oldRoot, Path.GetFullPath(gameRoot), fileSystem);
        }

        return moved;
    }

    /// <summary>迁移旧前缀下可能存在的游戏原文件备份。</summary>
    private static void MoveBackup(
        BoxPaths paths,
        string? gameId,
        string oldMapped,
        string newMapped,
        List<string> warnings,
        IFileSystem fileSystem)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        try
        {
            string oldBackup = paths.BackupFile(gameId, oldMapped);
            if (!fileSystem.FileExists(oldBackup))
            {
                return;
            }

            string newBackup = paths.BackupFile(gameId, newMapped);
            string? directory = Path.GetDirectoryName(newBackup);
            if (!string.IsNullOrEmpty(directory))
            {
                fileSystem.CreateDirectory(directory);
            }

            fileSystem.MoveFile(oldBackup, newBackup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add($"迁移备份失败（{oldMapped} → {newMapped}）：{ex.Message}");
        }
    }

    private static string Map(string? target, string file) =>
        string.IsNullOrWhiteSpace(target)
            ? BoxPaths.NormalizeRelative(file)
            : BoxPaths.NormalizeRelative(target + "/" + file);

    /// <summary>删掉旧前缀留下的空父目录（如 reframework/plugins、reframework），遇到非空即停止。</summary>
    private static void PruneEmptyParents(string start, string gameRoot, IFileSystem fileSystem)
    {
        try
        {
            DirectoryPruner.PruneEmptyDirectoriesUpward(start, gameRoot, fileSystem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 空父目录残留不影响功能。
        }
    }

    private static string FormatTarget(string? target) => string.IsNullOrWhiteSpace(target) ? "原路径" : target;
}
