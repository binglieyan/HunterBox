using System.Globalization;

namespace HunterBox.Application.Saves;

/// <summary>存档备份的名称与大小格式化：时间戳解析、备份名生成、界面可读体积（A-11）。</summary>
internal static class SaveBackupFormatter
{
    /// <summary>备份目录名的时间戳格式。</summary>
    internal const string TimestampFormat = Common.Formats.CompactTimestamp;

    /// <summary>把时间戳格式的目录名解析成本地时间；自定义名称返回 <see langword="false"/>。</summary>
    /// <param name="name">备份目录名。</param>
    /// <param name="createdAt">解析结果。</param>
    /// <returns>名称符合 <c>yyyyMMdd-HHmmss</c> 时返回 <see langword="true"/>。</returns>
    internal static bool TryParseTimestamp(string name, out DateTimeOffset createdAt)
    {
        createdAt = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            name,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out createdAt);
    }

    /// <summary>把字节数格式化成界面用的「x.x KB / x.x MB / x.x GB / xx B」。</summary>
    /// <param name="bytes">字节数，负数按 0 处理。</param>
    /// <returns>带单位的可读文本。</returns>
    internal static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const long Kb = 1024;
        const long Mb = Kb * 1024;
        const long Gb = Mb * 1024;
        return bytes switch
        {
            >= Gb => (bytes / (double)Gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            >= Mb => (bytes / (double)Mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            >= Kb => (bytes / (double)Kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        };
    }
}
