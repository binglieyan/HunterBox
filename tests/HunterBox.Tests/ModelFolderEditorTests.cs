using HunterBox.Application.Models;
using HunterBox.Domain.Models;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>拖文件夹入口的模型扫描（下沉 Application 后）：散装 + 根层 pak 条目，不依赖真实 pak 解析。</summary>
public class ModelFolderEditorTests
{
    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    private const string ArmorMale = "natives/stm/art/model/character/ch02/012/003/5/ch02_012_0035.mesh.1";
    private const string BowZero = "natives/stm/art/model/item/it11/00/0000/it1100_0000_0.mdf2.45";

    /// <summary>同一批两条源路径映射到同一目标时，整组保留原路径并告警，不抛异常。</summary>
    [Fact]
    public void ExcludeDuplicateTargets_SameTarget_KeepsBothSourcesAndWarns()
    {
        List<(string Source, string Target)> moves =
        [
            (Path.Combine("root", "a.mod3"), Path.Combine("root", "target.mod3")),
            (Path.Combine("root", "b.mod3"), Path.Combine("root", "TARGET.mod3")),
            (Path.Combine("root", "c.mod3"), Path.Combine("root", "other.mod3")),
        ];
        List<string> warnings = [];

        List<(string Source, string Target)> accepted = ModelFolderEditor.ExcludeDuplicateTargets(
            moves,
            Path.GetFileName,
            warnings);

        (string Source, string Target) remaining = Assert.Single(accepted);
        Assert.Equal("c.mod3", Path.GetFileName(remaining.Source));
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Contains("冲突", warning));
    }

    [Fact]
    public void Scan_LooseFilesAndPakEntries_ReturnsModelsWithPakSource()
    {
        using TempDir temp = TempDir.Create();
        temp.File(ArmorMale);
        temp.File("mod.pak");
        FakePakEntryPathReader reader = new();
        reader.Entries["mod.pak"] = [BowZero];
        FakeModelNameResolver names = new();
        names.Names[ArmorMale] = "公会骑士";

        FolderModelScan scan = ModelFolderEditor.Scan(temp.Path, ModelGame.Wilds, reader, names, Fs);

        Assert.Equal(2, scan.Models.Count);
        Assert.Empty(scan.UnsupportedPaks);
        Assert.Contains(
            scan.Models,
            model => model.RewriteId == "012_003" && model.PakFile is null && model.DisplayName == "公会骑士");
        Assert.Contains(scan.Models, model => model.IsWeapon && model.PakFile == "mod.pak");
    }

    [Fact]
    public void Scan_UnreadablePak_ReportsReasonAndKeepsLooseModels()
    {
        using TempDir temp = TempDir.Create();
        temp.File(ArmorMale);
        temp.File("bad.pak");
        FakePakEntryPathReader reader = new();
        reader.Failures["bad.pak"] = "无法解析：不是 RE Engine pak";
        List<string> warnings = [];

        FolderModelScan scan = ModelFolderEditor.Scan(
            temp.Path,
            ModelGame.Wilds,
            reader,
            new FakeModelNameResolver(),
            Fs,
            warnings.Add);

        Assert.Equal("012_003", Assert.Single(scan.Models).RewriteId);
        Assert.Contains(
            scan.UnsupportedPaks,
            text => text.Contains("bad.pak", StringComparison.Ordinal)
                && text.Contains("无法解析：不是 RE Engine pak", StringComparison.Ordinal));
        Assert.Contains(
            warnings,
            text => text.Contains("跳过无法扫描的 pak：bad.pak", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_World_DoesNotExpandRootPaks()
    {
        using TempDir temp = TempDir.Create();
        temp.File("bow.pak");
        FakePakEntryPathReader reader = new();
        reader.Entries["bow.pak"] = [BowZero];

        FolderModelScan scan = ModelFolderEditor.Scan(temp.Path, ModelGame.World, reader, new FakeModelNameResolver(), Fs);

        Assert.Empty(scan.Models);
        Assert.Empty(reader.RequestedPaks);
        Assert.Contains(
            scan.UnsupportedPaks,
            text => text.Contains("bow.pak", StringComparison.Ordinal)
                && text.Contains("不支持改模型ID", StringComparison.Ordinal));
    }

    /// <summary>N-19：文件夹改写遇到已存在的目标路径必须保留原文件并告警，不能 overwrite。</summary>
    [Fact]
    public void Rewrite_TargetExists_KeepsBothFilesAndWarns()
    {
        using TempDir temp = TempDir.Create();
        const string sourcePath = "natives/stm/art/model/character/ch02/012/003/5/body.mod3";
        const string targetPath = "natives/stm/art/model/character/ch02/015/007/5/body.mod3";
        temp.File(sourcePath, "source");
        temp.File(targetPath, "existing");

        ModelRewriteSummary summary = ModelFolderEditor.Rewrite(
            temp.Path,
            [("ch02/012/003", "ch02/015/007")],
            new PakModelRewriteService(new RePakReader()),
            ModelGame.Wilds,
            new FakePakEntryPathReader(),
            Fs,
            progress: null,
            CancellationToken.None);

        Assert.Equal(0, summary.FilesMoved);
        Assert.Equal("source", File.ReadAllText(Path.Combine(temp.Path, sourcePath)));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(temp.Path, targetPath)));
        Assert.Contains(summary.Warnings, warning => warning.Contains("目标路径已被占用"));
    }

    /// <summary>
    /// 骨骼路径无法解析成完整身份，但可被身份 token 改写；
    /// 第一个 change 移动它之后，第二个 change 仍会匹配。旧实现用失效快照直接索引会抛
    /// <see cref="KeyNotFoundException"/>，修复后应连续完成两次改名。
    /// </summary>
    [Fact]
    public void Rewrite_MultipleChangesMovingUnparsedFile_RewritesTwiceWithoutThrowing()
    {
        using TempDir temp = TempDir.Create();
        const string sourcePath = "bonesystem/ch02_012_003.mot";
        const string finalPath = "bonesystem/ch02_020_001.mot";
        temp.File(sourcePath, "bone");
        Assert.False(
            ModelIdentity.TryParsePath(sourcePath, ModelGame.Wilds, out _),
            "用例前提：该路径必须无法解析成完整模型身份，才会进入未解析分支");

        ModelRewriteSummary summary = ModelFolderEditor.Rewrite(
            temp.Path,
            [("ch02/012/003", "ch02/015/007"), ("ch02/015/007", "ch02/020/001")],
            new PakModelRewriteService(new RePakReader()),
            ModelGame.Wilds,
            new FakePakEntryPathReader(),
            Fs,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, summary.FilesMoved);
        Assert.True(File.Exists(Path.Combine(temp.Path, finalPath)), "链式改写应把文件移到最终身份路径");
        Assert.False(File.Exists(Path.Combine(temp.Path, sourcePath)));
        Assert.DoesNotContain(summary.Warnings, warning => warning.Contains("回滚", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_MissingFolder_ThrowsDirectoryNotFound()
    {
        using TempDir temp = TempDir.Create();

        Assert.Throws<DirectoryNotFoundException>(() => ModelFolderEditor.Scan(
            Path.Combine(temp.Path, "missing"),
            ModelGame.Wilds,
            new FakePakEntryPathReader(),
            new FakeModelNameResolver(),
            Fs));
    }
}
