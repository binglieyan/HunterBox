using HunterBox.Domain.Layout;

namespace HunterBox.Domain.Mods;

/// <summary>散装文件在游戏根目录里的一次文件操作类型。</summary>
public enum FileOperationKind
{
    /// <summary>直接复制（覆盖自己或别的 MOD 留下的同名文件）。</summary>
    Copy,

    /// <summary>先把游戏原文件备份到 <c>Mods/.backup</c>，再复制。</summary>
    BackupThenCopy,

    /// <summary>删除（禁用时，且没有其它启用中的 MOD 需要该文件）。</summary>
    Delete,

    /// <summary>从备份还原游戏原文件。</summary>
    RestoreBackup,
}

/// <summary>散装文件的一次文件操作。</summary>
/// <param name="Kind">操作类型。</param>
/// <param name="RelativePath">相对游戏根目录的路径（正斜杠、小写）。</param>
public sealed record FileOperation(FileOperationKind Kind, string RelativePath);

/// <summary>
/// 启用/禁用时需要查询的现场状态。
/// 由 Application 层根据 <see cref="IModStore"/> 与磁盘实际情况实现。
/// </summary>
public sealed class MountContext
{
    /// <summary>游戏根目录里是否已存在该相对路径。</summary>
    public required Func<string, bool> ExistsInGame { get; init; }

    /// <summary>
    /// 该相对路径任一已启用占用方；没有则返回 <see langword="null"/>。
    /// 仅用于存在性判断，实际生效方按启用顺序末位优先。
    /// </summary>
    public required Func<string, int?> OwnerOf { get; init; }

    /// <summary>该相对路径是否已有HunterBox留下的备份。</summary>
    public required Func<string, bool> HasBackup { get; init; }

    /// <summary>除当前 MOD 外，是否还有其它启用中的 MOD 提供该相对路径。</summary>
    public required Func<string, bool> ClaimedByOtherEnabledMod { get; init; }

    /// <summary>空现场：游戏目录里什么都没有，也没有备份。</summary>
    public static MountContext Empty { get; } = new()
    {
        ExistsInGame = _ => false,
        OwnerOf = _ => null,
        HasBackup = _ => false,
        ClaimedByOtherEnabledMod = _ => false,
    };
}

/// <summary>
/// 计算把散装 MOD 铺进游戏根目录 / 从根目录撤出需要哪些文件操作
/// （<c>docs/SPEC.md</c> 4.1）。只产出计划，不碰磁盘。
/// </summary>
public static class LooseFilePlanner
{
    /// <summary>计算启用某个 MOD 需要执行的操作。</summary>
    /// <param name="relativePaths">该 MOD 需要写入游戏根目录的相对路径。</param>
    /// <param name="context">现场状态。</param>
    /// <returns>按输入顺序排列的文件操作。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">路径元素为空/空白、绝对路径或包含 <c>..</c>。</exception>
    public static IReadOnlyList<FileOperation> PlanEnable(
        IEnumerable<string> relativePaths,
        MountContext context)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentNullException.ThrowIfNull(context);

        List<FileOperation> operations = [];
        foreach (string path in Distinct(relativePaths))
        {
            if (!context.ExistsInGame(path))
            {
                operations.Add(new FileOperation(FileOperationKind.Copy, path));
                continue;
            }

            // 已有备份 → 游戏原文件早已存好，别重复备份；
            // 有HunterBox上的占用方 → 覆盖它；剩下才是游戏原文件，先备份再覆盖。
            FileOperationKind kind = context.HasBackup(path) || context.OwnerOf(path) is not null
                ? FileOperationKind.Copy
                : FileOperationKind.BackupThenCopy;
            operations.Add(new FileOperation(kind, path));
        }

        return operations;
    }

    /// <summary>计算禁用某个 MOD 需要执行的操作。</summary>
    /// <param name="relativePaths">该 MOD 之前写入游戏根目录的相对路径。</param>
    /// <param name="context">现场状态。</param>
    /// <returns>按输入顺序排列的文件操作；不需要处理的文件不会出现在结果里。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">路径元素为空/空白、绝对路径或包含 <c>..</c>。</exception>
    public static IReadOnlyList<FileOperation> PlanDisable(
        IEnumerable<string> relativePaths,
        MountContext context)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentNullException.ThrowIfNull(context);

        List<FileOperation> operations = [];
        foreach (string path in Distinct(relativePaths))
        {
            bool hasBackup = context.HasBackup(path);
            if (!context.ExistsInGame(path) && !hasBackup)
            {
                // 目标不存在且没有备份：无事可做。目标不存在但有备份时必须继续，
                // 否则游戏原文件会永久留在备份目录（C-05）。
                continue;
            }

            // 还有别的启用中 MOD 需要这个文件 → 留着。
            if (context.ClaimedByOtherEnabledMod(path))
            {
                continue;
            }

            operations.Add(hasBackup
                ? new FileOperation(FileOperationKind.RestoreBackup, path)
                : new FileOperation(FileOperationKind.Delete, path));
        }

        return operations;
    }

    /// <summary>
    /// 校验并规范化输入路径：空/空白、绝对路径、含 <c>..</c> 等非法路径按契约抛
    /// <see cref="ArgumentException"/>，不再静默丢弃；输出「正斜杠、小写、去重」的路径。
    /// </summary>
    private static IEnumerable<string> Distinct(IEnumerable<string> relativePaths)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException($"文件路径不能为空: \"{raw}\"", nameof(relativePaths));
            }

            // 与落盘阶段用同一套校验：绝对路径 / 含 .. / 含盘符都会在这里抛 ArgumentException。
            string normalized = PackageInspector.Normalize(BoxPaths.NormalizeRelative(raw));
            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }
}
