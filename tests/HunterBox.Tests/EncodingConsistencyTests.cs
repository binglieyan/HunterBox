namespace HunterBox.Tests;

/// <summary>
/// 源码、Markdown 文档、工程、XAML、解决方案与脚本统一使用 UTF-8 BOM
/// （范围见 <see cref="TextExtensions"/>）。
/// </summary>
public class EncodingConsistencyTests
{
    /// <summary>
    /// 需要 BOM 的文本文件扩展名：与行尾门禁共用
    /// <see cref="TextFileExtensions"/>。运行时数据（<c>.json</c>）与 <c>.editorconfig</c> 明确不在范围内：
    /// 前者由 JsonSerializer 生成/读取，不应依赖 BOM；后者由编辑器工具链解析，保持无 BOM 的纯文本。
    /// </summary>
    /// <param name="root">仓库根目录。</param>
    /// <returns>声明扩展名。</returns>
    private static string[] TextExtensions(string root) => TextFileExtensions.Load(root);

    /// <summary>仓库内源码、文档、工程、XAML 与脚本都必须带 UTF-8 BOM。</summary>
    [SourceTreeFact]
    public void SourceAndTextFiles_UseUtf8Bom()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string[] extensions = TextExtensions(root);
        List<string> offenders = [];
        foreach (string file in SourceTreeLocator.EnumerateRepositoryFiles())
        {
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] head = new byte[3];
            int read;
            using (FileStream stream = File.OpenRead(file))
            {
                read = stream.Read(head, 0, head.Length);
            }

            if (read < 3 || head[0] != 0xEF || head[1] != 0xBB || head[2] != 0xBF)
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下文件缺少 UTF-8 BOM（范围：" + string.Join(", ", extensions) + "）："
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
