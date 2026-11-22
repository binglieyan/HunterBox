namespace HunterBox.Application.Common;

/// <summary>跨模块共享的格式常量（避免同一格式串在多个服务里各写一份）。</summary>
public static class Formats
{
    /// <summary>紧凑本地时间戳格式（存档备份目录名、清理隔离目录名、崩溃日志文件名）。</summary>
    public const string CompactTimestamp = "yyyyMMdd-HHmmss";
}
