using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>索引容错（/）：损坏文件绝不能静默清空并被后续 Save 覆盖。</summary>
public class ModRepositoryCorruptionTests
{
    private static string IndexPath(BoxPaths paths) => Path.Combine(paths.ModsDirectory, "index.json");

    private static string[] BadFiles(BoxPaths paths) =>
        [.. Directory.GetFiles(paths.ModsDirectory, "index.json.bad-*")];

    [Fact]
    public void Load_TruncatedJson_PreservesBadFileAndExposesWarning()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        const string truncated = """{"NextId": 3, "Mods": [{"Id": 1, "Index": 1""";
        File.WriteAllText(IndexPath(paths), truncated);

        ModRepository repo = ModRepository.Load(paths);

        Assert.Empty(repo.Mods);
        Assert.NotNull(repo.LoadWarning);
        Assert.Contains(".bad-", repo.LoadWarning);
        Assert.False(File.Exists(IndexPath(paths)), "损坏原件应已改名保留");
        string bad = Assert.Single(BadFiles(paths));
        Assert.Equal(truncated, File.ReadAllText(bad));
        Assert.Contains(Path.GetFileName(bad), repo.LoadWarning);
    }

    /// <summary>启动加载时清理历史遗留的多余索引备份（≤3）。</summary>
    [Fact]
    public void Load_StaleBadCopies_ArePrunedAtStartup()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        for (int index = 0; index < 5; index++)
        {
            File.WriteAllText($"{IndexPath(paths)}.bad-2026010100000{index}", "old");
        }

        File.WriteAllText(IndexPath(paths), """{"SchemaVersion": 1, "NextId": 1, "Mods": []}""");
        ModRepository repo = ModRepository.Load(paths);

        Assert.Empty(repo.Mods);
        Assert.True(
            BadFiles(paths).Length <= CorruptFileQuarantine.DefaultMaxCopies,
            "启动期未清理多余的索引备份");
    }

    /// <summary>索引隔离文件同样只保留最近 3 份，不会无上限累积。</summary>
    [Fact]
    public void Load_RepeatedCorruption_KeepsLimitedBadCopies()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);

        for (int round = 0; round < CorruptFileQuarantine.DefaultMaxCopies + 2; round++)
        {
            File.WriteAllText(IndexPath(paths), "{ 损坏 " + round);
            _ = ModRepository.Load(paths);
        }

        Assert.True(
            BadFiles(paths).Length <= CorruptFileQuarantine.DefaultMaxCopies,
            "索引隔离文件数量超过上限");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"NextId\": 3, \"Mods\": null}")]
    public void Load_NonIndexShape_IsTreatedAsCorrupt(string content)
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), content);

        ModRepository repo = ModRepository.Load(paths);

        Assert.Empty(repo.Mods);
        Assert.NotNull(repo.LoadWarning);
        Assert.Single(BadFiles(paths));
    }

    [Fact]
    public void Load_ValidEmptyDocument_IsNotTreatedAsCorrupt()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), """{"SchemaVersion": 1, "NextId": 1, "Mods": []}""");

        ModRepository repo = ModRepository.Load(paths);

        Assert.Empty(repo.Mods);
        Assert.Null(repo.LoadWarning);
        Assert.Empty(BadFiles(paths));
    }

    [Fact]
    public void Load_IndexWithNullRecord_SkipsNullAndKeepsValidOnes()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            IndexPath(paths),
            """
            {"SchemaVersion":1,"NextId":5,"Mods":[null,{"Id":1,"Index":1,"Metadata":{"Name":"A"},"RelativeFiles":[]},null]}
            """);

        // C-04：null 元素不能进入仓库，否则构造函数里的 mods.Max 直接 NRE。
        ModRepository repo = ModRepository.Load(paths);

        ModRecord record = Assert.Single(repo.Mods);
        Assert.Equal(1, record.Id);
        Assert.Equal(5, repo.NextId());

        // 清洗丢弃记录必须通过 LoadWarning 告知用户，不能静默消失。
        Assert.NotNull(repo.LoadWarning);
        Assert.Contains("2 条", repo.LoadWarning);
    }

    [Fact]
    public void Load_IndexWithNullMetadataAndFiles_RepairsDefaults()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            IndexPath(paths),
            """{"SchemaVersion":1,"NextId":2,"Mods":[{"Id":1,"Index":1,"Metadata":null,"RelativeFiles":null}]}""");

        // C-04：JSON null 会绕开属性初始化器，加载时必须补回默认值。
        ModRepository repo = ModRepository.Load(paths);

        ModRecord record = Assert.Single(repo.Mods);
        Assert.NotNull(record.Metadata);
        Assert.Empty(record.RelativeFiles);
        Assert.Equal("MOD #1", record.DisplayName);
        Assert.Null(repo.LoadWarning);
    }

    [Fact]
    public void Load_RelativeFilesWithNullAndBlankEntries_FiltersThem()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            IndexPath(paths),
            """
            {"SchemaVersion":1,"NextId":2,"Mods":[{"Id":1,"Index":1,"Metadata":{"Name":"A"},"RelativeFiles":[null,"","  ","natives/stm/a.tex"]}]}
            """);

        // null/空白元素会在启用/禁用时触发路径参数异常，加载时必须过滤。
        ModRepository repo = ModRepository.Load(paths);

        ModRecord record = Assert.Single(repo.Mods);
        Assert.Equal(["natives/stm/a.tex"], record.RelativeFiles);
    }

    [Fact]
    public void Load_InvalidIdsAndIndexes_AreDroppedOrRepaired()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            IndexPath(paths),
            """
            {"SchemaVersion":1,"NextId":2,"Mods":[
              {"Id":1,"Index":0,"Metadata":{"Name":"A"},"RelativeFiles":[]},
              {"Id":1,"Index":2,"Metadata":{"Name":"重复"},"RelativeFiles":[]},
              {"Id":0,"Index":3,"Metadata":{"Name":"非法"},"RelativeFiles":[]},
              {"Id":2,"Index":-5,"Metadata":{"Name":"B"},"RelativeFiles":[]}
            ]}
            """);

        // 重复 Id/Id≤0 丢弃；非正 Index 补到末尾，保证排序可用。
        ModRepository repo = ModRepository.Load(paths);

        Assert.Equal([1, 2], repo.Mods.Select(mod => mod.Id));
        Assert.All(repo.Mods, mod => Assert.True(mod.Index > 0, "Index 必须为正"));
        Assert.Equal(2, repo.Mods.Select(mod => mod.Index).Distinct().Count());
    }

    [Fact]
    public void Load_SameIndex_SortsByIdForStableOrder()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            IndexPath(paths),
            """
            {"SchemaVersion":1,"NextId":3,"Mods":[
              {"Id":2,"Index":1,"Metadata":{"Name":"B"},"RelativeFiles":[]},
              {"Id":1,"Index":1,"Metadata":{"Name":"A"},"RelativeFiles":[]}
            ]}
            """);

        // C-28：Index 相同时按 Id 兜底，列表顺序不会随排序实现漂移。
        ModRepository repo = ModRepository.Load(paths);

        Assert.Equal([1, 2], repo.Mods.Select(mod => mod.Id));
    }

    [Fact]
    public void Save_AfterCorruptLoad_WritesNewIndexAndKeepsBadFile()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), "{ broken json");
        ModRepository repo = ModRepository.Load(paths);
        string bad = Assert.Single(BadFiles(paths));

        repo.Add(new HunterBox.Domain.Mods.ModRecord
        {
            Id = 1,
            Index = 0,
            GameId = "mhws",
            Kind = HunterBox.Domain.Mods.ModKind.LooseFiles,
            RelativeFiles = ["natives/a.tex"],
        });
        repo.Save();

        Assert.True(File.Exists(IndexPath(paths)));
        Assert.True(File.Exists(bad), "之后 Save 不能覆盖/删除损坏原件");
        ModRepository reloaded = ModRepository.Load(paths);
        Assert.Null(reloaded.LoadWarning);
        Assert.Single(reloaded.Mods);
        Assert.Equal("mhws", reloaded.Mods[0].GameId);
    }

    [Fact]
    public void RepeatedCorruptLoads_KeepSeparateBadFiles()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), "first-broken");
        _ = ModRepository.Load(paths);
        File.WriteAllText(IndexPath(paths), "second-broken");
        _ = ModRepository.Load(paths);

        string[] bad = BadFiles(paths);
        Assert.Equal(2, bad.Length);
        string[] contents = [.. bad.Select(File.ReadAllText)];
        Assert.Contains("first-broken", contents);
        Assert.Contains("second-broken", contents);
    }

    [Fact]
    public void Load_LegacyArrayFormat_StillWorks()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(
            IndexPath(paths),
            """
            [
              {
                "Id": 7,
                "Index": 1,
                "Enabled": false,
                "Metadata": { "Name": "旧数组格式" },
                "Kind": 1,
                "RelativeFiles": ["natives/a.tex"]
              }
            ]
            """);

        ModRepository repo = ModRepository.Load(paths);

        Assert.Null(repo.LoadWarning);
        Assert.Equal("旧数组格式", Assert.Single(repo.Mods).Metadata.Name);
        Assert.Empty(repo.ModsFor("mhws"));
    }

    /// <summary>损坏索引已被改名保留后，用户恢复 .bak 仍不能被本次会话的空仓库覆盖。</summary>
    [Fact]
    public void Save_AfterCorruptLoadMovedAsideThenValidIndexRestored_RefusesToOverwrite()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), "{ broken json");
        ModRepository repo = ModRepository.Load(paths);
        Assert.Single(BadFiles(paths));

        const string restored = """{"SchemaVersion":1,"NextId":9,"Mods":[{"Id":8,"Index":1,"GameId":"mhws"}]}""";
        File.WriteAllText(IndexPath(paths), restored);

        Assert.Throws<IOException>(() => repo.Save());
        Assert.Equal(restored, File.ReadAllText(IndexPath(paths)));
    }

    [Fact]
    public void Save_KeepsPreviousIndexAsBak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository repo) = TestBox.OpenRepository(temp);
        repo.Add(new HunterBox.Domain.Mods.ModRecord
        {
            Id = 1,
            Index = 0,
            GameId = "mhws",
            Kind = HunterBox.Domain.Mods.ModKind.LooseFiles,
            RelativeFiles = ["natives/a.tex"],
        });
        repo.Save();
        string firstVersion = File.ReadAllText(IndexPath(paths));

        repo.Add(new HunterBox.Domain.Mods.ModRecord
        {
            Id = 2,
            Index = 0,
            GameId = "mhws",
            Kind = HunterBox.Domain.Mods.ModKind.LooseFiles,
            RelativeFiles = ["natives/b.tex"],
        });
        repo.Save();

        Assert.True(File.Exists(repo.BackupPath));
        Assert.Equal(firstVersion, File.ReadAllText(repo.BackupPath));
        Assert.Equal(2, ModRepository.Load(paths).Mods.Count);
        Assert.False(File.Exists(IndexPath(paths) + ".tmp"), "临时文件不应残留");
    }

    /// <summary>加载时损坏索引无法改名保留（被其它进程以允许读、禁止改名的共享模式占用）。</summary>
    private static ModRepository LoadWithUnpreservedCorruptIndex(BoxPaths paths)
    {
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), "{ broken json");
        using (new FileStream(
            IndexPath(paths),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            return ModRepository.Load(paths);
        }
    }

    /// <summary>N-13：损坏原件被外部删除后，Save 应清掉降级标志并正常写新索引，而不是永久失败。</summary>
    [Fact]
    public void Save_AfterUnpreservedCorruptFileDeleted_WritesNewIndex()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRepository repo = LoadWithUnpreservedCorruptIndex(paths);
        Assert.NotNull(repo.LoadWarning);
        File.Delete(IndexPath(paths));

        repo.Add(new HunterBox.Domain.Mods.ModRecord
        {
            Id = 1,
            Index = 0,
            GameId = "mhws",
            Kind = HunterBox.Domain.Mods.ModKind.LooseFiles,
            RelativeFiles = ["natives/a.tex"],
        });
        repo.Save();

        Assert.Single(ModRepository.Load(paths).Mods);
    }

    /// <summary>N-13：用户把 .bak 恢复成 index.json 后，Save 必须拒绝用当前空仓库覆盖并提示重启。</summary>
    [Fact]
    public void Save_AfterValidIndexRestored_RefusesToOverwrite()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRepository repo = LoadWithUnpreservedCorruptIndex(paths);
        const string restored = """{"SchemaVersion":1,"NextId":9,"Mods":[{"Id":8,"Index":1,"GameId":"mhws"}]}""";
        File.WriteAllText(IndexPath(paths), restored);

        IOException failure = Assert.Throws<IOException>(() => repo.Save());

        Assert.Contains("恢复", failure.Message);
        Assert.Equal(restored, File.ReadAllText(IndexPath(paths)));
    }

    [Fact]
    public void Load_IndexLockedByAnotherProcess_ThrowsInsteadOfReturningEmpty()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        Directory.CreateDirectory(paths.ModsDirectory);
        File.WriteAllText(IndexPath(paths), """{"NextId": 1, "Mods": []}""");

        using FileStream locked = new(
            IndexPath(paths),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.ThrowsAny<IOException>(() => ModRepository.Load(paths));
    }
}
