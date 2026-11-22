using HunterBox.Application.Activation;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>
/// 启用/禁用的事务性与数据安全：
/// 重叠文件重新复制、 pak 源缺失容错、 中途失败回滚。
/// </summary>
public class ActivationSafetyTests
{
    private const string WildsPrefix = TestGames.WildsPatchPrefix;
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";
    private const string GameId = "mhws";

    private sealed record Box(
        BoxPaths Paths,
        string GameRoot,
        ModRepository Store,
        ModActivationService Service);

    private static Box Create(TempDir temp)
    {
        TestBox<ModRepository> box = TestBox.Create(temp);
        ModActivationService service = new(box.Paths, box.Store, box.GameRoot, box.Game, new FileContentFingerprint(), new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        return new Box(box.Paths, box.GameRoot, box.Store, service);
    }

    private static string SourcePath(Box box, int id, string file)
    {
        string source = Path.Combine(
            box.Paths.FilesDirectory(id),
            file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        return source;
    }

    private static ModRecord AddMod(Box box, int id, string content, string file = LooseFile)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [file],
        };
        File.WriteAllText(SourcePath(box, id, file), content);
        box.Store.Add(mod);
        return mod;
    }

    private static ModRecord AddPakMod(Box box, int id, string pakName, string content)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = GameId,
            Kind = ModKind.PakFiles,
            RelativeFiles = [pakName],
        };
        File.WriteAllText(SourcePath(box, id, pakName), content);
        box.Store.Add(mod);
        return mod;
    }

    private static string GameFile(Box box, string relative) =>
        Path.Combine(box.GameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private const PakNamingMode Mode = PakNamingMode.PatchNumbered;

    [Fact]
    public void Disable_OverlappingFile_RecopiesRemainingModAndKeepsBackup()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord first = AddMod(box, 1, "A-CONTENT");
        ModRecord second = AddMod(box, 2, "B-CONTENT");
        string gameFile = GameFile(box, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");
        string backup = box.Paths.BackupFile(GameId, LooseFile);

        box.Service.Enable(first, Mode);
        box.Service.Enable(second, Mode);
        Assert.Equal("B-CONTENT", File.ReadAllText(gameFile));

        ActivationReport report = box.Service.Disable(second, Mode);

        Assert.Equal("A-CONTENT", File.ReadAllText(gameFile));
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(backup));
        Assert.False(second.Enabled);
        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.Loose.Copied);

        // 最后一个占用方禁用时才还原游戏原文件。
        box.Service.Disable(first, Mode);
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(gameFile));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void Disable_OverlappingFileWithMissingSource_KeepsContentAndWarns()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord first = AddMod(box, 1, "A-CONTENT");
        ModRecord second = AddMod(box, 2, "B-CONTENT");
        string gameFile = GameFile(box, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");
        box.Service.Enable(first, Mode);
        box.Service.Enable(second, Mode);

        // 另一个启用中 MOD的源文件被手删/隔离：禁用后者不能卡死，也不能丢掉备份。
        File.Delete(SourcePath(box, 1, LooseFile));

        ActivationReport report = box.Service.Disable(second, Mode);

        Assert.Equal("B-CONTENT", File.ReadAllText(GameFile(box, LooseFile)));
        Assert.True(File.Exists(box.Paths.BackupFile(GameId, LooseFile)), "备份应保留给最后一个占用方");
        Assert.Contains(report.Warnings, w => w.Contains("找不到可用源文件"));
        Assert.False(second.Enabled);
    }

    [Fact]
    public void Disable_MissingPakSource_KeepsUnprovenPakWithWarning()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord mod = AddPakMod(box, 1, "cool.pak", "pak-bytes");

        box.Service.Enable(mod, Mode);
        string deployed = Path.Combine(box.GameRoot, "pak_mods", WildsPrefix + "016.pak");
        Assert.True(File.Exists(deployed));

        // 源 pak 被手删/杀软隔离。
        File.Delete(SourcePath(box, 1, "cool.pak"));

        ActivationReport report = box.Service.Disable(mod, Mode);

        Assert.True(File.Exists(deployed), "无法证明归属时宁可保留，也不误删 pak");
        Assert.NotNull(report.Pak);
        Assert.Equal(0, report.Pak!.Removed);
        Assert.Contains(report.Warnings, w => w.Contains("无法确认"));
        Assert.False(mod.Enabled);
    }

    [Fact]
    public void Enable_MissingPakSource_ThrowsBeforeTouchingLooseFiles()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord mod = AddPakMod(box, 1, "cool.pak", "pak-bytes");
        mod.RelativeFiles.Add(LooseFile);
        File.WriteAllText(SourcePath(box, 1, LooseFile), "loose");
        File.Delete(SourcePath(box, 1, "cool.pak"));

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(
            () => box.Service.Enable(mod, Mode));

        Assert.Contains("pak 源文件缺失", ex.Message);
        Assert.False(mod.Enabled);
        Assert.False(File.Exists(GameFile(box, LooseFile)), "pak 校验失败时散装文件不能先铺");
    }

    [Fact]
    public void Enable_LooseCopyFailure_RollsBackBackupAndCopiedFiles()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);

        const string fileA = "natives/stm/a.tex";
        const string fileB = "natives/stm/b.tex";
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [fileA, fileB],
        };
        File.WriteAllText(SourcePath(box, 1, fileA), "MOD-A");
        File.WriteAllText(SourcePath(box, 1, fileB), "MOD-B");
        box.Store.Add(mod);

        string gameA = GameFile(box, fileA);
        Directory.CreateDirectory(Path.GetDirectoryName(gameA)!);
        File.WriteAllText(gameA, "GAME-A");
        string backup = box.Paths.BackupFile(GameId, fileA);

        // 第二个文件的落点被一个同名目录占住 → 复制必然失败，验证第一个文件已回滚。
        Directory.CreateDirectory(GameFile(box, fileB));

        IOException failure = Assert.ThrowsAny<IOException>(() => box.Service.Enable(mod, Mode));
        Assert.Contains("回滚", failure.Message);

        Assert.Equal("GAME-A", File.ReadAllText(gameA));
        Assert.False(File.Exists(backup), "启用失败后本次新建的备份应清理");
        Assert.False(mod.Enabled);
        Assert.True(Directory.Exists(GameFile(box, fileB)), "失败现场（占位目录）不应被误删");
    }

    [Fact]
    public void Enable_PakWriteFailure_RollsBackLooseFiles()
    {
        using TempDir temp = TempDir.Create();
        Box box = Create(temp);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["cool.pak", LooseFile],
        };
        File.WriteAllText(SourcePath(box, 1, "cool.pak"), "pak-bytes");
        File.WriteAllText(SourcePath(box, 1, LooseFile), "loose");
        box.Store.Add(mod);

        // pak_mods 被一个同名文件占住 → pak 写入阶段失败，散装文件必须被撤销。
        File.WriteAllText(Path.Combine(box.GameRoot, "pak_mods"), "block");

        Assert.ThrowsAny<IOException>(() => box.Service.Enable(mod, Mode));

        Assert.False(File.Exists(GameFile(box, LooseFile)), "pak 写入失败后散装文件不能残留");
        Assert.False(mod.Enabled);
    }

    [Fact]
    public void Enable_SaveFailure_RollsBackLooseFilesAndStaysDisabled()
    {
        using TempDir temp = TempDir.Create();
        TestBox<FakeModStore> box = TestBox.Create(temp, _ => new FakeModStore());
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        File.WriteAllText(box.SourceFile(1, LooseFile), "MOD-CONTENT");
        box.Store.Add(mod);

        ModActivationService service = new(box.Paths, box.Store, box.GameRoot, box.Game, new FileContentFingerprint(), new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        string gameFile = box.GameFile(LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");

        box.Store.FailSave = true;

        Assert.ThrowsAny<IOException>(() => service.Enable(mod, Mode));

        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(gameFile));
        Assert.False(File.Exists(box.Paths.BackupFile(GameId, LooseFile)));
        Assert.False(mod.Enabled);
        Assert.False(box.Store.Mods.Single().Enabled);
    }

    [Fact]
    public void Disable_SaveFailure_RestoresEnabledStateAndFiles()
    {
        using TempDir temp = TempDir.Create();
        TestBox<FakeModStore> box = TestBox.Create(temp, _ => new FakeModStore());
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        File.WriteAllText(box.SourceFile(1, LooseFile), "MOD-CONTENT");
        box.Store.Add(mod);

        ModActivationService service = new(box.Paths, box.Store, box.GameRoot, box.Game, new FileContentFingerprint(), new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        string gameFile = box.GameFile(LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");

        service.Enable(mod, Mode);
        Assert.Equal("MOD-CONTENT", File.ReadAllText(gameFile));
        string backup = box.Paths.BackupFile(GameId, LooseFile);
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(backup));

        box.Store.FailSave = true;

        Assert.ThrowsAny<IOException>(() => service.Disable(mod, Mode));

        // 保存失败 → 恢复到「仍启用」的一致状态（文件 + 备份 + Enabled）。
        Assert.True(mod.Enabled);
        Assert.Equal("MOD-CONTENT", File.ReadAllText(gameFile));
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(backup));
    }
}
