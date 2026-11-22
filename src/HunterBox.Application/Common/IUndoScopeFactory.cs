namespace HunterBox.Application.Common;

/// <summary>
/// 创建撤销作用域的工厂：用例只依赖抽象，测试或其它实现可替换具体的撤销机制。
/// </summary>
public interface IUndoScopeFactory
{
    /// <summary>为一个操作创建撤销作用域。</summary>
    /// <param name="undoRoot">撤销目录绝对路径（调用方保证唯一）。</param>
    /// <param name="onRollbackApplied">
    /// 单个条目回滚成功后的回调，参数是本次被恢复/删除的路径；为 <see langword="null"/> 时不做额外处理。
    /// </param>
    /// <returns>撤销作用域；调用方用 <see langword="using"/> 管理其生命周期。</returns>
    IUndoScope Create(string undoRoot, Action<string>? onRollbackApplied = null);
}
