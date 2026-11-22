using System.Collections;
using HunterBox.Application.Models;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;
using HunterBox.Application.Paks;
using HunterBox.Domain.Paks;

namespace HunterBox.Tests;

/// <summary>
/// 模型自动分类下沉 Application 后的单元测试：用假 pak reader / 名称解析，验证识别、写描述、
/// recompute、坏包 warning 与「备注信息」文本，不读写真实 pak。
/// </summary>
public class ModelAutoClassifierTests
{
    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    private const string ArmorMale = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";
    private const string ArmorTexture =
        "natives/stm/art/model/character/ch02/012/003/5/textures/ch02_012_0035_ALBD.tex.1";

    private static BoxPaths NewPaths() => new(Path.Combine(
        Path.GetTempPath(),
        "hunterbox-tests",
        "model-classifier",
        Guid.NewGuid().ToString("N")));

    private static ModRecord NewMod(params string[] files) => new()
    {
        Id = 1,
        GameId = "mhws",
        RelativeFiles = [.. files],
    };

    [Fact]
    public void Apply_DetectedModel_WritesCategoryAndDescription()
    {
        ModRecord record = NewMod(ArmorMale);
        FakeModelNameResolver names = new();
        List<string> warnings = [];

        IReadOnlyList<string> ids = ModelAutoClassifier.Apply(
            record,
            [(ArmorMale, null)],
            ModelGame.Wilds,
            names,
            warnings.Add);

        Assert.Equal("012_003", Assert.Single(ids));
        Assert.Equal("模型替换", record.Metadata.Category);
        Assert.Equal("012_003", record.Metadata.Description);
        Assert.Contains(warnings, w => w.StartsWith("自动分类：", StringComparison.Ordinal));
    }

    /// <summary>分类带回的模型行可直接供备注文本复用，结果与现场检测一致。</summary>
    [Fact]
    public void Apply_OutDetected_ReusedByBuildModelText()
    {
        ModRecord record = NewMod(ArmorMale);
        FakeModelNameResolver names = new();
        IReadOnlyList<(string Path, string? PakFile)> entries = [(ArmorMale, null)];

        IReadOnlyList<string> ids = ModelAutoClassifier.Apply(
            record,
            entries,
            ModelGame.Wilds,
            names,
            out IReadOnlyList<DetectedModel> detected);

        Assert.Equal("012_003", Assert.Single(ids));
        Assert.NotEmpty(detected);
        Assert.Equal(
            ModelAutoClassifier.BuildModelText(record, entries, ModelGame.Wilds, names),
            ModelAutoClassifier.BuildModelText(record, entries, ModelGame.Wilds, names, detected));
    }

    /// <summary>分类对同一份条目只枚举一次（身份收集与模型检测共用一趟）。</summary>
    [Fact]
    public void Apply_CountedEntries_EnumeratesOnlyOnce()
    {
        ModRecord record = NewMod(ArmorMale, ArmorTexture);
        CountingEntryList entries = new((ArmorMale, null), (ArmorTexture, null));

        ModelAutoClassifier.Apply(record, entries, ModelGame.Wilds, new FakeModelNameResolver());

        Assert.Equal(entries.Count, entries.Reads);
    }

    [Fact]
    public void Apply_ExistingCategory_IsKept()
    {
        ModRecord record = NewMod(ArmorMale);
        record.Metadata = new ModMetadata { Name = "测试MOD", Category = "武器替换" };
        FakeModelNameResolver names = new();

        ModelAutoClassifier.Apply(record, [(ArmorMale, null)], ModelGame.Wilds, names);

        Assert.Equal("武器替换", record.Metadata.Category);
        Assert.Equal("012_003", record.Metadata.Description);
    }

    [Fact]
    public void Apply_NoModel_ReturnsEmptyAndKeepsMetadata()
    {
        ModRecord record = NewMod("readme.txt");
        ModMetadata before = record.Metadata;
        List<string> warnings = [];

        IReadOnlyList<string> ids = ModelAutoClassifier.Apply(
            NewPaths(),
            record,
            ModelGame.Wilds,
            new FakePakEntryPathReader(),
            new FakeModelNameResolver(),
            warnings.Add);

        Assert.Empty(ids);
        Assert.Equal(before, record.Metadata);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Apply_WithoutRecompute_KeepsUserDescription_WithRecomputeReplacesIt()
    {
        const string Entry = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";
        ModRecord record = NewMod(ArmorMale);
        record.Metadata = new ModMetadata { Name = "测试MOD", Description = "用户备注" };
        FakeModelNameResolver names = new();

        ModelAutoClassifier.Apply(record, [(Entry, null)], ModelGame.Wilds, names);
        Assert.Equal("用户备注", record.Metadata.Description);

        ModelAutoClassifier.Apply(record, [(Entry, null)], ModelGame.Wilds, names, recompute: true);
        Assert.Equal("012_003", record.Metadata.Description);
    }

    [Fact]
    public void Apply_NoResolvableNames_FallsBackToModelCodes()
    {
        ModRecord record = NewMod(ArmorMale);
        FakeModelNameResolver names = new();

        ModelAutoClassifier.Apply(record, [(ArmorMale, null)], ModelGame.Wilds, names);

        Assert.Equal("012_003", record.Metadata.Description);
    }

    [Fact]
    public void Apply_PakEntriesAreCollectedThroughReader()
    {
        ModRecord record = NewMod("mod.pak");
        FakePakEntryPathReader reader = new();
        reader.Entries["mod.pak"] = [ArmorMale, ArmorTexture];
        FakeModelNameResolver names = new();
        names.Names[ArmorMale] = "公会骑士";
        List<string> warnings = [];

        IReadOnlyList<string> ids = ModelAutoClassifier.Apply(
            NewPaths(),
            record,
            ModelGame.Wilds,
            reader,
            names,
            warnings.Add,
            recompute: true);

        Assert.Equal("012_003", Assert.Single(ids));
        Assert.Equal("公会骑士 (012_003) [pak]", record.Metadata.Description);
        Assert.Equal(["mod.pak"], reader.RequestedPaks);
        Assert.Contains(warnings, w => w.StartsWith("自动分类：", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_BadPak_WarnsAndStillClassifiesLooseFiles()
    {
        ModRecord record = NewMod("bad.pak", ArmorMale);
        FakePakEntryPathReader reader = new();
        reader.Failures["bad.pak"] = "无法解析：不是 RE Engine pak";
        FakeModelNameResolver names = new();
        names.Names[ArmorMale] = "公会骑士";
        List<string> warnings = [];

        IReadOnlyList<string> ids = ModelAutoClassifier.Apply(
            NewPaths(),
            record,
            ModelGame.Wilds,
            reader,
            names,
            warnings.Add);

        Assert.Equal("012_003", Assert.Single(ids));
        Assert.Contains(
            warnings,
            w => w.Contains("跳过无法扫描的 pak：bad.pak", StringComparison.Ordinal)
                && w.Contains("不是 RE Engine pak", StringComparison.Ordinal));
    }

    /// <summary>pak 读取成功但零条目仍算成功；读取失败才报告 false；世界不展开 pak 视为成功。</summary>
    [Fact]
    public void CollectEntries_ReportsPakReadabilityExplicitly()
    {
        BoxPaths paths = NewPaths();
        ModRecord record = NewMod("mod.pak");
        FakePakEntryPathReader empty = new();
        empty.Entries["mod.pak"] = [];

        _ = ModelAutoClassifier.CollectEntries(
            paths, record, ModelGame.Wilds, empty, null, out bool emptyReadable);

        Assert.True(emptyReadable);

        FakePakEntryPathReader failed = new();
        failed.Failures["mod.pak"] = "坏包";

        _ = ModelAutoClassifier.CollectEntries(
            paths, record, ModelGame.Wilds, failed, null, out bool failedReadable);

        Assert.False(failedReadable);

        _ = ModelAutoClassifier.CollectEntries(
            paths, record, ModelGame.World, failed, null, out bool worldReadable);

        Assert.True(worldReadable);
    }

    /// <summary>坏 TOC/超限/不支持 TOC 等条目级失败都报告不可读，不缓存空明细。</summary>
    [Theory]
    [InlineData("坏 TOC")]
    [InlineData("文件过大（ 512MB ）")]
    [InlineData("不支持的 TOC")]
    public void DetectDetails_PakEntryFailure_ReportsUnreadable(string reason)
    {
        ModRecord record = NewMod("mod.pak");
        FakePakEntryPathReader reader = new();
        reader.Failures["mod.pak"] = reason;

        IReadOnlyList<ModelDetail> details = ModelAutoClassifier.DetectDetails(
            NewPaths(),
            record,
            ModelGame.Wilds,
            reader,
            new FakeModelNameResolver(),
            null,
            out bool readable);

        Assert.False(readable);
        Assert.Empty(details);
    }

    /// <summary>空 pak（零条目但读取成功）仍可缓存，不误判为不可读。</summary>
    [Fact]
    public void DetectDetails_EmptyPak_StaysReadable()
    {
        ModRecord record = NewMod("mod.pak");
        FakePakEntryPathReader reader = new();
        reader.Entries["mod.pak"] = [];

        IReadOnlyList<ModelDetail> details = ModelAutoClassifier.DetectDetails(
            NewPaths(),
            record,
            ModelGame.Wilds,
            reader,
            new FakeModelNameResolver(),
            null,
            out bool readable);

        Assert.True(readable);
        Assert.Empty(details);
    }

    [Fact]
    public void BuildModelText_PakModel_ContainsNameAndPakMarker()
    {
        ModRecord record = NewMod("mod.pak");
        FakeModelNameResolver names = new();
        names.Names[ArmorMale] = "公会骑士";

        string text = ModelAutoClassifier.BuildModelText(
            record,
            [(ArmorMale, "mod.pak")],
            ModelGame.Wilds,
            names);

        Assert.Equal("公会骑士 (012_003) [pak]", text);
    }

    [Fact]
    public void BuildModelText_NoModel_FallsBackToUserDescription()
    {
        ModRecord record = NewMod("readme.txt");
        record.Metadata = new ModMetadata { Description = "用户备注" };

        string text = ModelAutoClassifier.BuildModelText(
            record,
            [("readme.txt", null)],
            ModelGame.Wilds,
            new FakeModelNameResolver());

        Assert.Equal("用户备注", text);
    }

    [Fact]
    public void DetectDetails_ResolvesDisplayNameAndPakSource()
    {
        ModRecord record = NewMod("mod.pak");
        FakePakEntryPathReader reader = new();
        reader.Entries["mod.pak"] = [ArmorMale];
        FakeModelNameResolver names = new();
        names.Names[ArmorMale] = "公会骑士";

        IReadOnlyList<ModelDetail> details = ModelAutoClassifier.DetectDetails(
            NewPaths(),
            record,
            ModelGame.Wilds,
            reader,
            names,
            null,
            out _);

        ModelDetail detail = Assert.Single(details);
        Assert.Equal("012_003", detail.RewriteId);
        Assert.Equal("公会骑士", detail.DisplayName);
        Assert.Equal("mod.pak", detail.PakFile);
        Assert.False(detail.HasPrefab);
    }

    [Fact]
    public void DescribeNoModels_UnsupportedPak_AppendsParseReason()
    {
        using TempDir temp = TempDir.Create();
        temp.File("Mods/1/files/bad.pak", "not a pak");
        BoxPaths paths = new(temp.Path);
        ModRecord record = NewMod("bad.pak");
        FakePakRepacker repacker = new(_ => throw new InvalidDataException("损坏的条目表"));

        string message = ModelAutoClassifier.DescribeNoModels(paths, record, ModelGame.Wilds, repacker, Fs);

        Assert.StartsWith("这个MOD没什么模型可以修改", message);
        Assert.Contains("bad.pak", message, StringComparison.Ordinal);
        Assert.Contains("损坏的条目表", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNoModels_World_DoesNotInspectRootPaks()
    {
        using TempDir temp = TempDir.Create();
        temp.File("Mods/1/files/bow.pak", "x");
        BoxPaths paths = new(temp.Path);
        ModRecord record = NewMod("bow.pak");
        FakePakRepacker repacker = new(_ => throw new InvalidOperationException("世界 pak 不应被检查"));

        string message = ModelAutoClassifier.DescribeNoModels(paths, record, ModelGame.World, repacker, Fs);

        Assert.Equal("这个MOD没什么模型可以修改", message);
        Assert.Equal(0, repacker.InspectCount);
    }
}

/// <summary>统计元素读取次数的条目列表：用于断言分类只遍历一次。</summary>
internal sealed class CountingEntryList : IReadOnlyList<(string Path, string? PakFile)>
{
    private readonly List<(string Path, string? PakFile)> _items;

    /// <summary>用给定条目创建计数列表。</summary>
    /// <param name="items">条目（路径 + 来源 pak）。</param>
    public CountingEntryList(params (string Path, string? PakFile)[] items) => _items = [.. items];

    /// <summary>条目的累计读取次数（索引器/枚举器每取一个元素加一）。</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public (string Path, string? PakFile) this[int index]
    {
        get
        {
            Reads++;
            return _items[index];
        }
    }

    /// <inheritdoc />
    public IEnumerator<(string Path, string? PakFile)> GetEnumerator()
    {
        for (int index = 0; index < _items.Count; index++)
        {
            yield return this[index];
        }
    }

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>假 pak 条目读取端口：按 pak 名返回预设条目或失败原因，不访问磁盘。</summary>
internal sealed class FakePakEntryPathReader : IPakEntryPathReader
{
    /// <summary>pak 名 → 条目路径。</summary>
    public Dictionary<string, IReadOnlyList<string>> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>pak 名 → 读取失败原因。</summary>
    public Dictionary<string, string> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>本次调用请求过的 pak 名（按调用顺序）。</summary>
    public List<string> RequestedPaks { get; } = [];

    /// <inheritdoc />
    public PakNameIndex LoadNameIndex(ModelGame game) => new();

    /// <inheritdoc />
    public IReadOnlyList<string>? TryReadEntryPaths(
        string pakPath,
        string pakName,
        PakNameIndex index,
        out string? reason)
    {
        RequestedPaks.Add(pakName);
        if (Failures.TryGetValue(pakName, out string? failure))
        {
            reason = failure;
            return null;
        }

        reason = null;
        return Entries.TryGetValue(pakName, out IReadOnlyList<string>? paths) ? paths : [];
    }
}

/// <summary>假名称解析端口：显式配置名字/描述，不读名称表资源。</summary>
internal sealed class FakeModelNameResolver : IModelNameResolver
{
    /// <summary>路径 → 名字。</summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Resolve(string path) => Names.GetValueOrDefault(path);
}

/// <summary>假 pak 整包解析端口：只实现 <see cref="IPakRepacker.Inspect"/>，其余调用失败。</summary>
internal sealed class FakePakRepacker : IPakRepacker
{
    private readonly Func<string, PakArchiveInfo> _inspect;

    /// <summary>用指定的 Inspect 行为装配。</summary>
    /// <param name="inspect">检查 pak 的行为。</param>
    public FakePakRepacker(Func<string, PakArchiveInfo> inspect) => _inspect = inspect;

    /// <summary><see cref="Inspect"/> 的调用次数。</summary>
    public int InspectCount { get; private set; }

    /// <inheritdoc />
    public PakArchiveInfo Inspect(string pakPath)
    {
        InspectCount++;
        return _inspect(pakPath);
    }

    /// <inheritdoc />
    public PakRepackProfile Capture(string pakPath) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Unpack(string pakPath, string destinationDirectory, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public void Unpack(
        string pakPath,
        string destinationDirectory,
        PakNameIndex? nameIndex,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Pack(
        string sourceDirectory,
        string pakPath,
        PakRepackProfile profile,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <inheritdoc />
    public void Pack(
        string sourceDirectory,
        string pakPath,
        PakRepackProfile profile,
        IProgress<PakRepackProgress>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
