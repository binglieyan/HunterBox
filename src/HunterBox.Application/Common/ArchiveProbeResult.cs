using HunterBox.Domain.Archives;

namespace HunterBox.Application.Common;

/// <summary>压缩格式探测结果：格式与读取失败原因。</summary>
/// <param name="Format">识别出的格式；不可读或不是已知格式时为 <see cref="ArchiveFormat.Unknown"/>。</param>
/// <param name="Error">读取失败原因；文件可读时为 <see langword="null"/>。</param>
public sealed record ArchiveProbeResult(ArchiveFormat Format, Exception? Error)
{
    /// <summary>文件是否可读（不存在、被占用或权限不足时为 <see langword="false"/>）。</summary>
    public bool Readable => Error is null;
}
