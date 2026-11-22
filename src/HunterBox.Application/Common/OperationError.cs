using HunterBox.Application.Saves;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Common;

/// <summary>操作错误的类别：界面据此决定提示语气，不再靠异常消息文本猜测（A-12）。</summary>
public enum OperationErrorKind
{
    /// <summary>用户可理解并修正的业务错误（游戏运行中、MOD 未启用、pak 冲突等）。</summary>
    Business,

    /// <summary>运行环境导致、通常可提示用户检查磁盘/权限/路径的错误。</summary>
    Environment,

    /// <summary>未预期故障：界面只给通用提示，细节写日志。</summary>
    Unexpected,

    /// <summary>用户主动取消。</summary>
    Canceled,
}

/// <summary>
/// 操作结果里的错误契约（A-12）：把「给用户看的文案」与「给日志/诊断的技术细节」分开，
/// 并由类别决定未知故障是否原样展示。
/// </summary>
/// <param name="Kind">错误类别。</param>
/// <param name="UserMessage">可直接展示给用户的文案。</param>
/// <param name="TechnicalDetail">技术细节（异常消息/堆栈），只进日志。</param>
public sealed record OperationError(OperationErrorKind Kind, string UserMessage, string? TechnicalDetail)
{
    /// <summary>
    /// 把异常归类为面向界面的错误契约。
    /// 无法归类的 <see cref="InvalidDataException"/> 保留异常自带文案（可能是清单不可读/路径非法/内容不一致），
    /// 界面只展示该文案、不再统一概括为「与清单不一致」。
    /// </summary>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>带类别的错误对象；未预期异常不会把原始消息暴露给用户。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> 为 <see langword="null"/>。</exception>
    public static OperationError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => new(OperationErrorKind.Canceled, "操作已取消", null),
            PakConflictException conflict => new(OperationErrorKind.Business, conflict.Message, conflict.Message),
            SaveBackupVerificationException verification => new(
                OperationErrorKind.Business,
                BuildSaveVerificationMessage(verification),
                verification.ToString()),
            InvalidDataException data => new(OperationErrorKind.Business, data.Message, data.Message),
            InvalidOperationException business => new(OperationErrorKind.Business, business.Message, business.Message),
            // 已知业务来源的聚合异常（如清理心跳失败）直接展示聚合消息，混合/空内部异常也不丢进度文案。
            OperationBusinessAggregateException businessAggregate => new(
                OperationErrorKind.Business,
                businessAggregate.Message,
                businessAggregate.ToString()),
            AggregateException aggregate => FromAggregate(aggregate),
            IOException environment => new(OperationErrorKind.Environment, environment.Message, environment.ToString()),
            UnauthorizedAccessException environment => new(OperationErrorKind.Environment, environment.Message, environment.ToString()),
            System.Security.SecurityException environment => new(OperationErrorKind.Environment, environment.Message, environment.ToString()),
            _ => new(OperationErrorKind.Unexpected, "操作失败，请查看日志了解详情。", exception.ToString()),
        };
    }

    /// <summary>
    /// 聚合异常按内部异常递归分类：全部同类时沿用该类别并保留聚合消息（如清理心跳的「已禁用 N/M」），
    /// 混合或未知故障统一归为未预期。
    /// </summary>
    /// <param name="aggregate">聚合异常。</param>
    /// <returns>分类后的错误对象。</returns>
    private static OperationError FromAggregate(AggregateException aggregate)
    {
        OperationErrorKind? kind = null;
        foreach (Exception inner in aggregate.InnerExceptions)
        {
            OperationError classified = From(inner);
            if (classified.Kind == OperationErrorKind.Unexpected)
            {
                return new(OperationErrorKind.Unexpected, "操作失败，请查看日志了解详情。", aggregate.ToString());
            }

            if (kind is null)
            {
                kind = classified.Kind;
            }
            else if (kind != classified.Kind)
            {
                return new(OperationErrorKind.Unexpected, "操作失败，请查看日志了解详情。", aggregate.ToString());
            }
        }

        return kind switch
        {
            OperationErrorKind.Business => new(OperationErrorKind.Business, aggregate.Message, aggregate.ToString()),
            OperationErrorKind.Environment => new(OperationErrorKind.Environment, aggregate.Message, aggregate.ToString()),
            OperationErrorKind.Canceled => new(OperationErrorKind.Canceled, "操作已取消", null),
            _ => new(OperationErrorKind.Unexpected, "操作失败，请查看日志了解详情。", aggregate.ToString()),
        };
    }

    /// <summary>用结构化校验结果生成用户文案，不展示异常里只给日志用的原始消息。</summary>
    private static string BuildSaveVerificationMessage(SaveBackupVerificationException exception)
    {
        string rollback = exception.RolledBack ? "，已回滚到还原前的存档" : string.Empty;
        return $"存档校验失败：{exception.Verification.Issues.Count} 个文件与备份清单不一致{rollback}";
    }
}
