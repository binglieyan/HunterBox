using HunterBox.Infrastructure.IO;

namespace HunterBox.Tests;

/// <summary>损坏文件隔离命名、保留份数与端口化删除。</summary>
public class CorruptFileQuarantineTests
{
    /// <summary>keep 不在枚举结果时也计入保留名额，目录内备份数不会超过上限。</summary>
    [Fact]
    public void Prune_KeepNotEnumerated_StillRespectsLimit()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "index.json");
        for (int index = 0; index < 4; index++)
        {
            File.WriteAllText($"{path}.bad-2026010100000{index}", "old");
        }

        string keep = Path.Combine(temp.Path, "index.json.bad-20260101000009");
        CorruptFileQuarantine.Prune(
            path,
            SystemFileSystem.Instance,
            keep,
            _ => { });

        Assert.Equal(
            CorruptFileQuarantine.DefaultMaxCopies - 1,
            Directory.GetFiles(temp.Path, "index.json.bad-*").Length);
    }

    /// <summary>没有 keep 时按文件名时间戳保留最新几份。</summary>
    [Fact]
    public void Prune_WithoutKeep_KeepsNewestByFileName()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "index.json");
        for (int index = 0; index < 5; index++)
        {
            File.WriteAllText($"{path}.bad-2026010100000{index}", "old");
        }

        CorruptFileQuarantine.Prune(
            path,
            SystemFileSystem.Instance,
            keep: null,
            _ => { });

        string[] copies = Directory.GetFiles(temp.Path, "index.json.bad-*");
        Assert.Equal(CorruptFileQuarantine.DefaultMaxCopies, copies.Length);
        Assert.Contains(copies, copy => copy.EndsWith("20260101000004", StringComparison.Ordinal));
        Assert.DoesNotContain(copies, copy => copy.EndsWith("20260101000000", StringComparison.Ordinal));
    }
}
