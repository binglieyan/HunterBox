using System.Diagnostics.CodeAnalysis;

namespace HunterBox.Application.Saves;

/// <summary>
/// 存档还原的哈希校验失败：携带结构化校验结果，界面按
/// <see cref="SaveBackupVerification.Issues"/> 生成用户文案（A-16）。
/// </summary>
/// <remarks>
/// 异常消息只用于日志，不直接展示给用户；调用方应读取 <see cref="Verification"/> 与
/// <see cref="RolledBack"/> 生成提示。
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "校验异常必须携带结构化校验结果，无参/仅消息构造会让 Verification 无法初始化；调用方依赖该结果。")]
public sealed class SaveBackupVerificationException : Exception
{
    /// <summary>用校验结果构造异常。</summary>
    /// <param name="verification">包含失败项的校验结果。</param>
    /// <param name="rolledBack">
    /// 覆盖当前存档后校验失败、且已用还原前快照成功回滚时为 <see langword="true"/>；
    /// 校验在改动当前存档之前失败（如备份源损坏）时为 <see langword="false"/>。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="verification"/> 为 <see langword="null"/>。</exception>
    public SaveBackupVerificationException(SaveBackupVerification verification, bool rolledBack = false)
        : base(rolledBack ? "存档备份哈希校验失败，已回滚到还原前的当前存档" : "存档备份哈希校验失败")
    {
        ArgumentNullException.ThrowIfNull(verification);
        Verification = verification;
        RolledBack = rolledBack;
    }

    /// <summary>失败的结构化校验结果。</summary>
    public SaveBackupVerification Verification { get; }

    /// <summary>当前存档是否已回滚到还原前的状态。</summary>
    public bool RolledBack { get; }
}
