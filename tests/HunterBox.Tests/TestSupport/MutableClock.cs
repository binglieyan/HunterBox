using HunterBox.Application.Common;

namespace HunterBox.Tests;

/// <summary>可推进的测试时钟（测试工程只保留这一份实现）。</summary>
internal sealed class MutableClock : IClock
{
    private DateTimeOffset _now;

    /// <summary>创建测试时钟。</summary>
    /// <param name="start">起始时刻；为 <see langword="null"/> 时使用固定默认值。</param>
    public MutableClock(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _now;

    /// <summary>推进时钟。</summary>
    /// <param name="delta">推进量。</param>
    public void Advance(TimeSpan delta) => _now += delta;
}
