using HunterBox.Domain.Mods;

namespace HunterBox.Application.Models;

/// <summary>按时间预算轮换刷新过期内容戳。</summary>
/// <remarks>
/// 调用方传入稳定的过期候选顺序与游标；每次只消费预算内的条目（未取消时至少处理一个），
/// 避免慢盘或读取耗时的时钟粒度导致游标永远停在同一条目。核心逻辑放在 Application，便于用可控耗时测试预算与轮换。
/// 单个候选项的 IO/权限异常只跳过该项并通过 <c>warnBatch</c> 批量告警，不中断整轮；告警回调自身抛异常同样被忽略。
/// 取消令牌在条目边界生效；逐项告警最多输出 10 条，其余合并为一条汇总，避免整盘不可读时刷爆日志。
/// </remarks>
public static class ModelStampRefresh
{
    /// <summary>逐项告警上限：超过后只输出一条汇总。</summary>
    private const int MaxWarningDetails = 10;

    /// <summary>在时间预算内轮换刷新候选。</summary>
    /// <param name="candidates">过期候选（顺序应稳定）。</param>
    /// <param name="cursor">上次消费到的位置。</param>
    /// <param name="budgetMilliseconds">本轮时间预算（毫秒）。</param>
    /// <param name="elapsedMilliseconds">读取已耗时（毫秒）的委托。</param>
    /// <param name="refresh">刷新单个候选。</param>
    /// <param name="cancellationToken">取消令牌；在条目边界检查。</param>
    /// <param name="warnBatch">批量告警回调（整轮只调用一次）；为 <see langword="null"/> 时不记录，回调自身的异常会被忽略。</param>
    /// <param name="nextCursor">下一轮的起始位置（已按候选数取模，调用方无需再算）。</param>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budgetMilliseconds"/> 为负数。</exception>
    public static void RefreshWithinBudget(
        IReadOnlyList<ModRecord> candidates,
        int cursor,
        int budgetMilliseconds,
        Func<long> elapsedMilliseconds,
        Action<ModRecord> refresh,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? warnBatch,
        out int nextCursor)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(elapsedMilliseconds);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetMilliseconds);

        int count = candidates.Count;
        if (count == 0)
        {
            nextCursor = 0;
            return;
        }

        int start = ((cursor % count) + count) % count;
        int processed = 0;
        int failures = 0;
        List<string>? warnings = null;
        while (processed < count)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 至少处理一个：即使耗时读数在首项前已超预算，游标也必须前进，避免永远卡在同一项。
            if (processed > 0 && elapsedMilliseconds() >= budgetMilliseconds)
            {
                break;
            }

            ModRecord candidate = candidates[(start + processed) % count];
            try
            {
                refresh(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单项不可读只影响该项；告警最多 10 条，回调异常不影响刷新。
                failures++;
                if (failures <= MaxWarningDetails)
                {
                    (warnings ??= []).Add($"刷新模型内容戳失败（MOD {candidate.Id}）：{ex.Message}");
                }
            }

            processed++;
        }

        if (failures > MaxWarningDetails)
        {
            (warnings ??= []).Add($"其余 {failures - MaxWarningDetails} 个 MOD 刷新失败已省略。");
        }

        if (warnings is not null)
        {
            // 整轮只调用一次批量告警，避免 10+1 次独立写日志。
            SafeWarn(warnBatch, warnings);
        }

        nextCursor = (start + processed) % count;
    }

    /// <summary>调用批量告警回调并吞掉回调自身异常，保证刷新流程不被日志故障中断。</summary>
    /// <param name="warnBatch">批量告警回调；为 <see langword="null"/> 时不记录。</param>
    /// <param name="messages">本轮告警。</param>
    private static void SafeWarn(Action<IReadOnlyList<string>>? warnBatch, List<string> messages)
    {
        if (warnBatch is null)
        {
            return;
        }

        try
        {
            warnBatch(messages);
        }
        catch (Exception)
        {
            // 告警失败不能影响刷新；此处没有更合适的日志出口。
        }
    }
}
