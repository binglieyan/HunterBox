using System.Globalization;

namespace HunterBox.Application.Common;

/// <summary>
/// 文件改动撤销日志：把每一步改动的「前像 / 新建 / 移入撤销目录」登记下来，
/// 失败时逆序回滚，成功时随 <see cref="Dispose"/> 丢弃临时文件。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Mount.MountService"/>（散装文件）与 <see cref="Paks.PakMountService"/>（pak_mods）
/// 共用这一份事务脚手架：覆盖前备份、记录本次新建、把文件移入撤销目录、使用外部备份还原
/// 都登记成条目；中途失败时 <see cref="Rollback"/> 逆序恢复，单项恢复失败只追加告警、绝不中断其余条目。
/// </para>
/// <para>
/// 撤销目录按需创建（只有 <see cref="BackupFile"/>、<see cref="MoveAside"/> 需要临时前像），
/// <see cref="Dispose"/> 递归清理它；清理失败不影响主流程（统一走 <see cref="IFileSystem"/> 的尽力删除）。
/// 日志不判断「是否需要备份 / 是否该删」，这类业务判断仍留在各服务的 Planner。
/// </para>
/// <para>仅在单线程内使用（与两个挂载服务的既有约定相同）。</para>
/// </remarks>
public sealed class FileUndoJournal : IUndoScope
{
    private readonly string _undoRoot;
    private readonly IFileSystem _fileSystem;
    private readonly Action<string>? _onRollbackApplied;
    private readonly List<UndoEntry> _entries = [];
    private bool _undoRootCreated;
    private bool _rollbackIncomplete;
    private bool _rollbackCalled;
    private bool _committed;
    private bool _disposed;

    /// <summary>用撤销目录路径装配日志；目录在第一次需要临时前像时才创建。</summary>
    /// <param name="undoRoot">撤销目录绝对路径（调用方保证唯一，例如带 GUID 的临时目录）。</param>
    /// <param name="fileSystem">文件系统端口（清理撤销目录用）。</param>
    /// <param name="onRollbackApplied">
    /// 单个条目回滚成功后的回调，参数是本次被恢复/删除的路径；调用方用它立即失效指纹缓存。
    /// 为 <see langword="null"/> 时不做额外处理。
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="undoRoot"/> 为空或空白。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> 为 <see langword="null"/>。</exception>
    public FileUndoJournal(string undoRoot, IFileSystem fileSystem, Action<string>? onRollbackApplied = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(undoRoot);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _undoRoot = undoRoot;
        _fileSystem = fileSystem;
        _onRollbackApplied = onRollbackApplied;
    }

    /// <summary>覆盖前备份：把现有文件复制进撤销目录，回滚时还原它。</summary>
    /// <param name="destination">即将被覆盖的文件绝对路径。</param>
    /// <remarks>
    /// 文件在登记时不存在则复制本身抛 <see cref="FileNotFoundException"/>（与直接复制一致），
    /// 不会登记一条无法还原的条目。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 为空或空白。</exception>
    public void BackupFile(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        // 先复制再登记：复制失败时不留无效条目；复制成功后立刻登记，随后的覆盖失败也能还原。
        string preImage = NextPreImagePath();
        _fileSystem.CopyFile(destination, preImage, overwrite: true);
        _entries.Add(new UndoEntry(UndoKind.Restore, destination, preImage));
    }

    /// <summary>记录本次新建的文件：回滚时删除它。</summary>
    /// <param name="destination">本次新建的文件绝对路径。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 为空或空白。</exception>
    public void RecordCreated(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        _entries.Add(new UndoEntry(UndoKind.Created, destination));
    }

    /// <summary>把文件移入撤销目录：回滚时移回原位（用于 pak 删除/替换）。</summary>
    /// <param name="original">要移走的文件绝对路径。</param>
    /// <exception cref="ArgumentException"><paramref name="original"/> 为空或空白。</exception>
    public void MoveAside(string original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(original);

        // 先建撤销目录再移动：目录创建失败不会动到原文件；移动成功后才登记，避免回滚一个不存在的条目。
        string saved = NextPreImagePath();
        _fileSystem.MoveFile(original, saved);
        _entries.Add(new UndoEntry(UndoKind.MovedAside, original, saved));
    }

    /// <summary>登记一次「前像不在撤销目录里」的还原（例如游戏原文件的持久备份）。</summary>
    /// <param name="destination">即将被覆盖的文件绝对路径。</param>
    /// <param name="preImage">覆盖前的旧内容所在路径（撤销目录之外的备份）。</param>
    /// <param name="deletePreImageOnRollback">
    /// 为 <see langword="true"/> 表示 <paramref name="preImage"/> 是本次新建的；
    /// 回滚还原目标后会删除它，回到「没有备份」的启用前状态。
    /// </param>
    /// <exception cref="ArgumentException">任一参数为空或空白。</exception>
    public void RecordRestore(string destination, string preImage, bool deletePreImageOnRollback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(preImage);

        _entries.Add(new UndoEntry(UndoKind.Restore, destination, preImage, deletePreImageOnRollback));
    }

    /// <summary>逆序回滚全部登记；单项失败只追加告警，不中断其余条目的恢复。</summary>
    /// <param name="warnings">收集回滚失败告警的列表，调用方决定如何上报（例如并入异常文案）。</param>
    /// <remarks>
    /// 失败项会让 <see cref="Dispose"/> 保留整个撤销目录：其中可能是游戏原文件的唯一前像，
    /// 不能因为「回滚已经报过错」就把最后一份可恢复数据删掉。告警文案包含保留目录路径。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="warnings"/> 为 <see langword="null"/>。</exception>
    public void Rollback(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        _rollbackCalled = true;

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            UndoEntry entry = _entries[i];
            try
            {
                UndoOutcome outcome = TryApply(entry, out string? failure);
                if (outcome == UndoOutcome.Applied)
                {
                    _onRollbackApplied?.Invoke(entry.Destination);
                }
                else if (outcome == UndoOutcome.Failed)
                {
                    _rollbackIncomplete = true;
                    warnings.Add(FormatRollbackFailure(entry, failure));
                }
            }
            catch (Exception ex)
            {
                // 回滚回调/恢复过程中的任何异常都不能中断其余条目：转成告警并标记回滚不完整。
                _rollbackIncomplete = true;
                warnings.Add(FormatRollbackFailure(entry, ex.Message));
            }
        }
    }

    /// <summary>回滚失败告警：前像可能不在撤销目录（例如持久备份），必须输出真实位置。</summary>
    /// <param name="entry">失败的回滚条目。</param>
    /// <param name="failure">失败原因。</param>
    /// <returns>面向用户的告警文案。</returns>
    private static string FormatRollbackFailure(UndoEntry entry, string? failure)
    {
        string? preImage = entry.PreImage;
        return $"回滚文件失败：{entry.Destination}（{failure}）"
            + (string.IsNullOrEmpty(preImage) ? string.Empty : $"；前像保留在：{preImage}");
    }

    /// <summary>标记本次操作成功提交：撤销目录里的临时前像不再需要。</summary>
    public void Commit() => _committed = true;

    /// <summary>
    /// 递归清理撤销目录；提交成功或回滚全部成功时删除。
    /// 回滚有失败项、或调用方既未提交也未回滚（异常退出）时保留，避免唯一前像丢失。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool safeToDelete = _committed || (_rollbackCalled && !_rollbackIncomplete);
        if (_undoRootCreated && safeToDelete)
        {
            _fileSystem.TryDeleteDirectory(_undoRoot);
        }
    }

    /// <summary>尝试撤销一条登记，返回是否实际改动、无需处理或失败。</summary>
    /// <param name="entry">已登记的改动。</param>
    /// <param name="failure">失败原因；成功或无需处理时为 <see langword="null"/>。</param>
    /// <returns>撤销结果。</returns>
    private UndoOutcome TryApply(UndoEntry entry, out string? failure)
    {
        failure = null;
        switch (entry.Kind)
        {
            case UndoKind.Created:
                // 目标已经不存在：撤销的目的已经达到，不算失败。
                return TryDelete(entry.Destination) ? UndoOutcome.Applied : UndoOutcome.Noop;

            case UndoKind.MovedAside:
                if (entry.PreImage is not { } saved || !_fileSystem.FileExists(saved))
                {
                    failure = "移入撤销目录的前像缺失";
                    return UndoOutcome.Failed;
                }

                if (_fileSystem.FileExists(entry.Destination))
                {
                    failure = "目标路径已被重新创建，前像未移回";
                    return UndoOutcome.Failed;
                }

                _fileSystem.MoveFile(saved, entry.Destination);
                return UndoOutcome.Applied;

            default:
                return TryRestore(entry, out failure);
        }
    }

    /// <summary>撤销结果。</summary>
    private enum UndoOutcome
    {
        /// <summary>实际执行了撤销动作。</summary>
        Applied,

        /// <summary>无需处理（例如本次新建的文件已经不存在）。</summary>
        Noop,

        /// <summary>无法完整撤销，前像需要保留。</summary>
        Failed,
    }

    /// <summary>还原一个「有前像」的条目：前像可用则复制回来，前像缺失则视为回滚失败。</summary>
    /// <param name="entry">已登记的改动。</param>
    /// <param name="failure">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>撤销结果。</returns>
    private UndoOutcome TryRestore(UndoEntry entry, out string? failure)
    {
        failure = null;
        if (entry.PreImage is { } preImage)
        {
            if (!_fileSystem.FileExists(preImage))
            {
                // 前像已经不在：不能假装回滚成功；目标也不应继续保留HunterBox写入的内容。
                TryDelete(entry.Destination);
                failure = "前像缺失，原内容已无法恢复";
                return UndoOutcome.Failed;
            }

            _fileSystem.CopyFile(preImage, entry.Destination, overwrite: true);
            if (entry.DeletePreImageOnRollback)
            {
                _fileSystem.DeleteFile(preImage);
            }

            return UndoOutcome.Applied;
        }

        // 没有前像表示改动前目标不存在：删除本次新建的文件即可。
        return TryDelete(entry.Destination) ? UndoOutcome.Applied : UndoOutcome.Noop;
    }

    /// <summary>删除本次新建的文件；文件已不存在时返回 <see langword="false"/>。</summary>
    private bool TryDelete(string destination)
    {
        if (!_fileSystem.FileExists(destination))
        {
            return false;
        }

        _fileSystem.DeleteFile(destination);
        return true;
    }

    /// <summary>取下一个临时前像路径；第一次需要时创建撤销目录。</summary>
    private string NextPreImagePath()
    {
        EnsureUndoRoot();
        return Path.Combine(
            _undoRoot,
            _entries.Count.ToString(CultureInfo.InvariantCulture) + ".bak");
    }

    /// <summary>按需创建撤销目录（没有任何 <see cref="BackupFile"/>/<see cref="MoveAside"/> 时不产生空目录）。</summary>
    private void EnsureUndoRoot()
    {
        if (_undoRootCreated)
        {
            return;
        }

        _fileSystem.CreateDirectory(_undoRoot);
        _undoRootCreated = true;
    }

    /// <summary>一条已登记的改动，用于失败时逆序撤销。</summary>
    /// <param name="Kind">撤销方式。</param>
    /// <param name="Destination">游戏/目标目录里的受影响路径。</param>
    /// <param name="PreImage">改动前的旧内容路径；<see langword="null"/> 表示原本不存在。</param>
    /// <param name="DeletePreImageOnRollback">还原后是否删除 <paramref name="PreImage"/>（本次新建的外部备份）。</param>
    private sealed record UndoEntry(
        UndoKind Kind,
        string Destination,
        string? PreImage = null,
        bool DeletePreImageOnRollback = false);

    /// <summary>撤销方式。</summary>
    private enum UndoKind
    {
        /// <summary>用前像还原目标（前像不可用时删除目标）。</summary>
        Restore,

        /// <summary>删除本次新建的目标。</summary>
        Created,

        /// <summary>把移入撤销目录的文件移回原位。</summary>
        MovedAside,
    }
}
