namespace HunterBox.Application.Saves;

/// <summary>存档目录候选的歧义策略。</summary>
/// <remarks>
/// Steam <c>userdata</c> 可能同时存在多个账号：歧义时不替用户选择，
/// 避免把别的账号的存档目录写进配置并读写他人存档。
/// 探测（枚举候选）留在 Infrastructure，策略放在 Application 便于单测与复用。
/// </remarks>
public static class SavePathSelection
{
    /// <summary>从候选里选出唯一可用的存档目录。</summary>
    /// <param name="candidates">候选目录（顺序无关）。</param>
    /// <returns>恰好一个候选时返回它；没有或多个候选时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> 为 <see langword="null"/>。</exception>
    public static string? SelectSingle(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
