using System.Diagnostics.CodeAnalysis;

namespace HunterBox.Domain.Paks;

/// <summary>pak 同名冲突导致无法启用。</summary>
/// <remarks>界面应捕获本异常，把 <see cref="Conflicts"/> 里的文件名与占用方展示给用户。</remarks>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "冲突异常必须携带冲突明细，无参/无明细构造会让 Conflicts 无法初始化；界面需要按明细展示。")]
public sealed class PakConflictException : Exception
{
    /// <summary>用冲突列表构造异常。</summary>
    /// <param name="conflicts">冲突明细，至少一条。</param>
    /// <exception cref="ArgumentNullException"><paramref name="conflicts"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="conflicts"/> 为空（没有冲突就不该构造本异常）。</exception>
    public PakConflictException(IReadOnlyList<PakConflict> conflicts)
        : base(BuildMessage(conflicts ?? throw new ArgumentNullException(nameof(conflicts))))
    {
        if (conflicts.Count == 0)
        {
            throw new ArgumentException("冲突列表不能为空", nameof(conflicts));
        }

        Conflicts = conflicts;
    }

    /// <summary>冲突明细。</summary>
    public IReadOnlyList<PakConflict> Conflicts { get; }

    private static string BuildMessage(IReadOnlyList<PakConflict>? conflicts)
    {
        if (conflicts is null || conflicts.Count == 0)
        {
            return "pak 同名冲突";
        }

        IEnumerable<string> lines = conflicts.Select(c =>
            $"{c.FileName}（已被 {c.Owner.ModName} 占用，{c.Incoming.ModName} 无法启用）");
        return "pak 同名冲突：" + string.Join("；", lines);
    }
}
