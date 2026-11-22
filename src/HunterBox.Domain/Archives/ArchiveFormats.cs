namespace HunterBox.Domain.Archives;

/// <summary>压缩格式（按内容魔数探测，不依赖扩展名）。</summary>
public enum ArchiveFormat
{
    /// <summary>无法识别的格式。</summary>
    Unknown,

    /// <summary>ZIP。</summary>
    Zip,

    /// <summary>7z。</summary>
    SevenZip,

    /// <summary>RAR。</summary>
    Rar,

    /// <summary>TAR（含 tar.gz/tar.bz2 解出的内层）。</summary>
    Tar,

    /// <summary>GZip。</summary>
    GZip,

    /// <summary>BZip2。</summary>
    BZip2,

    /// <summary>XZ/LZMA。</summary>
    Xz,

    /// <summary>LZip。</summary>
    LZip,

    /// <summary>Zstandard。</summary>
    Zstandard,
}

/// <summary>解压进度快照。</summary>
/// <param name="EntryPath">当前解压条目（可空）。</param>
/// <param name="BytesTransferred">已传输字节数。</param>
/// <param name="TotalBytes">总字节数（未知时为空）。</param>
/// <param name="Percent">进度百分比（0–100）。</param>
public sealed record ArchiveProgress(string? EntryPath, long BytesTransferred, long? TotalBytes, int Percent);

/// <summary>解压选项：密码、进度回调、取消与嵌套解压。</summary>
public sealed record ArchiveExtractionOptions
{
    /// <summary>解压密码；为 <see langword="null"/> 时按文件名里的「解压码/密码」自动猜测。</summary>
    public string? Password { get; init; }

    /// <summary>进度回调（解压/复制条目时调用）。</summary>
    public IProgress<ArchiveProgress>? Progress { get; init; }

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>是否递归解压包内的嵌套压缩包（安装包解析流程）。</summary>
    public bool ExtractNestedArchives { get; init; } = true;

    /// <summary>嵌套解压最大深度，防御压缩炸弹。</summary>
    public int MaxNestedDepth { get; init; } = 3;

    /// <summary>单个解压条目的最大字节数（默认 8 GiB），超过即中止，防御压缩炸弹（C-13）。</summary>
    public long MaxEntryBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>整个压缩包的解压总量上限（默认 32 GiB），超过即中止，防御压缩炸弹（C-13）。</summary>
    public long MaxTotalBytes { get; init; } = 32L * 1024 * 1024 * 1024;
}

/// <summary>按内容魔数探测压缩格式，并从文件名猜测国内常见「解压码/密码」。</summary>
public static class ArchiveFormatDetector
{
    /// <summary>按文件头字节探测压缩格式（纯规则，不读文件）。</summary>
    /// <param name="data">文件开头的一段字节（通常 512 字节以内即可）。</param>
    /// <returns>识别出的格式；不足 4 字节或不是已知压缩格式时为 <see cref="ArchiveFormat.Unknown"/>。</returns>
    /// <remarks>文件读取在 <c>HunterBox.Application.Common.ArchiveFileProbe</c>，Domain 不做 IO（A-03）。</remarks>
    public static ArchiveFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return ArchiveFormat.Unknown;
        }

        if (data[0] == 0x50 && data[1] == 0x4B
            && (data[2] == 0x03 || data[2] == 0x05 || data[2] == 0x07)
            && (data[3] == 0x04 || data[3] == 0x06 || data[3] == 0x08))
        {
            return ArchiveFormat.Zip;
        }

        if (StartsWith(data, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return ArchiveFormat.SevenZip;
        }

        if (StartsWith(data, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07])
            && data.Length >= 7
            && (data[6] == 0x00 || data[6] == 0x01))
        {
            return ArchiveFormat.Rar;
        }

        if (StartsWith(data, [0x1F, 0x8B]))
        {
            return ArchiveFormat.GZip;
        }

        if (StartsWith(data, [0x42, 0x5A, 0x68]))
        {
            return ArchiveFormat.BZip2;
        }

        if (StartsWith(data, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
        {
            return ArchiveFormat.Xz;
        }

        if (StartsWith(data, [0x4C, 0x5A, 0x49, 0x50]))
        {
            return ArchiveFormat.LZip;
        }

        if (StartsWith(data, [0x28, 0xB5, 0x2F, 0xFD]))
        {
            return ArchiveFormat.Zstandard;
        }

        // TAR：ustar 标记位于偏移 257。
        if (data.Length >= 262 && data.Slice(257, 5).SequenceEqual("ustar"u8))
        {
            return ArchiveFormat.Tar;
        }

        return ArchiveFormat.Unknown;
    }

    /// <summary>
    /// 从文件名猜测国内压缩包常用的解压密码（文件名密码推断）。
    /// 支持「xxx解压码123.zip」「xxx密码abc(1).zip」等形式。
    /// </summary>
    /// <param name="fileName">文件名（可含扩展名）。</param>
    /// <returns>猜测到的密码；无法推断时为 <see langword="null"/>。</returns>
    public static string? GuessPasswordFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string name = fileName.Contains('.')
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
        name = name.Replace(":", string.Empty).Replace("：", string.Empty);

        string keyword = "解压码";
        int index = name.LastIndexOf(keyword, StringComparison.Ordinal);
        if (index < 0)
        {
            keyword = "密码";
            index = name.LastIndexOf(keyword, StringComparison.Ordinal);
        }

        if (index < 0)
        {
            return null;
        }

        // 关键字位于文件名开头也合法：整名就是「关键字 + 密码」。
        string password = name[(index + keyword.Length)..];
        int leftParenthesis = password.LastIndexOf('(');
        int rightParenthesis = password.LastIndexOf(')');
        if (leftParenthesis > 0 && rightParenthesis > leftParenthesis)
        {
            password = password[..leftParenthesis];
        }

        password = password.Trim();
        return password.Length == 0 ? null : password;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);
}
