namespace HunterBox.Application.Common;

/// <summary>可替换的时钟端口：避免业务代码直接依赖 <see cref="DateTimeOffset.Now"/>。</summary>
public interface IClock
{
    /// <summary>当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>使用系统时钟的默认实现。</summary>
public sealed class SystemClock : IClock
{
    /// <summary>共享实例（无状态）。</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
