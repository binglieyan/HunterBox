using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Activation;

/// <summary>批量启用/禁用/重新复制的逐项结果。</summary>
/// <param name="Processed">已成功处理的 MOD 数量。</param>
/// <param name="Total">本次请求处理的 MOD 总数。</param>
/// <param name="Canceled">是否因取消而提前停止（停止时未处理项保持原状）。</param>
/// <param name="Failures">逐项失败说明（形如「MOD 名：原因」）；是否中断由批量动作决定。</param>
public sealed record BatchActivationResult(
    int Processed,
    int Total,
    bool Canceled,
    IReadOnlyList<string> Failures)
{
    /// <summary>逐项警告（如「MOD 名：pak 源缺失，已保留残留」）；警告不影响成功判定。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 批量启用 / 禁用 / 重新复制：逐项调用 <see cref="ModActivationService"/>，
/// 每项之间检查取消令牌并上报进度，取消时已处理项保持各自完成后的状态（不再回滚已完成项）。
/// </summary>
/// <remarks>
/// <para>
/// 与界面原语义保持一致：
/// <list type="bullet">
/// <item>启用：单项失败不中断其它项（逐条汇总失败）。</item>
/// <item>禁用：单项失败也不中断其它项（逐条汇总失败），与「禁用全部MOD」一致。</item>
/// <item>重新复制：单项失败立即停止（与「重新复制MOD」原行为一致）。</item>
/// </list>
/// </para>
/// <para>
/// 取消只在项与项之间生效，因此不会出现「一个 MOD 半启用/半禁用」的中间状态；
/// 单个 MOD的启用/禁用仍由 <see cref="ModActivationService"/>的事务/回滚保证一致。
/// </para>
/// <para>
/// 落盘：整批共用一个 <see cref="ModActivationService.BeginSaveBatch"/> 作用域，逐项成功不再整表保存，
/// 批量结束（含取消/逐项失败）时统一保存一次，已处理项不丢；单项路径不受影响（P-07）。
/// </para>
/// </remarks>
public sealed class ModBatchActivator
{
    private readonly ModActivationService _activation;

    /// <summary>装配批量动作。</summary>
    /// <param name="activation">单项启用/禁用/重新复制用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="activation"/> 为 <see langword="null"/>。</exception>
    public ModBatchActivator(ModActivationService activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        _activation = activation;
    }

    /// <summary>按给定顺序批量启用。</summary>
    /// <param name="mods">待启用的 MOD（按列表顺序）。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="cancellationToken">取消令牌（在项与项之间检查）。</param>
    /// <param name="progress">进度回调：<c>(已处理数, 总数)</c>；可能从后台线程调用。</param>
    /// <returns>逐项结果；取消时已启用的项保持启用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mods"/> 为 <see langword="null"/>。</exception>
    public BatchActivationResult Enable(
        IReadOnlyList<ModRecord> mods,
        PakNamingMode pakMode,
        CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null) =>
        Run(mods, (mod, token) => _activation.Enable(mod, pakMode, token), continueOnFailure: true, cancellationToken, progress);

    /// <summary>按给定顺序批量禁用。</summary>
    /// <param name="mods">待禁用的 MOD（按列表顺序）。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="cancellationToken">取消令牌（在项与项之间检查）。</param>
    /// <param name="progress">进度回调：<c>(已处理数, 总数)</c>；可能从后台线程调用。</param>
    /// <returns>逐项结果；取消时已禁用的项保持禁用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mods"/> 为 <see langword="null"/>。</exception>
    public BatchActivationResult Disable(
        IReadOnlyList<ModRecord> mods,
        PakNamingMode pakMode,
        CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null) =>
        Run(mods, (mod, token) => _activation.Disable(mod, pakMode, token), continueOnFailure: true, cancellationToken, progress);

    /// <summary>按给定顺序批量重新复制（要求 MOD 均已启用）。</summary>
    /// <param name="mods">待重新复制的 MOD（按列表顺序）。</param>
    /// <param name="pakMode">pak 命名策略。</param>
    /// <param name="cancellationToken">取消令牌（在项与项之间检查）。</param>
    /// <param name="progress">进度回调：<c>(已处理数, 总数)</c>；可能从后台线程调用。</param>
    /// <returns>逐项结果；遇到第一个失败即停止。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mods"/> 为 <see langword="null"/>。</exception>
    public BatchActivationResult Recopy(
        IReadOnlyList<ModRecord> mods,
        PakNamingMode pakMode,
        CancellationToken cancellationToken = default,
        IProgress<(int Done, int Total)>? progress = null) =>
        Run(mods, (mod, token) => _activation.Recopy(mod, pakMode, token), continueOnFailure: false, cancellationToken, progress);

    private BatchActivationResult Run(
        IReadOnlyList<ModRecord> mods,
        Func<ModRecord, CancellationToken, ActivationReport> action,
        bool continueOnFailure,
        CancellationToken cancellationToken,
        IProgress<(int Done, int Total)>? progress)
    {
        ArgumentNullException.ThrowIfNull(mods);

        // 整批统一落盘一次：作用域结束（正常/取消/逐项失败）时保存，已处理项的启用状态不会丢（P-07）。
        BatchActivationResult result;
        using (IDisposable saveBatch = _activation.BeginSaveBatch())
        {
            result = RunCore(mods, action, continueOnFailure, cancellationToken, progress);
        }

        // 作用域结束（EndBatch）后回收未随逐项报告带出的候选告警。
        IReadOnlyList<string> leftover = _activation.TakeBatchWarnings();
        if (leftover.Count == 0)
        {
            return result;
        }

        List<string> warnings = [.. result.Warnings, .. leftover];
        return result with { Warnings = warnings };
    }

    private static BatchActivationResult RunCore(
        IReadOnlyList<ModRecord> mods,
        Func<ModRecord, CancellationToken, ActivationReport> action,
        bool continueOnFailure,
        CancellationToken cancellationToken,
        IProgress<(int Done, int Total)>? progress)
    {
        List<string> failures = [];
        List<string> warnings = [];
        int processed = 0;
        for (int index = 0; index < mods.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new BatchActivationResult(processed, mods.Count, true, failures) { Warnings = warnings };
            }

            ModRecord mod = mods[index];
            try
            {
                ActivationReport report = action(mod, cancellationToken);
                foreach (string warning in report.Warnings)
                {
                    warnings.Add($"{mod.DisplayName}：{warning}");
                }

                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new BatchActivationResult(processed, mods.Count, true, failures) { Warnings = warnings };
            }
            catch (Exception ex)
            {
                failures.Add($"{mod.DisplayName}：{ex.Message}");
                if (!continueOnFailure)
                {
                    return new BatchActivationResult(processed, mods.Count, false, failures) { Warnings = warnings };
                }
            }

            progress?.Report((index + 1, mods.Count));
        }

        return new BatchActivationResult(processed, mods.Count, false, failures) { Warnings = warnings };
    }
}
