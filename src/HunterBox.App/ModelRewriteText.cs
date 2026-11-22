using HunterBox.Application.Models;

namespace HunterBox.App;

/// <summary>把模型改写结果映射成用户可读的多行提示（表示层职责，A-16）。</summary>
internal static class ModelRewriteText
{
    /// <summary>生成汇总文案：不支持改写的 pak、实际改写数与提示列表。</summary>
    /// <param name="summary">改写结果。</param>
    /// <returns>多行提示文本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> 为 <see langword="null"/>。</exception>
    public static string Build(ModelRewriteSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        List<string> lines = [];
        if (summary.UnsupportedPaks.Count > 0)
        {
            lines.Add($"有 {summary.UnsupportedPaks.Count} 个 pak 不支持改写（条目表加密），已跳过：");
            // 先按条数截断，再给展示项加前缀。
            lines.Add(DialogTextLimits.JoinTruncated(
                summary.UnsupportedPaks,
                DialogTextLimits.DetailLines,
                Environment.NewLine,
                unit: "个",
                logFile: AppServices.LogFile,
                format: pak => "  " + pak));
        }

        lines.Add(summary.HasChanges
            ? $"已改写 {summary.EntriesChanged} 个 pak 条目、{summary.FilesMoved} 个散装文件。"
            : "没有发生改写（所选目标与当前模型相同，或路径不在模型清单中）。");
        if (summary.Warnings.Count > 0)
        {
            lines.Add($"另有 {summary.Warnings.Count} 条提示：");
            lines.Add(DialogTextLimits.JoinTruncated(
                summary.Warnings,
                DialogTextLimits.DetailLines,
                Environment.NewLine,
                unit: "条",
                logFile: AppServices.LogFile,
                format: warning => "  " + warning));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>把「模型记录保存失败」追加到汇总文案：成功提示不能掩盖落盘失败。</summary>
    /// <param name="text">已生成的汇总文案。</param>
    /// <param name="persistenceWarning">保存失败提示；为 <see langword="null"/> 时原样返回。</param>
    /// <returns>追加了保存失败提示的文案。</returns>
    public static string AppendPersistenceWarning(string text, string? persistenceWarning) =>
        string.IsNullOrWhiteSpace(persistenceWarning)
            ? text
            : text + Environment.NewLine + Environment.NewLine + persistenceWarning;
}
