using System.IO.Compression;

namespace HunterBox.Tests;

/// <summary>
/// A-08：构造测试 ZIP 的共享工具（原先藏在 <c>ArchiveExtractorTests.cs</c> 文件里，
/// 被 10+ 个测试文件引用）。保持 <c>internal</c> + 根命名空间，可见性与调用点不变。
/// </summary>
internal static class ZipBuilder
{
    /// <summary>在 <paramref name="directory"/> 下创建 <paramref name="fileName"/> 并写入文本条目。</summary>
    public static string Create(
        string directory,
        string fileName,
        params (string Path, string Content)[] entries)
    {
        string zipPath = Path.Combine(directory, fileName);
        using FileStream file = File.Create(zipPath);
        using ZipArchive zip = new(file, ZipArchiveMode.Create);
        foreach ((string path, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }

        return zipPath;
    }

    /// <summary>按完整路径创建 ZIP（父目录自动创建），供调用方已持有完整路径的测试使用。</summary>
    public static string CreateAt(string zipPath, params (string Path, string Content)[] entries)
    {
        string? directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return Create(directory ?? string.Empty, Path.GetFileName(zipPath), entries);
    }
}
