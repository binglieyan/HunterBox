namespace HunterBox.Application.Common;

/// <summary>
/// 文件事务的补偿作用域：把每一步改动登记成可逆操作，失败时逆序撤销。
/// </summary>
/// <remarks>
/// <para>
/// 挂载类用例（散装文件、<c>pak_mods</c>）通过本接口登记前像、新建与移走动作，
/// 具体实现由 <see cref="IUndoScopeFactory"/> 提供；生产实现是 <see cref="FileUndoJournal"/>。
/// </para>
/// <para>
/// 生命周期与撤销目录绑定：<see cref="IDisposable.Dispose"/> 负责清理成功提交或完整回滚后的临时目录；
/// 回滚不完整时实现必须保留前像，避免丢掉唯一可恢复数据。
/// </para>
/// <para>仅在单线程内使用（与挂载服务的既有约定相同）。</para>
/// </remarks>
public interface IUndoScope : IDisposable
{
    /// <summary>覆盖前备份：把现有文件复制进撤销目录，回滚时还原它。</summary>
    /// <param name="destination">即将被覆盖的文件绝对路径。</param>
    void BackupFile(string destination);

    /// <summary>记录本次新建的文件：回滚时删除它。</summary>
    /// <param name="destination">本次新建的文件绝对路径。</param>
    void RecordCreated(string destination);

    /// <summary>把文件移入撤销目录：回滚时移回原位。</summary>
    /// <param name="original">要移走的文件绝对路径。</param>
    void MoveAside(string original);

    /// <summary>登记一次「前像不在撤销目录里」的还原。</summary>
    /// <param name="destination">即将被覆盖的文件绝对路径。</param>
    /// <param name="preImage">覆盖前的旧内容所在路径。</param>
    /// <param name="deletePreImageOnRollback">回滚还原后是否删除 <paramref name="preImage"/>。</param>
    void RecordRestore(string destination, string preImage, bool deletePreImageOnRollback = false);

    /// <summary>逆序回滚全部登记；单项失败只追加告警，不中断其余条目。</summary>
    /// <param name="warnings">收集回滚失败告警的列表。</param>
    void Rollback(List<string> warnings);

    /// <summary>标记本次操作成功提交：撤销目录里的临时前像不再需要。</summary>
    void Commit();
}
