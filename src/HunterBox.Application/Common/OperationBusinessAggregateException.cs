namespace HunterBox.Application.Common;

/// <summary>
/// 已知业务来源的聚合异常：消息本身就包含用户可读的业务上下文。
/// </summary>
/// <remarks>
/// 清理用例在心跳/最终保存失败时抛出本类型：异常消息已包含「已禁用 N/M」与保守保留告警，
/// <see cref="OperationError.From"/> 据此直接归为 <see cref="OperationErrorKind.Business"/>，
/// 不依赖内部异常的组合（混合或空集合都不会丢失提示）。
/// </remarks>
public sealed class OperationBusinessAggregateException : AggregateException
{
    /// <summary>装配不带消息的业务聚合异常。</summary>
    public OperationBusinessAggregateException()
    {
    }

    /// <summary>装配业务聚合异常。</summary>
    /// <param name="message">可直接展示给用户的消息。</param>
    public OperationBusinessAggregateException(string message)
        : base(message)
    {
    }

    /// <summary>装配业务聚合异常（单个内部异常）。</summary>
    /// <param name="message">可直接展示给用户的消息。</param>
    /// <param name="innerException">导致失败的内部异常。</param>
    public OperationBusinessAggregateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>装配业务聚合异常（多个内部异常）。</summary>
    /// <param name="message">可直接展示给用户的消息。</param>
    /// <param name="innerExceptions">导致失败的内部异常。</param>
    public OperationBusinessAggregateException(string message, params Exception[] innerExceptions)
        : base(message, innerExceptions)
    {
    }
}
