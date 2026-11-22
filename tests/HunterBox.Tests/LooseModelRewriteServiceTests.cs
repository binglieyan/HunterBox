using System.Text;
using HunterBox.Application.Models;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Models;
using HunterBox.Domain.Mods;

namespace HunterBox.Tests;

public class LooseModelRewriteServiceTests
{
    private static readonly HunterBox.Application.Common.IFileSystem Fs =
        HunterBox.Infrastructure.IO.SystemFileSystem.Instance;

    private const string MeshPath = "natives/stm/art/model/character/ch03/012/003/5/ch03_012_0035.mesh.241111606";
    private const string PrefabPath = "natives/stm/gamedesign/equip/_prefab/armor/female/012/003/body/ch03_012_0032.pfb.18";
    private const string BonePath = "reframework/data/bonesystem/ch03_012_0031";
    private const string ScriptPath = "reframework/autorun/ch03_012_003.lua";
    private const string TexturePath =
        "natives/stm/art/model/character/ch03/012/003/5/textures/ch03_012_0035_ALBD.tex.241106027";

    private static (BoxPaths Paths, ModRecord Mod, LooseModelRewriteService Service) Setup(TempDir temp)
    {
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            RelativeFiles = [MeshPath, PrefabPath, BonePath, ScriptPath, TexturePath],
        };

        string filesDir = paths.FilesDirectory(1);
        foreach (string relative in mod.RelativeFiles)
        {
            string full = Path.Combine(filesDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "path=" + relative);
        }

        return (paths, mod, new LooseModelRewriteService(paths, Fs));
    }

    private static string Full(BoxPaths paths, string relative) =>
        Path.Combine(paths.FilesDirectory(1), relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Rewrite_MovesModelPrefabAndBonePaths_WithCompleteIdentity()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, LooseModelRewriteService service) = Setup(temp);

        LooseModelRewriteReport report = service.Rewrite(mod, "ch03/012/003", "ch03/015/007");

        // 模型本体、prefab 文件名、骨骼文件名都同步改
        const string newMesh = "natives/stm/art/model/character/ch03/015/007/5/ch03_015_0075.mesh.241111606";
        const string newPrefab =
            "natives/stm/gamedesign/equip/_prefab/armor/female/015/007/body/ch03_015_0072.pfb.18";
        const string newBone = "reframework/data/bonesystem/ch03_015_0071";
        Assert.Equal(3, report.FilesMoved);
        Assert.Contains(newMesh, mod.RelativeFiles);
        Assert.Contains(newPrefab, mod.RelativeFiles);
        Assert.Contains(newBone, mod.RelativeFiles);
        Assert.True(File.Exists(Full(paths, newMesh)));
        Assert.True(File.Exists(Full(paths, newPrefab)));
        Assert.True(File.Exists(Full(paths, newBone)));
        Assert.False(File.Exists(Full(paths, MeshPath)));
    }

    [Fact]
    public void Rewrite_DoesNotTouchScriptOrTexturePaths()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, LooseModelRewriteService service) = Setup(temp);

        service.Rewrite(mod, "ch03/012/003", "ch03/015/007");

        Assert.Contains(ScriptPath, mod.RelativeFiles);
        Assert.Contains(TexturePath, mod.RelativeFiles);
        Assert.True(File.Exists(Full(paths, ScriptPath)));
        Assert.True(File.Exists(Full(paths, TexturePath)));
    }

    /// <summary>批内两个源落到同一目标时整组跳过并告警，不能让第二个 Move 触发整批回滚。</summary>
    [Fact]
    public void Rewrite_DuplicateTargetsInBatch_SkipsAllAndWarns()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            RelativeFiles = [MeshPath, MeshPath],
        };
        string source = Full(paths, MeshPath);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mesh");

        LooseModelRewriteReport report = new LooseModelRewriteService(paths, Fs)
            .Rewrite(mod, "ch03/012/003", "ch03/015/007");

        Assert.Equal(0, report.FilesMoved);
        Assert.Contains(report.Warnings, warning => warning.Contains("本次另一文件"));
        Assert.Equal(2, mod.RelativeFiles.Count(file => string.Equals(file, MeshPath, StringComparison.OrdinalIgnoreCase)));
        Assert.True(File.Exists(source));
        Assert.DoesNotContain(mod.RelativeFiles, file => file.Contains("015/007", StringComparison.Ordinal));
    }

    [Fact]
    public void Rewrite_DoesNotChangeFileContent_OnlyPath()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, LooseModelRewriteService service) = Setup(temp);
        string source = Full(paths, MeshPath);
        byte[] before = File.ReadAllBytes(source);

        service.Rewrite(mod, "ch03/012/003", "ch03/015/007");

        const string newMesh = "natives/stm/art/model/character/ch03/015/007/5/ch03_015_0075.mesh.241111606";
        byte[] after = File.ReadAllBytes(Full(paths, newMesh));
        Assert.Equal(before, after); // 字节级断言：只改名，不替换内容（MODEL-009）
        Assert.Equal("path=" + MeshPath, Encoding.UTF8.GetString(after));
    }

    [Fact]
    public void Rewrite_ReportsRewrittenModelIdsAsCompleteIds()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRecord mod, LooseModelRewriteService service) = Setup(temp);

        LooseModelRewriteReport report = service.Rewrite(mod, "ch03/012/003", "ch03/015/007");

        Assert.Contains("015_007", report.ModelIds);
        Assert.Contains("015_007", ModelPathRecognizer.FindIdentities(mod.RelativeFiles).Select(i => i.Code));
    }

    [Fact]
    public void Rewrite_InvalidIdentity_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRecord mod, LooseModelRewriteService service) = Setup(temp);

        Assert.Throws<ArgumentException>(() => service.Rewrite(mod, "012", "015"));
        Assert.Throws<ArgumentException>(() => service.Rewrite(mod, "ch03/012/003", "ch04/015/007"));
    }

    [Fact]
    public void Rewrite_TypeMismatch_Throws()
    {
        using TempDir temp = TempDir.Create();
        (_, ModRecord mod, LooseModelRewriteService service) = Setup(temp);

        Assert.Throws<ArgumentException>(() => service.Rewrite(mod, "ch03/012/003", "ch05/012/0003"));
    }

    [Fact]
    public void Rewrite_MissingFilesDirectory_ReturnsEmptyReport()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        ModRecord mod = new() { Id = 9, Index = 1, RelativeFiles = [MeshPath] };

        LooseModelRewriteReport report = new LooseModelRewriteService(paths, Fs).Rewrite(mod, "ch03/012/003", "ch03/015/007");

        Assert.Equal(0, report.FilesMoved);
        Assert.Equal([MeshPath], mod.RelativeFiles);
    }

    [Fact]
    public void Rewrite_TargetAlreadyExists_KeepsSourceAndWarnsInsteadOfOverwrite()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRecord mod, LooseModelRewriteService service) = Setup(temp);

        // 目标路径已经存在另一份内容：改名必须跳过并告警，不能静默覆盖。
        const string occupied = "natives/stm/art/model/character/ch03/015/007/5/ch03_015_0075.mesh.241111606";
        string occupiedFull = Full(paths, occupied);
        Directory.CreateDirectory(Path.GetDirectoryName(occupiedFull)!);
        File.WriteAllText(occupiedFull, "existing");
        mod.RelativeFiles.Add(occupied);

        LooseModelRewriteReport report = service.Rewrite(mod, "ch03/012/003", "ch03/015/007");

        Assert.Contains(report.Warnings, w => w.Contains("已被占用"));
        Assert.Equal("existing", File.ReadAllText(occupiedFull));
        Assert.Contains(MeshPath, mod.RelativeFiles);
    }
}
