namespace HunterBox.Tests;

/// <summary>临时目录清理遇到只读文件时不得把清理失败冒成测试失败。</summary>
public class TempDirTests
{
    [Fact]
    public void Dispose_DirectoryWithReadOnlyFile_DoesNotThrow()
    {
        TempDir temp = TempDir.Create();
        string file = temp.File("readonly.txt", "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            temp.Dispose();
        }
        finally
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }

        Assert.False(Directory.Exists(temp.Path), "清理后临时目录不应残留");
    }
}
