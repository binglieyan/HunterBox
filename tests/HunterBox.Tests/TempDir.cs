namespace HunterBox.Tests;

/// <summary>测试用临时目录，Dispose 时递归删除。</summary>
internal sealed class TempDir : IDisposable
{
    private TempDir(string path) => Path = path;

    public string Path { get; }

    public static TempDir Create()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "hunterbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    /// <summary>创建子目录并返回绝对路径。</summary>
    public string Dir(params string[] parts)
    {
        string path = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>写入一个文件（自动建父目录）并返回绝对路径。</summary>
    public string File(string relativePath, string content = "x")
    {
        string path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // ACL/只读用例留下的文件：清只读后重试一次，清理失败不影响测试结论。
            TryClearReadOnly(Path);
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    /// <summary>尽力清除目录树的只读属性；失败忽略。</summary>
    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.SetAttributes(path, FileAttributes.Normal);
                return;
            }

            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 属性清理失败由后续删除结果体现。
        }
    }
}
