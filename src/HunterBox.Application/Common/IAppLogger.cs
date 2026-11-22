namespace HunterBox.Application.Common;

/// <summary>
/// 应用日志端口：Infrastructure 的适配器通过它记录诊断信息，测试可注入内存实现（A-23）。
/// </summary>
/// <remarks>
/// 端口只覆盖写日志，不暴露日志文件路径/轮转等实现细节；生产实现是 Infrastructure 的
/// <c>AppLogAdapter</c>，底层仍是进程级文件日志。
/// </remarks>
public interface IAppLogger
{
    /// <summary>记录普通信息。</summary>
    /// <param name="message">内容。</param>
    void Info(string message);

    /// <summary>记录警告。</summary>
    /// <param name="message">内容。</param>
    void Warn(string message);

    /// <summary>
    /// 批量记录警告：按给定顺序记录全部条目。
    /// </summary>
    /// <remarks>没有默认实现：所有实现都必须明确批量语义，避免逐条刷盘等实现细节藏在端口里。</remarks>
    /// <param name="messages">警告内容列表。</param>
    void WarnBatch(IReadOnlyList<string> messages);

    /// <summary>记录错误。</summary>
    /// <param name="message">内容。</param>
    /// <param name="exception">异常（可选，会附完整堆栈）。</param>
    void Error(string message, Exception? exception = null);
}
