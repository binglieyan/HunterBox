using System.Diagnostics;
using HunterBox.Application.Models;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Models;

namespace HunterBox.Tests;

/// <summary>模型文本缓存可跨会话持久化，损坏文件按空缓存处理。</summary>
public class JsonModelTextCacheStoreTests
{
    /// <summary>「大量变化」用例规模：只表示批量大小，不再是压缩阈值。</summary>
    private const int LargeChangeCount = 2000;

    /// <summary>「大量小删除」用例规模：只表示批量大小，不再是压缩阈值。</summary>
    private const int ManySmallEntryCount = 2001;

    [Fact]
    public void SaveThenLoad_RoundTripsEntries()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        JsonModelTextCacheStore store = new(path, new SilentLogger());

        store.Save([new ModelTextCacheEntry(1, "sig", "文本", "mod.pak|10|638000000000000000")]);

        ModelTextCacheEntry entry = Assert.Single(Load(new JsonModelTextCacheStore(path, new SilentLogger())));
        Assert.Equal(1, entry.ModId);
        Assert.Equal("sig", entry.Signature);
        Assert.Equal("文本", entry.Text);
        Assert.Equal("mod.pak|10|638000000000000000", entry.PakStamp);
        Assert.False(File.Exists(path + ".journal"), "首次全量写入不应留下增量日志");
    }

    /// <summary>装载后再次保存只追加变化条目，日志远小于基础文件。</summary>
    [Fact]
    public void Save_AfterLoad_AppendsOnlyChanges()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        JsonModelTextCacheStore seed = new(path, new SilentLogger());
        List<ModelTextCacheEntry> entries = [];
        for (int id = 1; id <= 200; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", new string('x', 512), "stamp", "mhws"));
        }

        Assert.True(seed.Save(entries));
        long baseLength = new FileInfo(path).Length;

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out IReadOnlyList<ModelTextCacheEntry> loaded));
        Assert.Equal(entries.Count, loaded.Count);
        entries[0] = entries[0] with { Text = "改过的文本" };
        Assert.True(incremental.Save(entries));

        string journal = path + ".journal";
        Assert.True(File.Exists(journal));
        long journalLength = new FileInfo(journal).Length;
        Assert.True(journalLength < baseLength / 10, $"增量日志 {journalLength} 字节，基础文件 {baseLength} 字节");

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Contains(reloaded, entry => entry.ModId == 1 && entry.Text == "改过的文本");
    }

    /// <summary>删除条目写入增量日志，重新装载后不再出现。</summary>
    [Fact]
    public void Save_Deletion_IsAppliedOnReload()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first, second]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save([first]));

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        ModelTextCacheEntry only = Assert.Single(reloaded);
        Assert.Equal(1, only.ModId);
    }

    /// <summary>本次保存的变更总量实际序列化字节超过压缩阈值时直接重写基础文件。</summary>
    [Fact]
    public void Save_ManyLargeChanges_CompactsIntoBase()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .Save([new ModelTextCacheEntry(1, "sig", "一", "stamp", "mhws")]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        string text = new('文', 1024);
        List<ModelTextCacheEntry> entries = [];
        for (int id = 0; id <= LargeChangeCount; id++)
        {
            entries.Add(new ModelTextCacheEntry(id + 10, "sig", text, "stamp", "mhws"));
        }

        Assert.True(incremental.Save(entries));

        Assert.False(File.Exists(path + ".journal"), "压缩后不应保留增量日志");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>大量小体积删除不应触发整份重写，基础文件保持原样、变化写入日志。</summary>
    [Fact]
    public void Save_ManySmallDeletions_AppendsJournalInsteadOfRewritingBase()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        List<ModelTextCacheEntry> entries = [];
        for (int id = 1; id <= ManySmallEntryCount; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", "文本", "stamp", "mhws"));
        }

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save(entries));
        byte[] baseBefore = File.ReadAllBytes(path);

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save([]));

        Assert.True(File.Exists(path + ".journal"), "小体积删除应写入增量日志");
        Assert.Equal(baseBefore, File.ReadAllBytes(path));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Empty(reloaded);
    }

    /// <summary>增量日志损坏时回退到基础文件并隔离日志，不影响启动。</summary>
    [Fact]
    public void TryLoad_CorruptJournal_FallsBackToBase()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry baseEntry = new(1, "sig", "基础", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([baseEntry]));
        File.WriteAllText(path + ".journal", "{ 坏日志");

        bool ok = new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        ModelTextCacheEntry loaded = Assert.Single(entries);
        Assert.Equal("基础", loaded.Text);
        // 写基础成功后日志可直接删除/隔离，不再要求保留 .bad-* 副本。
        Assert.False(File.Exists(path + ".journal"));
    }

    /// <summary>没有有效基础文件时增量日志无法解释，隔离后按空缓存启动。</summary>
    [Fact]
    public void TryLoad_JournalWithoutBase_IsIgnored()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(
            path + ".journal",
            """{"Version":2,"Changes":[{"ModId":1,"GameId":"mhws","Entry":{"ModId":1,"Signature":"sig","Text":"孤立","PakStamp":"","GameId":"mhws"},"Deleted":false}]}""");

        bool ok = new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        Assert.Empty(entries);
        Assert.False(File.Exists(path + ".journal"));
    }

    /// <summary>日志末尾半截记录只丢弃尾部，已写入的变化仍然生效。</summary>
    [Fact]
    public void TryLoad_TruncatedJournalTail_AppliesValidPrefix()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .Save([new ModelTextCacheEntry(1, "sig", "一", "stamp", "mhws")]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save(
        [
            new ModelTextCacheEntry(1, "sig", "一", "stamp", "mhws"),
            new ModelTextCacheEntry(2, "sig", "二", "stamp", "mhws"),
        ]));
        File.AppendAllText(path + ".journal", "{\"Version\":2,\"Changes\":[");

        bool ok = new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        Assert.Equal(2, entries.Count);
    }

    /// <summary>缓存文件带版本号，且写入不残留临时文件。</summary>
    [Fact]
    public void Save_WritesVersionedFileAtomically()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        JsonModelTextCacheStore store = new(path, new SilentLogger());

        store.Save([new ModelTextCacheEntry(1, "sig", "文本", "stamp")]);
        store.Save([new ModelTextCacheEntry(2, "sig2", "文本2", "stamp2")]);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonModelTextCacheStore.CurrentVersion, document.RootElement.GetProperty("Version").GetInt32());
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
    }

    /// <summary>中文按 UTF-8 原样写入，不再被转义成 \uXXXX 放大缓存体积。</summary>
    [Fact]
    public void Save_NonAsciiText_IsNotUnicodeEscaped()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        JsonModelTextCacheStore store = new(path, new SilentLogger());

        store.Save([new ModelTextCacheEntry(1, "sig", "公会骑士模型", "", "mhws")]);

        string json = File.ReadAllText(path);
        Assert.Contains("公会骑士模型", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u516C", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>旧数组格式（无版本）按空缓存处理，不抛异常。</summary>
    [Fact]
    public void Load_LegacyArrayFormat_ReturnsEmptyWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(path, """[{"ModId":1,"Signature":"sig","Text":"文本"}]""");
        SilentLogger logger = new();

        IReadOnlyList<ModelTextCacheEntry> entries = Load(new JsonModelTextCacheStore(path, logger));

        Assert.Empty(entries);
        Assert.NotEmpty(logger.Warnings);
    }

    /// <summary>版本不匹配时整份缓存作废，避免用过期结构。</summary>
    [Fact]
    public void Load_UnsupportedVersion_ReturnsEmpty()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(path, """{"Version":99,"Entries":[]}""");

        Assert.Empty(Load(new JsonModelTextCacheStore(path, new SilentLogger())));
    }

    /// <summary>版本 21 不能被当作版本 2 的前缀命中。</summary>
    [Fact]
    public void Load_VersionTwentyOne_ReturnsEmpty()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(path, """{"Version":21,"Entries":[]}""");

        Assert.Empty(Load(new JsonModelTextCacheStore(path, new SilentLogger())));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(path, "{ not json");
        SilentLogger logger = new();

        IReadOnlyList<ModelTextCacheEntry> entries = Load(new JsonModelTextCacheStore(path, logger));

        Assert.Empty(entries);
        Assert.NotEmpty(logger.Warnings);
    }

    /// <summary>合法但超过 32MB 的缓存必须正常装载，不能被误判为损坏而隔离。</summary>
    [Fact]
    public void TryLoad_LegitimateCacheOver32Mb_LoadsWithoutQuarantine()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string hugeText = new('x', 33 * 1024 * 1024);
        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.Save([new ModelTextCacheEntry(1, "sig", hugeText, "stamp", "mhws")]));

        bool ok = store.TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        ModelTextCacheEntry entry = Assert.Single(entries);
        Assert.Equal(1, entry.ModId);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bad-*"));
    }

    /// <summary>连续损坏缓存只保留最近 N 份 .bad-*，不会无限增长。</summary>
    [Fact]
    public void TryLoad_RepeatedCorruption_KeepsLimitedBadCopies()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        JsonModelTextCacheStore store = new(path, new SilentLogger());

        for (int round = 0; round < CorruptFileQuarantine.DefaultMaxCopies + 2; round++)
        {
            File.WriteAllText(path, "{ not json");
            Assert.True(store.TryLoad(out _));
        }

        Assert.True(
            Directory.GetFiles(temp.Path, "*.bad-*").Length <= CorruptFileQuarantine.DefaultMaxCopies,
            "隔离文件数量超过上限");
    }

    /// <summary>超限缓存改名保留为 .bad-*，本次允许重新生成并在下次启动命中。</summary>
    [Fact]
    public void TryLoad_FileTooLarge_QuarantinesAndAllowsRewrite()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(JsonModelTextCacheStore.MaxFileBytes + 1L);
        }

        SilentLogger logger = new();
        JsonModelTextCacheStore store = new(path, logger);
        bool ok = store.TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        Assert.Empty(entries);
        Assert.Single(Directory.GetFiles(temp.Path, "*.bad-*"));

        Assert.True(store.Save([new ModelTextCacheEntry(7, "sig", "文本", "stamp", "mhws")]));
        Assert.Single(Load(new JsonModelTextCacheStore(path, new SilentLogger())));
    }

    /// <summary>损坏文件改名保留并允许重写；日志说明保留位置。</summary>
    [Fact]
    public void TryLoad_CorruptFile_QuarantinesAndAllowsRewrite()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(path, "{ not json");
        SilentLogger logger = new();
        JsonModelTextCacheStore store = new(path, logger);

        bool ok = store.TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        Assert.Empty(entries);
        Assert.Single(Directory.GetFiles(temp.Path, "*.bad-*"));
        Assert.Contains(logger.Warnings, warning => warning.Contains("重新生成", StringComparison.Ordinal));

        Assert.True(store.Save([new ModelTextCacheEntry(1, "sig", "文本", "stamp", "mhws")]));
        Assert.Single(Load(new JsonModelTextCacheStore(path, new SilentLogger())));
    }

    /// <summary>文件被占用、改名也失败时才禁用本会话写盘。</summary>
    [Fact]
    public void TryLoad_LockedFile_QuarantineFailsAndReturnsFalse()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(path, "{ not json");
        using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        bool ok = new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.False(ok);
        Assert.Empty(entries);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bad-*"));
    }

    /// <summary>没有缓存文件是正常状态，允许随后写入新缓存。</summary>
    [Fact]
    public void TryLoad_MissingFile_ReturnsTrueEmpty()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        SilentLogger logger = new();

        bool ok = new JsonModelTextCacheStore(path, logger)
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries);

        Assert.True(ok);
        Assert.Empty(entries);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>启动时清理历史遗留的多余 .bad-*，按文件名时间戳只保留最新 3 份。</summary>
    [Fact]
    public void TryLoad_StartupPrunesStaleBadCopies()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        for (int index = 0; index < 5; index++)
        {
            File.WriteAllText($"{path}.bad-2026010100000{index}", "old");
        }

        bool ok = new JsonModelTextCacheStore(path, new SilentLogger()).TryLoad(out _);

        Assert.True(ok);
        string[] copies = Directory.GetFiles(temp.Path, "*.bad-*");
        Assert.Equal(CorruptFileQuarantine.DefaultMaxCopies, copies.Length);
        Assert.Contains(copies, copy => copy.EndsWith("20260101000004", StringComparison.Ordinal));
        Assert.DoesNotContain(copies, copy => copy.EndsWith("20260101000000", StringComparison.Ordinal));
    }

    /// <summary>启动时一次枚举清理基础文件与增量日志的多余 .bad-*，各自只保留最新 3 份。</summary>
    [Fact]
    public void TryLoad_StartupPrunesBaseAndJournalBadCopiesInOnePass()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        for (int index = 0; index < 5; index++)
        {
            File.WriteAllText($"{path}.bad-2026010100000{index}", "old");
            File.WriteAllText($"{journal}.bad-2026010100001{index}", "old");
        }

        bool ok = new JsonModelTextCacheStore(path, new SilentLogger()).TryLoad(out _);

        Assert.True(ok);
        Assert.Equal(
            CorruptFileQuarantine.DefaultMaxCopies,
            Directory.GetFiles(temp.Path, "model-text-cache.json.bad-*").Length);
        Assert.Equal(
            CorruptFileQuarantine.DefaultMaxCopies,
            Directory.GetFiles(temp.Path, "model-text-cache.json.journal.bad-*").Length);
        Assert.Contains(
            Directory.GetFiles(temp.Path, "model-text-cache.json.journal.bad-*"),
            copy => copy.EndsWith("20260101000014", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Directory.GetFiles(temp.Path, "model-text-cache.json.journal.bad-*"),
            copy => copy.EndsWith("20260101000010", StringComparison.Ordinal));
    }

    /// <summary>刚隔离的文件即使保留了旧 mtime 也一定保留，不会被同一次清理删掉。</summary>
    [Fact]
    public void TryLoad_FreshQuarantine_KeepsJustMovedFile()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        for (int index = 0; index < CorruptFileQuarantine.DefaultMaxCopies; index++)
        {
            string old = $"{path}.bad-2099010100000{index}";
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-10));
        }

        File.WriteAllText(path, "{ not json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddYears(-10));

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).TryLoad(out _));

        string[] copies = Directory.GetFiles(temp.Path, "*.bad-*");
        Assert.Equal(CorruptFileQuarantine.DefaultMaxCopies, copies.Length);
        Assert.Contains(copies, copy => !Path.GetFileName(copy).Contains("2099", StringComparison.Ordinal));
    }

    /// <summary>5000 条最坏文本 + 最大内容戳的实际 JSON 体积仍低于损坏判据。</summary>
    [Fact]
    public void Save_FiveThousandMaxTexts_StaysWellBelowCorruptLimit()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        // 控制字符在 JSON 中恒为 \uXXXX（6 字节/字符），是文本与内容戳字段的最坏转义。
        string text = ModelTextPrefetchService.ClampText(
            new string('\u0001', ModelTextPrefetchService.MaxTextLength));
        string stamp = new('\u0001', ModelTextPrefetchService.MaxStampLength);
        List<ModelTextCacheEntry> entries = new(ModelTextPrefetchService.MaxTextCacheCapacity);
        for (int id = 1; id <= ModelTextPrefetchService.MaxTextCacheCapacity; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", text, stamp, "mhws"));
        }

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save(entries));

        long length = new FileInfo(path).Length;
        Assert.True(length < 96L * 1024 * 1024, $"实际体积 {length} 字节，与 77MB 论证不符");
    }

    /// <summary>损坏日志的合法前缀立即下沉到基础文件，重启后删除项不复活。</summary>
    [Fact]
    public void TryLoad_CorruptJournal_PersistsAppliedPrefix()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first, second]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save([first]));
        File.AppendAllText(path + ".journal", "{\"Version\":3,\"Changes\":[");

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        ModelTextCacheEntry only = Assert.Single(entries);
        Assert.Equal(1, only.ModId);

        // 再启动一次：基础文件已被下沉，删除项不会复活。
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(1, Assert.Single(reloaded).ModId);
    }

    /// <summary>基础文件世代变化后旧日志不回放。</summary>
    [Fact]
    public void TryLoad_GenerationMismatch_IgnoresJournal()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save(
        [
            first,
            new ModelTextCacheEntry(2, "sig", "二", "stamp", "mhws"),
        ]));
        Assert.True(File.Exists(path + ".journal"));

        // 模拟外部工具重写基础文件（新世代、空内容）。
        File.WriteAllText(path, """{"Version":3,"Generation":"external","Entries":[]}""");

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        Assert.Empty(entries);
        Assert.False(File.Exists(path + ".journal"));
    }

    /// <summary>日志删除失败后下一次保存重试，不会残留无限增长。</summary>
    [Fact]
    public void Save_JournalDeleteFails_RetriesAfterBackoff()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        MutableClock clock = new();
        JsonModelTextCacheStore store = new(path, new SilentLogger(), clock);
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries =
        [
            first,
            new ModelTextCacheEntry(2, "sig", "二", "stamp", "mhws"),
        ];

        using (FileStream locked = new(
            path + ".journal",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(store.Save(entries));
        }

        Assert.True(File.Exists(path + ".journal"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(store.Save(entries));
        Assert.False(File.Exists(path + ".journal"));
    }

    /// <summary>压缩失败按退避跳过整份重写，恢复后下一次保存完成压缩。</summary>
    [Fact]
    public void Save_CompactionFails_BacksOffThenRecovers()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", new string('x', 1024), "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        MutableClock clock = new();
        // 注入单调时钟与等待委托，避免整份写节流造成真实 1 秒休眠。
        long tick = 0;
        List<TimeSpan> waits = [];
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock,
            tickSource: () => tick,
            delay: wait =>
            {
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            });
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = CreateLargeEntries(new string('x', 1024));

        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // 日志已超 1MB 压缩阈值；基础文件被占用导致 WriteBase 失败，但数据已在日志中。
            Assert.True(store.Save(entries));
        }

        Assert.True(File.Exists(path + ".journal"));
        clock.Advance(TimeSpan.FromSeconds(31));
        waits.Clear();
        Assert.True(store.Save(entries));
        // 零变化但日志超过压缩阈值仍会整份写，并进入节流等待。
        Assert.Single(waits);
        Assert.True(waits[0].TotalMilliseconds > 0, "零变化压缩也应等待整份写间隔");
        Assert.False(File.Exists(path + ".journal"));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>大批量变化按 100 条分块写入，单行大小有上限。</summary>
    [Fact]
    public void Save_ManyChanges_WritesChunkedJournalLines()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = [seed];
        for (int id = 100; id < 350; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", "文本", "stamp", "mhws"));
        }

        Assert.True(store.Save(entries));

        string[] lines = File.ReadAllLines(path + ".journal");
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.True(line.Length < 64 * 1024, "单行日志超过上限"));
        // 按 UTF-8 实际字节校验单行 256KB 上界。
        Assert.All(
            lines,
            line => Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(line) <= 256 * 1024,
                "单行日志超过 256KB 上界"));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>非 ASCII 内容戳按 UTF-8 原样往返，不被误哈希或转义丢失。</summary>
    [Fact]
    public void Save_ChineseStamp_RoundTrips()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string stamp = new('中', 300);
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save(
            [new ModelTextCacheEntry(1, "sig", "中文文本", stamp, "mhws")]));

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        ModelTextCacheEntry entry = Assert.Single(entries);
        Assert.Equal(stamp, entry.PakStamp);
        Assert.Equal("中文文本", entry.Text);
    }

    /// <summary>绕过 Application 直接写入超长文本时，存储端兜底收紧。</summary>
    [Fact]
    public void Save_OverlongText_IsClampedByStore()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save(
        [
            new ModelTextCacheEntry(
                1,
                "sig",
                new string('x', ModelTextPrefetchService.MaxTextLength + 100),
                "stamp",
                "mhws"),
        ]));

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        ModelTextCacheEntry entry = Assert.Single(entries);
        Assert.True(entry.Text.Length <= ModelTextPrefetchService.MaxTextLength);
    }

    /// <summary>缓存 JSON 的字符串字段为 null 或混入 null 元素时，加载归一为空串而不是抛 NRE。</summary>
    [Fact]
    public void TryLoad_NullStringFields_AreNormalized()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        File.WriteAllText(
            path,
            """{"Version":3,"Generation":"g","Entries":[{"ModId":1,"Signature":null,"Text":null,"PakStamp":null,"GameId":"mhws"},null]}""");

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        ModelTextCacheEntry entry = Assert.Single(entries);
        Assert.Equal(string.Empty, entry.Signature);
        Assert.Equal(string.Empty, entry.Text);
        Assert.Equal(string.Empty, entry.PakStamp);

        // 进入 Application 缓存后，PakStamp 为空串而不是 null，TryGetPakStamp 不会抛 NRE。
        ModelTextCache cache = new(8);
        cache.Load(entries);
        Assert.False(cache.TryGetPakStamp(1, "mhws", out _));
        Assert.True(cache.TryGet(1, string.Empty, "mhws", out string text));
        Assert.Equal(string.Empty, text);
    }

    /// <summary>混版本日志只跳过对应行，当前版本行仍然生效。</summary>
    [Fact]
    public void TryLoad_MixedVersionJournal_SkipsOnlyBadLine()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save([first, second]));

        string journal = path + ".journal";
        string current = File.ReadAllText(journal);
        File.WriteAllText(
            journal,
            """{"Version":2,"Generation":"old","Changes":[{"ModId":1,"GameId":"mhws","Entry":null,"Deleted":true}]}"""
            + Environment.NewLine
            + current);

        SilentLogger logger = new();
        Assert.True(new JsonModelTextCacheStore(path, logger)
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.ModId == 2);
        // 混版本行只汇总一条告警。
        string warning = Assert.Single(logger.Warnings, item => item.Contains("不完整/过期", StringComparison.Ordinal));
        Assert.Contains("1 行版本不匹配", warning, StringComparison.Ordinal);
    }

    /// <summary>实际序列化字节达到压缩阈值时直接整份写，避免先写日志再压缩。</summary>
    [Fact]
    public void Save_LargeAsciiBatch_WritesBaseOnce()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        string text = new('x', 600);
        List<ModelTextCacheEntry> entries = [seed];
        for (int id = 10; id < 2009; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", text, "stamp", "mhws"));
        }

        Assert.True(store.Save(entries));

        Assert.False(File.Exists(path + ".journal"), "达到字节阈值时不应先写日志再压缩");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>
    /// 400 条 × 500 个中文字符的实际字节低于压缩阈值，应只追加日志；
    /// 旧实现按最坏 6 字节/字符估算会误触发整份重写，本用例会失败。
    /// </summary>
    [Fact]
    public void Save_MediumChineseBatch_AppendsJournalWithoutFullRewrite()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        string text = new('中', 500);
        List<ModelTextCacheEntry> entries = [seed];
        for (int id = 10; id < 410; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", text, "stamp", "mhws"));
        }

        byte[] baseBefore = File.ReadAllBytes(path);
        Assert.True(store.Save(entries));

        Assert.True(File.Exists(path + ".journal"), "中等中文批次应只追加日志");
        Assert.Equal(baseBefore, File.ReadAllBytes(path));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>日志末尾缺换行时追加会把新行粘在残行后，必须改为整份重写。</summary>
    [Fact]
    public void Save_JournalWithoutTrailingNewline_RewritesBaseInsteadOfGlue()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry third = new(3, "sig", "三", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        Assert.True(store.Save([first, second]));

        using (FileStream stream = new(journal, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length - 1);
        }

        // 残缺尾行在序列化前就被识别，不应先序列化整批变化。
        int serializedBefore = store.JournalSerializeBatchCount;
        Assert.True(store.Save([first, second, third]));
        Assert.Equal(serializedBefore, store.JournalSerializeBatchCount);
        Assert.False(File.Exists(journal));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(3, reloaded.Count);
    }

    /// <summary>
    /// 容量由序列化后的精确字节判定；日志接近硬上限时尝试一个批次，
    /// 且序列化量不超过剩余容量。重新引入估算预检（slack 0 或 128）会让批次计数不变，本用例即可发现。
    /// </summary>
    [Fact]
    public void Save_JournalNearHardLimit_SerializesExactlyOneBatchThenRewritesBase()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));

        // 构造一个接近 32MB 且以换行结尾的日志；只写末字节，其余由文件长度占位。
        const long remaining = 10;
        using (FileStream stream = new(journal, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(JsonModelTextCacheStore.MaxJournalBytes - remaining);
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)'\n');
        }

        int serializedBefore = store.JournalSerializeBatchCount;
        Assert.True(store.Save([first, second]));
        // 断言「只尝试一次」而不是只看结果；恢复任何估算预检都会变成 0 批而失败。
        Assert.Equal(serializedBefore + 1, store.JournalSerializeBatchCount);
        // 剩余容量只有 10 字节，序列化阶段就应停止：这里断言「0 字节」，
        // 与第二个截断用例（部分分块已序列化但不落盘）互为补充。
        Assert.Equal(0, store.LastJournalSerializedBytes);
        // 截断批次绝不落盘，改走整份写，Save 不会假成功。
        Assert.Equal(1, store.JournalTruncatedBatchCount);
        Assert.False(File.Exists(journal));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(2, reloaded.Count);
    }

    /// <summary>
    /// 日志只剩数百 KB 时，单批序列化量按剩余容量截断，
    /// 不会先把上千条变化整批序列化再丢弃。
    /// </summary>
    [Fact]
    public void Save_JournalNearHardLimit_LimitsSerializedBytesToRemainingCapacity()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));

        const long remaining = 400 * 1024;
        using (FileStream stream = new(journal, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(JsonModelTextCacheStore.MaxJournalBytes - remaining);
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)'\n');
        }

        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('中', 64), 1400)];
        Assert.True(store.Save(entries));

        Assert.True(store.LastJournalSerializedBytes > 0, "应至少序列化一个分块后才因容量停止");
        Assert.InRange(store.LastJournalSerializedBytes, 1, remaining);
        // 截断批次整批不写入，改走整份写；日志不含半批数据，重载必须是完整快照。
        Assert.Equal(1, store.JournalTruncatedBatchCount);
        Assert.False(File.Exists(journal));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>
    /// 同一会话多次触发容量截断时，告警只输出一次，但计数按实际次数累加。
    /// </summary>
    [Fact]
    public void Save_JournalTruncatedTwice_WarnsOnlyOncePerSession()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        SilentLogger logger = new();
        JsonModelTextCacheStore store = new(path, logger);
        Assert.True(store.TryLoad(out _));

        static void CreateNearLimitJournal(string journalPath)
        {
            using FileStream stream = new(journalPath, FileMode.Create, FileAccess.Write);
            stream.SetLength(JsonModelTextCacheStore.MaxJournalBytes - (400 * 1024));
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)'\n');
        }

        List<ModelTextCacheEntry> first = [seed, .. CreateLargeEntries(new string('中', 64), 1400)];
        CreateNearLimitJournal(journal);
        Assert.True(store.Save(first));

        List<ModelTextCacheEntry> second = [seed, .. CreateLargeEntries(new string('文', 64), 1400)];
        CreateNearLimitJournal(journal);
        Assert.True(store.Save(second));

        Assert.Equal(2, store.JournalTruncatedBatchCount);
        Assert.Single(logger.Warnings, warning => warning.Contains("剩余容量不足", StringComparison.Ordinal));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(second.Count, reloaded.Count);
    }

    /// <summary>超长 Signature 在写入/加载时收紧为有界哈希，任意字段都有上界。</summary>
    [Fact]
    public void Save_OverlongSignature_IsBounded()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string signature = new('s', ModelTextPrefetchService.MaxStampLength * 4);

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .Save([new ModelTextCacheEntry(1, signature, "文本", "stamp", "mhws")]));

        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        ModelTextCacheEntry entry = Assert.Single(entries);
        Assert.True(entry.Signature.Length <= ModelTextPrefetchService.MaxStampLength);
        Assert.StartsWith("hash:", entry.Signature, StringComparison.Ordinal);
    }

    /// <summary>混世代日志只跳过旧行，当前世代行仍然生效。</summary>
    [Fact]
    public void TryLoad_MixedGenerationJournal_SkipsOnlyOldLine()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save([first, second]));

        string journal = path + ".journal";
        string current = File.ReadAllText(journal);
        File.WriteAllText(
            journal,
            """{"Version":3,"Generation":"old","Changes":[{"ModId":1,"GameId":"mhws","Entry":null,"Deleted":true}]}"""
            + Environment.NewLine
            + current);

        SilentLogger logger = new();
        Assert.True(new JsonModelTextCacheStore(path, logger)
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.ModId == 1);
        Assert.Contains(entries, entry => entry.ModId == 2);
        // 旧世代行只汇总一条告警。
        string warning = Assert.Single(logger.Warnings, item => item.Contains("不完整/过期", StringComparison.Ordinal));
        Assert.Contains("1 行旧世代", warning, StringComparison.Ordinal);
    }

    /// <summary>写基础失败且重写干净日志也失败时保留原日志，重启重放后删除项不复活。</summary>
    [Fact]
    public void TryLoad_RewriteJournalFails_KeepsJournalForReplay()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first, second]));

        JsonModelTextCacheStore incremental = new(path, new SilentLogger());
        Assert.True(incremental.TryLoad(out _));
        Assert.True(incremental.Save([first]));
        File.AppendAllText(journal, "{\"Version\":3,\"Changes\":[");
        // 只读属性阻止 File.Move 覆盖日志（重写失败），同时不影响读取与后续日志重放。
        File.SetAttributes(journal, FileAttributes.ReadOnly);

        using (FileStream lockedBase = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // 基础文件被共享读句柄占用，Move 覆盖失败；日志重写也失败。TryLoad 仍返回 true。
            Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).TryLoad(out _));
        }

        Assert.True(File.Exists(journal), "两级失败后原日志必须保留，不能隔离");

        File.SetAttributes(journal, FileAttributes.Normal);
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        ModelTextCacheEntry only = Assert.Single(entries);
        Assert.Equal(1, only.ModId);
    }

    /// <summary>追加失败后再保存不能删除仍含已确认变更的日志，首次追加的变更必须保留。</summary>
    [Fact]
    public void Save_AppendFailsThenRecovers_DoesNotLosePreviouslyAppendedChanges()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry third = new(3, "sig", "三", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        Assert.True(store.Save([first, second]));
        Assert.True(File.Exists(journal));

        using (FileStream lockedBase = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream lockedJournal = new(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.Save([first, second, third]));
        }

        Assert.True(store.Save([first, second, third]));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(3, reloaded.Count);
    }

    /// <summary>日志暂时删不掉时，后续保存不能反复整份重写基础文件。</summary>
    [Fact]
    public void Save_JournalCannotBeDeleted_DoesNotRewriteBaseEveryTime()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        MutableClock clock = new();
        // 注入单调时钟与等待委托，避免整份写节流造成真实 1 秒休眠。
        long tick = 0;
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock,
            tickSource: () => tick,
            delay: wait => tick += (long)wait.TotalMilliseconds);
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('x', 1024))];

        using (FileStream lockedBase = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(store.Save(entries));
        }

        clock.Advance(TimeSpan.FromSeconds(31));
        using (FileStream lockedJournal = new(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(store.Save(entries));
            byte[] baseAfterCompaction = File.ReadAllBytes(path);
            Assert.True(store.Save(entries));
            Assert.Equal(baseAfterCompaction, File.ReadAllBytes(path));
        }
    }

    /// <summary>
    /// 日志被独占且持续有变化时，每次保存只整份写一次并重新尝试追加；
    /// 清理待办期间的整份写节流使用可注入的单调时钟与等待委托，用例不真实休眠，并断言等待在锁外。
    /// </summary>
    [Fact]
    public void Save_JournalExclusivelyLocked_WithContinuousChanges_WritesBaseWithoutExtraAttempts()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry third = new(3, "sig", "三", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        MutableClock clock = new();
        SilentLogger logger = new();
        long tick = 0;
        List<TimeSpan> waits = [];
        object? gate = null;
        JsonModelTextCacheStore store = new(
            path,
            logger,
            clock,
            tickSource: () => tick,
            delay: wait =>
            {
                // 等待必须在 _gate 之外进行，否则并发的保存/加载会被最长 1 秒地阻塞。
                Assert.False(Monitor.IsEntered(gate!), "等待期间不应持有 _gate");
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            });
        gate = GetGate(store);
        Assert.True(store.TryLoad(out _));

        using (FileStream locked = new(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // 第一次有变化的保存：追加被独占句柄拒绝 → 整份写；三级清理全部失败 → 进入清理待办。
            Assert.True(store.Save([first, second]));
            Assert.Equal(1, store.BaseWriteAttempts);
            Assert.Equal(1, store.JournalAppendAttempts);
            Assert.Contains(
                logger.Warnings,
                warning => warning.Contains("无法删除/改名/截断", StringComparison.Ordinal));
            int warningsAfterFirst = logger.Warnings.Count;
            int diffsAfterFirst = store.ChangeDiffCount;

            // 第二次有变化的保存：锁外等待至间隔（断言等待时不持有 _gate），随后重新判定并只写一次；
            // 等待后的重试会再次尝试追加，因此追加次数为 3。
            // v1.5.175 及更早实现在清理待办期间会跳过追加（计数保持 1），本断言会失败。
            Assert.True(store.Save([first, second, third]));
            Assert.Equal(2, store.BaseWriteAttempts);
            Assert.Equal(3, store.JournalAppendAttempts);
            Assert.Equal(warningsAfterFirst, logger.Warnings.Count);
            // 等待后的重试复用缓存的差异，不重复全量 diff。
            // 差异与估算已合并为一趟，此计数同时覆盖估算扫描次数（+1）。
            Assert.Equal(diffsAfterFirst + 1, store.ChangeDiffCount);
        }

        Assert.Single(waits);
        Assert.Equal(1000, waits[0].TotalMilliseconds, 0);

        Assert.Equal(
            1,
            logger.Warnings.Count(warning => warning.Contains("追加模型文本缓存增量日志失败", StringComparison.Ordinal)));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(3, reloaded.Count);

        // 退避未到期：即使锁已释放也不清理，日志仍在。
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(store.Save([first, second, third]));
        Assert.True(File.Exists(journal));

        // 退避到期：无变化的保存也会重试清理并删除日志。
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(store.Save([first, second, third]));
        Assert.False(File.Exists(journal));
    }

    /// <summary>
    /// 清理待办期间追加成功的当前世代行在待办到期时不能被删除；
    /// 必须等下一次整份写下沉后才能清理，否则重启会丢失已返回成功的变更。
    /// </summary>
    [Fact]
    public void Save_CleanupPendingWithAppendedRows_KeepsJournalUntilBaseWrite()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry third = new(3, "sig", "三", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        MutableClock clock = new();
        JsonModelTextCacheStore store = new(path, new SilentLogger(), clock);
        Assert.True(store.TryLoad(out _));

        // 进入清理待办：日志被独占，整份写成功但三级清理全部失败。
        using (FileStream locked = new(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(store.Save([first, second]));
        }

        Assert.True(File.Exists(journal));

        // 锁已释放、退避尚未到期：追加成功，third 只存在于日志。
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(store.Save([first, second, third]));
        Assert.True(new FileInfo(journal).Length > 0);

        // 退避到期：日志含当前世代行，清理必须跳过，否则重启丢失 third。
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(store.Save([first, second, third]));
        Assert.True(File.Exists(journal), "含当前世代行的日志不能被清理");

        // 模拟重启：基础文件（first+second）+ 日志（third）应完整恢复。
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(3, reloaded.Count);
        Assert.Contains(reloaded, entry => entry.ModId == 3);
    }

    /// <summary>
    /// 日志可写但不可删时，整份写成功后用截断清理旧日志；
    /// 若清理停留在「删除/改名失败」而不截断，日志会保留旧内容，本用例失败。
    /// </summary>
    [Fact]
    public void Save_JournalWritableButNotDeletable_TruncatesStaleJournal()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry third = new(3, "sig", "三", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        SilentLogger logger = new();
        JsonModelTextCacheStore store = new(path, logger);
        Assert.True(store.TryLoad(out _));

        // 先制造一段真实日志内容（只追加 second）。
        Assert.True(store.Save([first, second]));
        Assert.True(new FileInfo(journal).Length > 0);

        // 句柄允许写入但拒绝删除：删除/改名失败，截断成功。
        using (FileStream holder = new(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.True(store.Save([first, second, third]));
            Assert.True(File.Exists(journal));
            Assert.Equal(0L, new FileInfo(journal).Length);
            Assert.DoesNotContain(
                logger.Warnings,
                warning => warning.Contains("无法删除/改名/截断", StringComparison.Ordinal));
        }

        // 锁释放后可以继续追加新变化。
        ModelTextCacheEntry fourth = new(4, "sig", "四", "stamp", "mhws");
        Assert.True(store.Save([first, second, third, fourth]));
        Assert.True(new FileInfo(journal).Length > 0);
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(4, reloaded.Count);
    }

    /// <summary>JSON 字节估算的 UTF-8、短转义、控制字符与代理项。</summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("中文", 6)]
    [InlineData("\b", 2)]
    [InlineData("\t", 2)]
    [InlineData("\n", 2)]
    [InlineData("\f", 2)]
    [InlineData("\r", 2)]
    [InlineData("\u0001", 6)]
    [InlineData("a\"b", 4)]
    [InlineData("a\\b", 4)]
    [InlineData("😀", 4)]
    [InlineData("a😀b", 6)]
    [InlineData("\uD83D\uDE00\uD83D\uDE00", 8)]
    [InlineData("中文\u0001\"\\", 16)]
    public void EstimateJsonStringBytes_MatchesJsonEscaping(string value, int expected)
    {
        Assert.Equal(expected, JsonModelTextCacheStore.EstimateJsonStringBytes(value));
    }

    /// <summary>孤立代理项按 UTF-8 替换符计 3 字节，不做代理对合并。</summary>
    [Fact]
    public void EstimateJsonStringBytes_LoneSurrogate_CountsReplacementBytes()
    {
        // (char)0xD800 避免源码中出现孤立代理项字面量。
        Assert.Equal(3, JsonModelTextCacheStore.EstimateJsonStringBytes(new string((char)0xD800, 1)));
    }

    /// <summary>5000 条典型中文文本（100 字符）估算本机约 4ms，回归上界 200ms。</summary>
    [Fact]
    public void EstimateJsonStringBytes_FiveThousandChineseTexts_StaysFast()
    {
        string text = new('中', 100);
        // 预热一次，避免把 JIT 计入回归测量。
        _ = JsonModelTextCacheStore.EstimateJsonStringBytes(text);
        Stopwatch stopwatch = Stopwatch.StartNew();
        long total = 0;
        for (int index = 0; index < 5000; index++)
        {
            total += JsonModelTextCacheStore.EstimateJsonStringBytes(text);
        }

        stopwatch.Stop();
        Assert.Equal(5000L * 100 * 3, total);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 200,
            $"5000 条中文文本估算耗时 {stopwatch.ElapsedMilliseconds}ms，超过 200ms 回归上界");
    }

    /// <summary>日志追加与基础文件重写同时失败时，Save 必须报告失败，不能假成功。</summary>
    [Fact]
    public void Save_AppendAndBaseBothFail_ReturnsFalseThenRecovers()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = [first, second];

        using (FileStream lockedBase = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (FileStream lockedJournal = new(
            path + ".journal",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.False(store.Save(entries));
        }

        Assert.True(store.Save(entries));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(2, reloaded.Count);
    }

    /// <summary>超过 32MB 硬上限的日志在加载时告警并隔离，不影响基础数据。</summary>
    [Fact]
    public void TryLoad_OversizedJournal_IsQuarantinedWithWarning()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .Save([new ModelTextCacheEntry(1, "sig", "一", "stamp", "mhws")]));

        string journal = path + ".journal";
        using (FileStream stream = new(journal, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(JsonModelTextCacheStore.MaxJournalBytes + 1L);
        }

        SilentLogger logger = new();
        Assert.True(new JsonModelTextCacheStore(path, logger)
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries));
        Assert.Single(entries);
        Assert.False(File.Exists(journal));
        Assert.Contains(logger.Warnings, warning => warning.Contains("过大", StringComparison.Ordinal));
    }

    /// <summary>外部只读句柄允许共享时不应阻塞压缩。</summary>
    [Fact]
    public void Save_ExternalSharedReader_DoesNotBlockCompaction()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        SilentLogger logger = new();
        JsonModelTextCacheStore store = new(path, logger);
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('x', 1024))];

        using (FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.True(store.Save(entries), string.Join(" | ", logger.Warnings));
        }

        Assert.False(File.Exists(path + ".journal"), string.Join(" | ", logger.Warnings));
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>目录拒绝写入时提前失败退避，不留下临时文件；恢复后下一次保存完成压缩。</summary>
    [AclFact(requireWriteDeny: true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Save_DirectoryReadOnly_BacksOffWithoutTempThenRecovers()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        MutableClock clock = new();
        SilentLogger logger = new();
        // 注入单调时钟与等待委托，避免整份写节流造成真实休眠。
        long tick = 0;
        JsonModelTextCacheStore store = new(
            path,
            logger,
            clock,
            tickSource: () => tick,
            delay: wait => tick += (long)wait.TotalMilliseconds);
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('x', 1024))];

        using (FileStream lockedBase = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // 日志已超 1MB 阈值；基础文件被占用导致压缩失败并进入退避。
            Assert.True(store.Save(entries));
        }

        Assert.True(File.Exists(path + ".journal"));
        clock.Advance(TimeSpan.FromSeconds(31));

        using (DirectoryWriteDeny deny = DirectoryWriteDeny.Apply(temp.Path))
        {
            Assert.True(store.Save(entries));
        }

        Assert.True(File.Exists(path + ".journal"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp-*"));
        Assert.Contains(logger.Warnings, warning => warning.Contains("不可写", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(store.Save(entries));
        Assert.False(File.Exists(path + ".journal"));
    }

    /// <summary>
    /// 整份写节流使用可注入单调时钟，覆盖 1ms 边界、零变化不等待与失败重试；
    /// 删除等待、把零变化也纳入等待或失败后不更新节流戳都会让本用例失败。
    /// </summary>
    [Fact]
    public void Save_BaseWriteThrottle_CoversBoundaryZeroChangesAndFailures()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry first = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry second = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry third = new(3, "sig", "三", "stamp", "mhws");
        ModelTextCacheEntry fourth = new(4, "sig", "四", "stamp", "mhws");
        ModelTextCacheEntry fifth = new(5, "sig", "五", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([first]));

        MutableClock clock = new();
        long tick = 0;
        List<TimeSpan> waits = [];
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock,
            tickSource: () => tick,
            delay: wait =>
            {
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            });
        Assert.True(store.TryLoad(out _));

        using (FileStream locked = new(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // 进入清理待办；最后一次写尝试在 tick=0。
            Assert.True(store.Save([first, second]));
            waits.Clear();

            // 999ms：还需等待 1ms 才能整份写。
            tick = 999;
            Assert.True(store.Save([first, second, third]));
            Assert.Equal(1, waits[^1].TotalMilliseconds, 0);

            // 正好 1000ms：不再等待（上次写尝试在 tick=1000）。
            tick = 2000;
            int beforeBoundary = waits.Count;
            Assert.True(store.Save([first, second, third, fourth]));
            Assert.Equal(beforeBoundary, waits.Count);

            // 零变化不进入整份写等待：旧实现会在清理待办期间等待 1 秒。
            tick = 2000;
            Assert.True(store.Save([first, second, third, fourth]));
            Assert.Equal(beforeBoundary, waits.Count);

            // 失败尝试也更新节流时间戳（C-03/P-04）：下一次保存仍要等待。
            using (FileStream lockedBase = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(store.Save([first, second, third, fourth, fifth]));
                int beforeFailure = waits.Count;
                Assert.False(store.Save([first, second, third, fourth, fifth]));
                Assert.True(waits.Count > beforeFailure, "失败尝试后仍应受节流");
            }
        }
    }

    /// <summary>
    /// 两个并发保存读到同一节流戳时，等待后必须复查并重新计算差异；
    /// 相同内容只发生一次整份写。把等待移回锁内会让本用例超时，删除复查会让写盘次数变成 2。
    /// </summary>
    [Fact]
    public void Save_ConcurrentIdenticalSaves_WriteBaseOnce()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        List<ModelTextCacheEntry> initialEntries = [seed, .. CreateLargeEntries(new string('x', 1024))];
        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('y', 1024))];

        // 先整份写一次建立「刚刚写过」的节流戳（首次写不受节流限制）；随后只前进 500ms，
        // 两个并发保存都必须等待剩余 500ms。
        long tick = 10_000;
        using Barrier barrier = new(2);
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock: null,
            tickSource: () => Interlocked.Read(ref tick),
            delay: wait =>
            {
                // 先汇合再推进时钟：确保两个线程都已在同一个节流窗口内进入等待，
                // 而不是一个先推进时钟让另一个直接跳过等待。
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("并发保存未同时进入整份写等待");
                }

                Interlocked.Add(ref tick, (long)wait.TotalMilliseconds);
            });
        Assert.True(store.TryLoad(out _));
        Assert.True(store.Save(initialEntries));
        int attemptsBefore = store.BaseWriteAttempts;
        Interlocked.Add(ref tick, 500);

        Task<bool> firstSave = Task.Run(() => store.Save(entries));
        Task<bool> secondSave = Task.Run(() => store.Save(entries));
        Assert.True(
            Task.WaitAll([firstSave, secondSave], TimeSpan.FromSeconds(20)),
            "并发保存未在超时内完成（等待可能发生在锁内或未释放）");
        Assert.True(firstSave.Result);
        Assert.True(secondSave.Result);

        // 只允许一次整份写：第二个保存等待后重新计算差异，发现内容相同直接结束。
        Assert.Equal(attemptsBefore + 1, store.BaseWriteAttempts);
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>
    /// 仅追加与「零变化且日志未超压缩阈值」的保存不进入整份写等待；
    /// 零变化但日志达到压缩阈值仍会整份写并等待（见 Save_CompactionFails_BacksOffThenRecovers）。
    /// </summary>
    [Fact]
    public void Save_JournalAppendOrNoChanges_DoesNotWait()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        long tick = 0;
        List<TimeSpan> waits = [];
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock: null,
            tickSource: () => tick,
            delay: wait =>
            {
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            });
        Assert.True(store.TryLoad(out _));

        // 首次整份写由 _hasWrittenBase 免等待，把节流戳设为“刚刚”。
        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('x', 1024))];

        Assert.True(store.Save(entries));
        Assert.Equal(1, store.BaseWriteAttempts);
        waits.Clear();

        // 仅追加一条小变化：日志可写时不等待，也不整份写。
        tick = 100;
        entries.Add(new ModelTextCacheEntry(1210, "sig", "小", "stamp", "mhws"));
        Assert.True(store.Save(entries));
        Assert.Empty(waits);
        Assert.Equal(1, store.BaseWriteAttempts);

        // 零变化：直接结束，不等待。
        tick = 200;
        Assert.True(store.Save(entries));
        Assert.Empty(waits);
        Assert.Equal(1, store.BaseWriteAttempts);
    }

    /// <summary>增长写按本次预计字节折算，单次写不会超过 8MB/s。</summary>
    [Fact]
    public void Save_GrowingSnapshot_ThrottleUsesEstimatedWriteBytes()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        long tick = 0;
        List<TimeSpan> waits = [];
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock: null,
            tickSource: () => tick,
            delay: wait =>
            {
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            });
        Assert.True(store.TryLoad(out _));

        // 先写约 1.2MB 快照，建立“刚刚写过”的节流戳（首次写不受节流限制）。
        List<ModelTextCacheEntry> initial = [seed, .. CreateLargeEntries(new string('x', 1024))];
        Assert.True(store.Save(initial));
        waits.Clear();

        // 再写约 18MB 快照：按本次字节折算约需 2.3 秒；只按旧基础文件（1.2MB）折算只需 1 秒。
        tick = 500;
        List<ModelTextCacheEntry> growing = [seed, .. CreateLargeEntries(new string('y', 2048), count: 9000)];
        Assert.True(store.Save(growing));
        Assert.NotEmpty(waits);
        Assert.True(
            waits[0].TotalMilliseconds > 1500,
            $"等待 {waits[0].TotalMilliseconds}ms 未按本次预计写入字节折算");
        Assert.Equal(2, store.BaseWriteAttempts);
    }

    /// <summary>
    /// 单次 Save 的等待预算耗尽后强制整份写；本次会话第一次整份写完全免等待。
    /// 注入 100ms 预算避免用 128MB 缓存做端到端测试；128MB 的字节间隔本身已由下面的断言锁定大于默认预算。
    /// </summary>
    [Fact]
    public void Save_BudgetExhausted_ForcesBaseWriteWithoutFirstWriteDelay()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        long tick = 0;
        List<TimeSpan> waits = [];
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock: null,
            tickSource: () => tick,
            delay: wait =>
            {
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            },
            maxBaseWriteWait: TimeSpan.FromMilliseconds(100));
        Assert.True(store.TryLoad(out _));
        int probesAfterLoad = store.FileExistsProbeCount;

        // 第一次整份写：即使估算 1.2MB、间隔 1 秒且预算只有 100ms，也不等待。
        Assert.True(store.Save([seed, .. CreateLargeEntries(new string('x', 1024))]));
        Assert.Empty(waits);
        Assert.Equal(1, store.BaseWriteAttempts);
        // 基础文件探测按场景精确断言；日志不存在的清理路径每次固定 2 次探测。
        Assert.Equal(1, store.BaseDestinationProbeCount);
        Assert.Equal(probesAfterLoad + 2, store.FileExistsProbeCount);

        // 第二次整份写：剩余 500ms 被预算裁到 100ms；预算耗尽后强制写入。
        Interlocked.Exchange(ref tick, 500);
        Assert.True(store.Save([seed, .. CreateLargeEntries(new string('y', 1024))]));
        Assert.Single(waits);
        Assert.Equal(100, waits[0].TotalMilliseconds, 0);
        Assert.Equal(2, store.BaseWriteAttempts);
        // 两次整份写各探测基础文件一次；总量按「每次写基础 + 日志清理」精确断言。
        Assert.Equal(2, store.BaseDestinationProbeCount);
        Assert.Equal(probesAfterLoad + 4, store.FileExistsProbeCount);

        // P-03：128MB 的节流窗口（约 16 秒）大于默认 10 秒预算，必须走「预算耗尽强制写」而不是无限等待。
        // 首次整份写本身由 _hasWrittenBase 免等待，与缓存体积无关（实际耗时是序列化 + 落盘）。
        Assert.True(
            JsonModelTextCacheStore.ComputeBaseWriteIntervalMilliseconds(128 * 1024 * 1024)
                > JsonModelTextCacheStore.MaxBaseWriteWait.TotalMilliseconds);
    }

    /// <summary>
    /// 注入第一次换入失败并模拟外部删除基础文件，验证重新探测后重试一次即可成功；
    /// 去掉重试分支会让本用例失败。
    /// </summary>
    [Fact]
    public void Save_BaseMoveFailsOnce_RetriesAfterReprobeAndSucceeds()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        int calls = 0;
        bool? firstDestinationExists = null;
        bool? secondDestinationExists = null;
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock: null,
            moveBaseFailureInjection: destinationExists =>
            {
                calls++;
                if (calls == 1)
                {
                    firstDestinationExists = destinationExists;
                    // 模拟探测后基础文件被外部删除：本次换入失败，重试前必须重新探测为「不存在」。
                    File.Delete(path);
                    return false;
                }

                secondDestinationExists = destinationExists;
                return true;
            });
        Assert.True(store.TryLoad(out _));

        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('x', 1024))];
        Assert.True(store.Save(entries));

        Assert.Equal(2, calls);
        Assert.True(firstDestinationExists, "第一次换入应沿用写前探测结果（基础文件存在）");
        Assert.False(secondDestinationExists, "重试前应重新探测到基础文件已被外部删除");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(entries.Count, reloaded.Count);
    }

    /// <summary>
    /// 差异计划只属于发起等待的 Save 调用；另一个并发不同快照的保存不能复用它。
    /// A 在等待中持有「新增 a」的计划，B 以自己的快照保存，必须写入 b 而不是 a。
    /// </summary>
    [Fact]
    public void Save_ConcurrentDifferentSnapshots_DoNotShareDifferencePlan()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        string journal = path + ".journal";
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry marker = new(2, "sig", "二", "stamp", "mhws");
        ModelTextCacheEntry a = new(3, "sig", "甲", "stamp", "mhws");
        ModelTextCacheEntry b = new(4, "sig", "乙", "stamp", "mhws");

        long tick = 0;
        int delayCount = 0;
        using ManualResetEventSlim aWaiting = new(false);
        using ManualResetEventSlim releaseA = new(false);
        JsonModelTextCacheStore store = new(
            path,
            new SilentLogger(),
            clock: null,
            tickSource: () => Interlocked.Read(ref tick),
            delay: wait =>
            {
                // 只阻塞第一次等待（A 的计划已缓存）；B 应当追加成功、不进入等待。
                if (Interlocked.Increment(ref delayCount) == 1)
                {
                    aWaiting.Set();
                    releaseA.Wait(TimeSpan.FromSeconds(10));
                }

                Interlocked.Add(ref tick, (long)wait.TotalMilliseconds);
            });
        Assert.True(store.TryLoad(out _));
        Assert.True(store.Save([seed, marker]));
        Assert.Equal(1, store.BaseWriteAttempts);

        using FileStream lockedJournal = new(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Interlocked.Add(ref tick, 500);

        Task<bool> saveA = Task.Run(() => store.Save([seed, marker, a]));
        Assert.True(aWaiting.Wait(TimeSpan.FromSeconds(10)), "A 未进入节流等待");
        lockedJournal.Dispose();

        // B 使用不同快照：若复用了 A 的计划，日志会出现 a（ModId=3）而不是 b（ModId=4）。
        Assert.True(store.Save([seed, marker, b]));
        string journalText = File.Exists(journal) ? File.ReadAllText(journal) : string.Empty;
        Assert.Contains("\"ModId\":4", journalText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ModId\":3", journalText, StringComparison.Ordinal);

        releaseA.Set();
        Assert.True(saveA.Wait(TimeSpan.FromSeconds(20)), "A 未在超时内完成");
        Assert.True(saveA.Result);
        // 每次 Save 都是完整快照：A 最后写入，它的快照不含 b，因此最终状态是 seed+marker+a。
        // 关键是 B 返回前确实写入了自己的 b（上面的日志断言），而不是复用了 A 的差异计划。
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger())
            .TryLoad(out IReadOnlyList<ModelTextCacheEntry> reloaded));
        Assert.Equal(3, reloaded.Count);
        Assert.Contains(reloaded, entry => entry.ModId == 3);
        Assert.DoesNotContain(reloaded, entry => entry.ModId == 4);
    }

    /// <summary>
    /// 整份写失败不能把 _baseBytes 抬到临时文件大小，否则后续较小的保存会被永久按大文件节流。
    /// 用只读共享句柄让 ReplaceFile 在序列化后失败（失败可观测），再保存约 1.2MB 快照，
    /// 等待应仍在 1 秒级而不是 2 秒级。
    /// </summary>
    [Fact]
    public void Save_FailedBaseWrite_DoesNotInflateThrottleBaseline()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        ModelTextCacheEntry marker = new(2, "sig", "二", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        long tick = 0;
        List<TimeSpan> waits = [];
        SilentLogger logger = new();
        JsonModelTextCacheStore store = new(
            path,
            logger,
            clock: null,
            tickSource: () => tick,
            delay: wait =>
            {
                waits.Add(wait);
                tick += (long)wait.TotalMilliseconds;
            });
        Assert.True(store.TryLoad(out _));
        // 首次整份写（约 1.2MB）免等待，建立节流戳与较小的基础文件基准。
        Assert.True(store.Save([seed, marker, .. CreateLargeEntries(new string('a', 1024))]));
        Assert.Equal(1, store.BaseWriteAttempts);

        List<ModelTextCacheEntry> large = [seed, marker, .. CreateLargeEntries(new string('z', 4096), 4000)];
        // 允许只读打开、但拒绝替换/删除：失败发生在临时文件写完之后，才能覆盖 _baseBytes 被抬高的路径。
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            tick = 100;
            Assert.True(store.Save(large));
        }

        // 替换阶段失败必须可观测（日志含不可写告警）。
        Assert.Contains(logger.Warnings, warning => warning.Contains("不可写", StringComparison.Ordinal));

        // 基础文件恢复可写后保存较小快照：等待应按 1 秒级间隔，而不是失败写留下的 16MB 基准。
        waits.Clear();
        tick += 100;
        Assert.True(store.Save([seed, marker, .. CreateLargeEntries(new string('m', 1024))]));
        Assert.NotEmpty(waits);
        // 同时断言下限，未节流（0ms）与节流（约 900ms）可区分。
        Assert.True(waits[^1].TotalMilliseconds >= 800, $"等待 {waits[^1].TotalMilliseconds}ms 低于节流下限");
        Assert.True(
            waits[^1].TotalMilliseconds < 1200,
            $"失败写抬高了后续节流基准：等待 {waits[^1].TotalMilliseconds}ms");
        Assert.Equal(3, store.BaseWriteAttempts);
    }

    /// <summary>
    /// 5000 条缓存的 diff + 估算段耗时（不含日志写盘），
    /// 回归上界 500ms；超过说明锁内扫描出现了数量级回退。
    /// </summary>
    [Fact]
    public void Save_FiveThousandEntries_DiffStaysWithinBudget()
    {
        using TempDir temp = TempDir.Create();
        string path = Path.Combine(temp.Path, "model-text-cache.json");
        ModelTextCacheEntry seed = new(1, "sig", "一", "stamp", "mhws");
        Assert.True(new JsonModelTextCacheStore(path, new SilentLogger()).Save([seed]));

        JsonModelTextCacheStore store = new(path, new SilentLogger());
        Assert.True(store.TryLoad(out _));
        List<ModelTextCacheEntry> entries = [seed, .. CreateLargeEntries(new string('中', 100), 5000)];
        Assert.True(store.Save(entries));

        List<ModelTextCacheEntry> changed = [.. entries];
        ModelTextCacheEntry last = changed[^1];
        changed[^1] = new ModelTextCacheEntry(last.ModId, "sig", "改", "stamp", "mhws");
        Assert.True(store.Save(changed));

        // 确认修改一条后的末次保存确实执行了第二次 diff 与一次日志批次序列化，
        // 计时范围（diff + 估算）内有实际工作。
        Assert.Equal(2, store.ChangeDiffCount);
        Assert.Equal(1, store.JournalSerializeBatchCount);
        Assert.True(
            store.LastChangeDiffMilliseconds < 500,
            $"5000 条 diff+估算耗时 {store.LastChangeDiffMilliseconds:0.###}ms，超过 500ms 回归上界");
    }

    /// <summary>单次 Save 的累计等待预算有独立断言，修改上限会让本用例失败。</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5000, 0, 5000)]
    [InlineData(20000, 0, 10000)]
    [InlineData(20000, 9000, 1000)]
    [InlineData(20000, 10000, 0)]
    public void ClampBaseWriteWait_RespectsPerSaveBudget(long remaining, long waited, long expected)
    {
        Assert.Equal(
            expected,
            JsonModelTextCacheStore.ClampBaseWriteWait(
                remaining,
                waited,
                (long)JsonModelTextCacheStore.MaxBaseWriteWait.TotalMilliseconds));
    }

    /// <summary>
    /// 8MB/s 字节上限与 1 秒最小间隔的换算有独立断言；修改常量会让本用例失败。
    /// </summary>
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(4 * 1024 * 1024, 1000)]
    [InlineData(8 * 1024 * 1024, 1000)]
    [InlineData(16 * 1024 * 1024, 2000)]
    [InlineData(128 * 1024 * 1024, 16001)]
    public void ComputeBaseWriteIntervalMilliseconds_MatchesByteRateCap(long baseBytes, long expected)
    {
        Assert.Equal(expected, JsonModelTextCacheStore.ComputeBaseWriteIntervalMilliseconds(baseBytes));
    }

    /// <summary>转义字符集合一次预分配到最大长度，新增字符未同步容量会失败。</summary>
    [Fact]
    public void BuildJsonEscapedChars_LengthMatchesPreallocatedCapacity()
    {
        string escaped = JsonModelTextCacheStore.BuildJsonEscapedChars();

        Assert.Equal(JsonModelTextCacheStore.JsonEscapedCharCount, escaped.Length);
        Assert.Equal(67, escaped.Length);
        Assert.Contains('\0', escaped);
        Assert.Contains('\u001F', escaped);
        Assert.Contains('\u007F', escaped);
        Assert.Contains('\u009F', escaped);
        Assert.Contains('"', escaped);
        Assert.Contains('\\', escaped);
    }

    /// <summary>DEL/C1 控制字符与宽松编码器的实际输出一致。</summary>
    [Theory]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0080b")]
    [InlineData("a\u0085b")]
    [InlineData("a\u009Fb")]
    public void EstimateJsonStringBytes_MatchesRelaxedSerializerForDelAndC1(string value)
    {
        byte[] serialized = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, RelaxedJsonOptions);

        Assert.Equal(serialized.Length - 2, JsonModelTextCacheStore.EstimateJsonStringBytes(value));
    }

    /// <summary>与缓存存储一致的宽松 JSON 编码器选项（缓存实例，避免 CA1869）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>构造指定数量的大快照条目（默认 1200 条：消除重复循环）。</summary>
    /// <param name="text">每条文本。</param>
    /// <param name="count">条数。</param>
    /// <returns>MOD ID 从 10 开始的条目列表。</returns>
    private static List<ModelTextCacheEntry> CreateLargeEntries(string text, int count = 1200)
    {
        List<ModelTextCacheEntry> entries = new(count);
        for (int id = 10; id < 10 + count; id++)
        {
            entries.Add(new ModelTextCacheEntry(id, "sig", text, "stamp", "mhws"));
        }

        return entries;
    }

    /// <summary>读取存储锁，用于断言等待发生在锁外。</summary>
    /// <param name="store">存储实例。</param>
    /// <returns><c>_gate</c> 对象。</returns>
    private static object GetGate(JsonModelTextCacheStore store) =>
        typeof(JsonModelTextCacheStore)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(store)!;

    private static IReadOnlyList<ModelTextCacheEntry> Load(JsonModelTextCacheStore store) =>
        store.TryLoad(out IReadOnlyList<ModelTextCacheEntry> entries) ? entries : [];

    private sealed class SilentLogger : HunterBox.Application.Common.IAppLogger
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void WarnBatch(IReadOnlyList<string> messages) => Warnings.AddRange(messages);

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
