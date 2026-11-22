using System.Runtime.Versioning;
using HunterBox.Application.Activation;
using HunterBox.Application.Common;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Application.Mount;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

public class MountServiceTests
{
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";
    private const string GameId = "mhws";

    private static (BoxPaths Paths, string GameRoot, ModRecord Mod, MountService Service) Setup(
        TempDir temp,
        params ModRecord[] otherMods)
    {
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        string modFiles = paths.FilesDirectory(1);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Metadata = new ModMetadata { Name = "测试MOD" },
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };

        string source = Path.Combine(modFiles, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "from-mod");

        List<ModRecord> all = [mod, .. otherMods];
        MountService service = new(paths, gameRoot, GameId, all, HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Paks.FileContentFingerprint());
        return (paths, gameRoot, mod, service);
    }

    private static string GameFile(string gameRoot, string relative) =>
        Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Enable_CopiesFileIntoGameRoot()
    {
        using TempDir temp = TempDir.Create();
        (_, string gameRoot, ModRecord mod, MountService service) = Setup(temp);

        MountReport report = service.Enable(mod);

        Assert.Equal(1, report.Copied);
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    [Fact]
    public void Enable_WithInjectedUndoScopeFactory_UsesFactoryScope()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord mod = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, mod, "from-mod");
        RecordingUndoScopeFactory factory = new();
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint(),
            factory);

        service.Enable(mod);

        RecordingUndoScope scope = Assert.Single(factory.Scopes);
        Assert.True(scope.Committed, "成功启用后必须提交撤销作用域");
        Assert.Contains(GameFile(gameRoot, LooseFile), scope.Created);
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    [Fact]
    public void Enable_AfterRelativeFilesReplaced_UsesNewMapping()
    {
        using TempDir temp = TempDir.Create();
        const string NewFile = "natives/stm/art/model/character/ch03/y.mod3";
        (BoxPaths paths, string gameRoot, ModRecord mod, MountService service) = Setup(temp);

        string source = Path.Combine(
            paths.FilesDirectory(1),
            NewFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "new-mod");
        mod.RelativeFiles = [NewFile];

        // P-01：同一服务实例内文件清单被替换后，映射缓存必须失效并使用新清单。
        MountReport report = service.Enable(mod);

        Assert.Equal(1, report.Copied);
        Assert.Equal("new-mod", File.ReadAllText(GameFile(gameRoot, NewFile)));
        Assert.False(File.Exists(GameFile(gameRoot, LooseFile)));
    }

    /// <summary>跨批次残留的 MOD 内容不能被静默当成游戏原文件备份。</summary>
    [Fact]
    public void Enable_TargetMatchesDisabledModContent_WarnsBeforeBackup()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord disabled = NewMod(2, "已禁用MOD", LooseFile);
        WriteModFile(paths, disabled, "from-mod");
        ModRecord enabling = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, enabling, "from-mod");
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [enabling, disabled],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-mod");

        MountReport report = service.Enable(enabling);

        Assert.Contains(report.Warnings, warning => warning.Contains("可能不是游戏原文件", StringComparison.Ordinal));
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    /// <summary>没有已禁用同名候选时，残留比对完全不读目标文件。</summary>
    [Fact]
    public void Enable_NoDisabledCandidate_DoesNotFingerprintDestination()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord mod = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, mod, "from-mod");
        CountingFingerprint fingerprint = new();
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-game");

        MountReport report = service.Enable(mod);

        Assert.Empty(fingerprint.Calls);
        Assert.Empty(report.Warnings);
    }

    /// <summary>候选源长度不同直接跳过，不计算任何哈希。</summary>
    [Fact]
    public void Enable_DisabledCandidateDifferentLength_DoesNotFingerprint()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord disabled = NewMod(2, "已禁用MOD", LooseFile);
        WriteModFile(paths, disabled, "short");
        ModRecord enabling = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, enabling, "from-mod");
        CountingFingerprint fingerprint = new();
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [enabling, disabled],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "much-longer-content");

        MountReport report = service.Enable(enabling);

        Assert.Empty(fingerprint.Calls);
        Assert.Empty(report.Warnings);
    }

    /// <summary>长度相同才计算目标哈希，且每个目标最多一次。</summary>
    [Fact]
    public void Enable_DisabledCandidateSameLength_FingerprintsDestinationOnce()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord disabled = NewMod(2, "已禁用MOD", LooseFile);
        WriteModFile(paths, disabled, "same");
        ModRecord enabling = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, enabling, "same");
        CountingFingerprint fingerprint = new();
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [enabling, disabled],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "same");

        MountReport report = service.Enable(enabling);

        string destination = GameFile(gameRoot, LooseFile);
        Assert.Single(fingerprint.Calls.Where(call => string.Equals(call, destination, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(report.Warnings, warning => warning.Contains("可能不是游戏原文件", StringComparison.Ordinal));
    }

    /// <summary>已禁用记录里的非法相对路径不影响本次启用。</summary>
    [Fact]
    public void Enable_DisabledModHasInvalidPath_StillEnables()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord invalid = NewMod(2, "坏记录", "../outside.mod3");
        ModRecord enabling = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, enabling, "from-mod");
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [enabling, invalid],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new CountingFingerprint());
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-game");

        MountReport report = service.Enable(enabling);

        Assert.Equal(1, report.Copied);
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    /// <summary>一次启用只读取一次记录快照（残留索引与占用索引共用）。</summary>
    [Fact]
    public void Enable_ReadsModSnapshotOnce()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord disabled = NewMod(2, "已禁用MOD", LooseFile);
        WriteModFile(paths, disabled, "same");
        ModRecord enabling = NewMod(1, "测试MOD", LooseFile);
        WriteModFile(paths, enabling, "same");
        int providerCalls = 0;
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            () =>
            {
                providerCalls++;
                return [enabling, disabled];
            },
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new CountingFingerprint());
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "same");

        service.Enable(enabling);

        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public void Enable_BacksUpExistingGameFile()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRecord mod, MountService service) = Setup(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-game");

        MountReport report = service.Enable(mod);

        Assert.Equal(1, report.BackedUp);
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
        Assert.Equal("from-game", File.ReadAllText(paths.BackupFile(GameId, LooseFile)));
        Assert.Equal(
            Path.Combine(paths.BackupRootDirectory, GameId, "natives", "stm", "art", "model", "character", "ch03", "x.mod3"),
            paths.BackupFile(GameId, LooseFile));
    }

    [Fact]
    public void FindLooseFileConflicts_ReturnsPathClaimedByOtherEnabledMod()
    {
        using TempDir temp = TempDir.Create();
        ModRecord other = new()
        {
            Id = 2,
            Index = 1,
            GameId = GameId,
            Enabled = true,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        ModRecord mod = new()
        {
            Id = 1,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        BoxPaths paths = new(temp.Path);
        MountService service = new(paths, temp.Dir("game"), GameId, [other, mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        IReadOnlyList<string> conflicts = service.FindLooseFileConflicts(mod);

        Assert.Equal([LooseFile], conflicts);
    }

    [Fact]
    public void Disable_DeletesFileAndPrunesEmptyDirectories()
    {
        using TempDir temp = TempDir.Create();
        (_, string gameRoot, ModRecord mod, MountService service) = Setup(temp);
        service.Enable(mod);

        MountReport report = service.Disable(mod);

        Assert.Equal(1, report.Deleted);
        Assert.False(File.Exists(GameFile(gameRoot, LooseFile)));
        Assert.False(Directory.Exists(Path.Combine(gameRoot, "natives")), "空目录应被清理");
        Assert.True(Directory.Exists(gameRoot), "游戏根目录本身必须保留");
    }

    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Disable_UnreadableParentDirectory_StillSucceedsWithWarning()
    {
        using TempDir temp = TempDir.Create();
        (_, string gameRoot, ModRecord mod, MountService service) = Setup(temp);
        service.Enable(mod);

        string pruneDirectory = Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!;
        using DirectoryListDeny deny = DirectoryListDeny.Apply(pruneDirectory);

        // 文件已撤出后空目录无法枚举：禁用仍必须成功，只记 warning（C-03）。
        MountReport report = service.Disable(mod);

        Assert.Equal(1, report.Deleted);
        Assert.False(File.Exists(GameFile(gameRoot, LooseFile)));
        Assert.Contains(report.Warnings, warning => warning.Contains("空目录清理失败"));
        Assert.True(Directory.Exists(pruneDirectory), "无权枚举时目录应保留");
    }

    [AclFact]
    [SupportedOSPlatform("windows")]
    public void Disable_OnePruneFailure_StillPrunesOtherDirectories()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["natives/a/x.dat", "natives/b/y.dat"],
        };
        foreach (string file in mod.RelativeFiles)
        {
            string source = Path.Combine(paths.FilesDirectory(1), file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "from-mod");
        }

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());
        service.Enable(mod);
        using DirectoryListDeny deny = DirectoryListDeny.Apply(Path.Combine(gameRoot, "natives", "a"));

        // 第一个目录清理失败不能阻止后续目录继续清理。
        MountReport report = service.Disable(mod);

        Assert.Equal(2, report.Deleted);
        Assert.Contains(report.Warnings, warning => warning.Contains("空目录清理失败"));
        Assert.True(Directory.Exists(Path.Combine(gameRoot, "natives", "a")), "无权限目录应保留");
        Assert.False(Directory.Exists(Path.Combine(gameRoot, "natives", "b")), "其它空目录仍应清理");
    }

    [Fact]
    public void Disable_RestoresBackupAndRemovesIt()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRecord mod, MountService service) = Setup(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-game");
        service.Enable(mod);

        MountReport report = service.Disable(mod);

        Assert.Equal(1, report.Restored);
        Assert.Equal("from-game", File.ReadAllText(GameFile(gameRoot, LooseFile)));
        Assert.False(File.Exists(paths.BackupFile(GameId, LooseFile)), "还原后备份应删除");
    }

    [Fact]
    public void Disable_TargetMissing_RestoresBackupAnyway()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRecord mod, MountService service) = Setup(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-game");
        service.Enable(mod);
        File.Delete(GameFile(gameRoot, LooseFile));

        // 目标被外部删除后仍必须还原备份并清理它（C-05）。
        MountReport report = service.Disable(mod);

        Assert.Equal(1, report.Restored);
        Assert.Equal("from-game", File.ReadAllText(GameFile(gameRoot, LooseFile)));
        Assert.False(File.Exists(paths.BackupFile(GameId, LooseFile)), "还原后备份应删除");
    }

    [Fact]
    public void Disable_KeepsFileNeededByAnotherEnabledMod()
    {
        using TempDir temp = TempDir.Create();
        ModRecord other = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Enabled = true,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };

        (_, string gameRoot, ModRecord mod, MountService service) = Setup(temp, other);
        service.Enable(mod);

        MountReport report = service.Disable(mod);

        Assert.Equal(0, report.Deleted);
        Assert.True(File.Exists(GameFile(gameRoot, LooseFile)), "另一个启用中的 MOD 还需要这个文件");
    }

    [Fact]
    public void Disable_IgnoresCrossGameModClaimingSameFile()
    {
        using TempDir temp = TempDir.Create();
        ModRecord otherGame = new()
        {
            Id = 2,
            Index = 2,
            GameId = "mhw",
            Enabled = true,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };

        (_, string gameRoot, ModRecord mod, MountService service) = Setup(temp, otherGame);
        service.Enable(mod);

        // 另一个游戏的记录即使 Enabled=true，也绝不能算作本游戏文件的占用方。
        MountReport report = service.Disable(mod);

        Assert.Equal(1, report.Deleted);
        Assert.False(File.Exists(GameFile(gameRoot, LooseFile)));
    }

    [Fact]
    public void EnableTwice_DoesNotBackUpOwnFile()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRecord mod, MountService service) = Setup(temp);

        service.Enable(mod);
        mod.Enabled = true;
        MountReport second = service.Enable(mod);

        Assert.Equal(0, second.BackedUp);
        Assert.False(File.Exists(paths.BackupFile(GameId, LooseFile)), "自己的文件不应被当成游戏原文件备份");
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    [Fact]
    public void EnableTwiceWithGameFile_BacksUpOnlyOnce()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRecord mod, MountService service) = Setup(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, LooseFile))!);
        File.WriteAllText(GameFile(gameRoot, LooseFile), "from-game");

        MountReport first = service.Enable(mod);
        mod.Enabled = true;
        MountReport second = service.Enable(mod);

        Assert.Equal(1, first.BackedUp);
        Assert.Equal(0, second.BackedUp);
        Assert.Equal("from-game", File.ReadAllText(paths.BackupFile(GameId, LooseFile)));
    }

    [Fact]
    public void CrossGameMod_IsRejected()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord otherGame = new()
        {
            Id = 1,
            Index = 1,
            GameId = "mhw",
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };

        MountService service = new(paths, gameRoot, GameId, [otherGame], HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        Assert.Throws<InvalidOperationException>(() => service.Enable(otherGame));
        Assert.Throws<InvalidOperationException>(() => service.Disable(otherGame));
    }

    [Fact]
    public void LuaMod_IsPlacedUnderAutorun()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.RefScripts,
            TargetDirectory = "reframework/autorun",
            RelativeFiles = ["weapon.lua"],
        };
        string source = Path.Combine(paths.FilesDirectory(1), "weapon.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "print('hi')");

        new MountService(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance).Enable(mod);

        Assert.True(File.Exists(GameFile(gameRoot, "reframework/autorun/weapon.lua")));
    }

    [Fact]
    public void ModFileEscapingGameRoot_IsRejected()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["../evil.txt"],
        };

        MountService service = new(paths, gameRoot, GameId, [mod], HunterBox.Infrastructure.IO.SystemFileSystem.Instance);

        Assert.ThrowsAny<ArgumentException>(() => service.Enable(mod));
    }

    // ---- P-09：重叠文件撤出时按「游戏路径 → 占用方」索引查表复制 ----

    [Fact]
    public void Disable_Overlap_CopiesBackContentFromOtherEnabledMod()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord first = NewMod(1, "先启用", LooseFile);
        ModRecord second = NewMod(2, "后启用", LooseFile);
        WriteModFile(paths, first, "from-mod-1");
        WriteModFile(paths, second, "from-mod-2");
        MountService service = new(paths, gameRoot, GameId, [first, second], HunterBox.Infrastructure.IO.SystemFileSystem.Instance, new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.Enable(first);
        first.Enabled = true;
        service.Enable(second);
        second.Enabled = true;
        Assert.Equal("from-mod-2", File.ReadAllText(GameFile(gameRoot, LooseFile)));

        MountReport report = service.Disable(second);
        second.Enabled = false;

        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Copied);
        Assert.True(File.Exists(GameFile(gameRoot, LooseFile)));
        Assert.Equal("from-mod-1", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    [Fact]
    public void Disable_Overlap_PrefersLatestEnabledOwnerLikeEnableOrder()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord first = NewMod(1, "第一个", LooseFile);
        ModRecord second = NewMod(2, "第二个", LooseFile);
        ModRecord third = NewMod(3, "第三个", LooseFile);
        WriteModFile(paths, first, "from-mod-1");
        WriteModFile(paths, second, "from-mod-2");
        WriteModFile(paths, third, "from-mod-3");
        MountService service = new(
            paths, gameRoot, GameId, [first, second, third],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.Enable(first);
        first.Enabled = true;
        service.Enable(second);
        second.Enabled = true;
        service.Enable(third);
        third.Enabled = true;
        Assert.Equal("from-mod-3", File.ReadAllText(GameFile(gameRoot, LooseFile)));

        MountReport report = service.Disable(third);
        third.Enabled = false;

        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Copied);
        Assert.True(
            File.ReadAllText(GameFile(gameRoot, LooseFile)) == "from-mod-2",
            "后加载的 MOD 生效：应回填列表顺序更靠后的占用方内容");
    }

    [Fact]
    public void Disable_Overlap_FallsBackWhenLaterOwnerSourceMissing()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord first = NewMod(1, "第一个", LooseFile);
        ModRecord second = NewMod(2, "第二个", LooseFile);
        ModRecord third = NewMod(3, "第三个", LooseFile);
        WriteModFile(paths, first, "from-mod-1");
        WriteModFile(paths, second, "from-mod-2");
        WriteModFile(paths, third, "from-mod-3");
        MountService service = new(
            paths, gameRoot, GameId, [first, second, third],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.Enable(first);
        first.Enabled = true;
        service.Enable(second);
        second.Enabled = true;
        service.Enable(third);
        third.Enabled = true;

        // 模拟杀软隔离/用户删除：靠后的占用方源文件缺失，应继续往前找可用源。
        Directory.Delete(paths.FilesDirectory(second.Id), recursive: true);

        MountReport report = service.Disable(third);
        third.Enabled = false;

        Assert.Equal(0, report.Deleted);
        Assert.Equal(1, report.Copied);
        Assert.Equal("from-mod-1", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    [Fact]
    public void Disable_Overlap_CopiesFromOwnerMappedByTargetDirectory()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord plain = NewMod(1, "散装", "reframework/autorun/x.lua");
        ModRecord prefixed = NewMod(2, "带前缀", "x.lua", targetDirectory: "reframework/autorun");
        WriteModFile(paths, plain, "from-mod-1");
        WriteModFile(paths, prefixed, "from-mod-2");
        MountService service = new(
            paths, gameRoot, GameId, [plain, prefixed],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.Enable(plain);
        plain.Enabled = true;
        service.Enable(prefixed);
        prefixed.Enabled = true;

        string gameFile = GameFile(gameRoot, "reframework/autorun/x.lua");
        Assert.Equal("from-mod-2", File.ReadAllText(gameFile));

        MountReport report = service.Disable(prefixed);
        prefixed.Enabled = false;

        Assert.Equal(1, report.Copied);
        Assert.True(
            File.ReadAllText(gameFile) == "from-mod-1",
            "占用方索引必须按 MapToGamePath 建表（目标目录前缀也要算进去）");
    }

    // ---- A-07：启用失败的撤销日志由 FileUndoJournal 承担，回滚与撤销目录清理语义不变 ----

    [Fact]
    public void Enable_Failure_RestoresBackupAndCleansUndoRoot()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        const string gameFileA = "natives/stm/a.tex";
        const string gameFileB = "natives/stm/b.tex";

        string sourceA = Path.Combine(paths.FilesDirectory(1), gameFileA.Replace('/', Path.DirectorySeparatorChar));
        string sourceB = Path.Combine(paths.FilesDirectory(1), gameFileB.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourceA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceB)!);
        File.WriteAllText(sourceA, "MOD-A");
        File.WriteAllText(sourceB, "MOD-B");

        ModRecord mod = NewMod(1, "两个文件", gameFileA);
        mod.RelativeFiles.Add(gameFileB);

        Directory.CreateDirectory(Path.GetDirectoryName(GameFile(gameRoot, gameFileA))!);
        File.WriteAllText(GameFile(gameRoot, gameFileA), "GAME-A");
        string backup = paths.BackupFile(GameId, gameFileA);
        // 第二个文件的落点被同名目录占住 → 复制必然失败，验证第一个文件（含本次新建的备份）已回滚。
        Directory.CreateDirectory(GameFile(gameRoot, gameFileB));

        MountService service = new(paths, gameRoot, GameId, [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        IOException failure = Assert.ThrowsAny<IOException>(() => service.Enable(mod));

        Assert.Contains("回滚", failure.Message);
        Assert.Equal("GAME-A", File.ReadAllText(GameFile(gameRoot, gameFileA)));
        Assert.False(File.Exists(backup), "启用失败后本次新建的备份应清理");
        Assert.True(Directory.Exists(GameFile(gameRoot, gameFileB)), "失败现场（占位目录）不应被误删");
        Assert.False(
            Directory.Exists(paths.TempDirectory)
            && Directory.EnumerateDirectories(paths.TempDirectory, "undo-*").Any(),
            "启用失败后临时撤销目录必须清理");
    }

    /// <summary>记录挂载服务是否经工厂获取撤销作用域，以及登记了哪些补偿动作。</summary>
    private sealed class RecordingUndoScopeFactory : IUndoScopeFactory
    {
        public List<RecordingUndoScope> Scopes { get; } = [];

        public IUndoScope Create(string undoRoot, Action<string>? onRollbackApplied = null)
        {
            RecordingUndoScope scope = new();
            Scopes.Add(scope);
            return scope;
        }
    }

    /// <summary>不落盘、只记录调用的撤销作用域替身。</summary>
    private sealed class RecordingUndoScope : IUndoScope
    {
        public List<string> Created { get; } = [];

        public bool Committed { get; private set; }

        public void BackupFile(string destination)
        {
        }

        public void RecordCreated(string destination) => Created.Add(destination);

        public void MoveAside(string original)
        {
        }

        public void RecordRestore(string destination, string preImage, bool deletePreImageOnRollback = false)
        {
        }

        public void Rollback(List<string> warnings)
        {
        }

        public void Commit() => Committed = true;

        public void Dispose()
        {
        }
    }

    /// <summary>批量作用域内只撤出子集时，其余文件仍要保留在占用索引里。</summary>
    [Fact]
    public void Disable_SubsetInBatch_KeepsRemainingFilesInOwnerIndex()
    {
        using TempDir temp = TempDir.Create();
        const string FileA = "natives/stm/art/model/character/ch03/a.mod3";
        const string FileB = "natives/stm/art/model/character/ch03/b.mod3";
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord first = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [FileA, FileB],
        };
        ModRecord second = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [FileB],
        };
        WriteSource(paths, first.Id, FileA, "first-a");
        WriteSource(paths, first.Id, FileB, "first-b");
        WriteSource(paths, second.Id, FileB, "second-b");
        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [first, second],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.Enable(first);
        service.Enable(second);
        service.BeginBatch();
        try
        {
            // 只撤出 FileA：FileB 的占用仍属于 first，second 禁用后应从 first 恢复而不是删除。
            service.Disable(first, [FileA]);
            service.Disable(second, [FileB]);
        }
        finally
        {
            service.EndBatch();
        }

        Assert.Equal("first-b", File.ReadAllText(GameFile(gameRoot, FileB)));
    }

    /// <summary>批量禁用时保留路径仍属于原 MOD，后续启用其它 MOD 不能把它当作游戏原文件备份。</summary>
    [Fact]
    public void Disable_BatchPreservedPath_KeepsOwnershipForLaterEnable()
    {
        using TempDir temp = TempDir.Create();
        const string Shared = "natives/stm/art/model/character/ch03/shared.mod3";
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);

        ModRecord first = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Enabled = true,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [Shared],
        };
        ModRecord second = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [Shared],
        };
        WriteSource(paths, first.Id, Shared, "first");
        WriteSource(paths, second.Id, Shared, "second");

        // 现场：游戏目录里是 first 部署的内容，但没有备份可证明归属；first 的源文件又被杀软隔离。
        string gameFile = GameFile(gameRoot, Shared);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "first");
        File.Delete(Path.Combine(paths.FilesDirectory(first.Id), Shared.Replace('/', Path.DirectorySeparatorChar)));

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [first, second],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        try
        {
            MountReport disabled = service.Disable(first);
            Assert.Contains(disabled.PreservedPaths, path => path.Equals(Shared, StringComparison.OrdinalIgnoreCase));

            MountReport enabled = service.Enable(second);

            // 保留路径仍属于 first：second 启用只能是普通覆盖，不能把 first 的残留内容备份成“游戏原文件”。
            Assert.Equal(0, enabled.BackedUp);
        }
        finally
        {
            service.EndBatch();
        }

        Assert.False(File.Exists(paths.BackupFile(GameId, Shared)));
        Assert.Equal("second", File.ReadAllText(gameFile));
    }

    private static void WriteSource(BoxPaths paths, int modId, string relative, string content)
    {
        string source = Path.Combine(paths.FilesDirectory(modId), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, content);
    }

    /// <summary>记录每次调用的指纹实现：用于断言残留比对不产生额外整文件哈希。</summary>
    private sealed class CountingFingerprint : IContentFingerprint
    {
        private readonly IContentFingerprint _inner = new HunterBox.Infrastructure.Paks.FileContentFingerprint();

        /// <summary>按调用顺序记录的路径。</summary>
        public List<string> Calls { get; } = [];

        public string GetFingerprint(string fullPath)
        {
            Calls.Add(fullPath);
            return _inner.GetFingerprint(fullPath);
        }
    }

    /// <summary>没有已禁用候选时，一次启用内候选索引只构建一次（空结果必须被记住）。</summary>
    [Fact]
    public void Enable_NoDisabledCandidates_BuildsDisabledIndexOncePerEnable()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        const string FileA = "natives/stm/art/model/character/ch03/a.mod3";
        const string FileB = "natives/stm/art/model/character/ch03/b.mod3";
        const string FileC = "natives/stm/art/model/character/ch03/c.mod3";
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [FileA, FileB, FileC],
        };
        foreach (string file in mod.RelativeFiles)
        {
            string source = Path.Combine(paths.FilesDirectory(mod.Id), file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "mod");
            string destination = GameFile(gameRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "game-original");
        }

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        MountReport report = service.Enable(mod);

        Assert.Equal(3, report.BackedUp);
        Assert.Equal(1, service.DisabledSourceIndexBuildCount);
    }

    /// <summary>批量作用域内候选索引只在批量开始时构建一次，后续启用复用增量索引。</summary>
    [Fact]
    public void Enable_BatchWithTwoMods_BuildsDisabledIndexOnce()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        const string FileA = "natives/stm/art/model/character/ch03/a.mod3";
        const string FileB = "natives/stm/art/model/character/ch03/b.mod3";
        ModRecord first = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [FileA],
        };
        ModRecord second = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [FileB],
        };
        WriteSourceFile(paths, first.Id, FileA, "first");
        WriteSourceFile(paths, second.Id, FileB, "second");
        foreach (string file in new[] { FileA, FileB })
        {
            string destination = GameFile(gameRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "game-original");
        }

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [first, second],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        try
        {
            service.Enable(first);
            service.Enable(second);
        }
        finally
        {
            service.EndBatch();
        }

        Assert.Equal(1, service.DisabledSourceIndexBuildCount);
    }

    /// <summary>已启用记录含非法路径时，启用其它 MOD 仍成功，并把该记录降级为告警。</summary>
    [Fact]
    public void Enable_EnabledRecordWithInvalidPath_SucceedsWithWarning()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord broken = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Enabled = true,
            Metadata = new ModMetadata { Name = "坏记录" },
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["../escape.mod3"],
        };
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        WriteSourceFile(paths, mod.Id, LooseFile, "from-mod");
        string destination = GameFile(gameRoot, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "game-original");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod, broken],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        MountReport report = service.Enable(mod);

        Assert.Equal(1, report.BackedUp);
        Assert.Contains(report.Warnings, warning => warning.Contains("非法路径", StringComparison.Ordinal));
        Assert.Equal("from-mod", File.ReadAllText(destination));
    }

    /// <summary>纯禁用批次不需要候选索引，构建次数必须为 0。</summary>
    [Fact]
    public void DisableOnlyBatch_DoesNotBuildDisabledIndex()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Enabled = true,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        WriteSourceFile(paths, mod.Id, LooseFile, "from-mod");
        string destination = GameFile(gameRoot, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "from-mod");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        try
        {
            service.Disable(mod);
        }
        finally
        {
            service.EndBatch();
        }

        Assert.Equal(0, service.DisabledSourceIndexBuildCount);
    }

    /// <summary>候选索引跳过含非法路径的已禁用记录时必须告警，而不是静默跳过。</summary>
    [Fact]
    public void Enable_DisabledRecordWithInvalidPath_ReportsResidueScanSkip()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord broken = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Metadata = new ModMetadata { Name = "坏候选" },
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["../escape.mod3"],
        };
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        WriteSourceFile(paths, mod.Id, LooseFile, "from-mod");
        string destination = GameFile(gameRoot, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "game-original");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod, broken],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        MountReport report = service.Enable(mod);

        Assert.Contains(report.Warnings, warning => warning.Contains("坏候选", StringComparison.Ordinal));
    }

    /// <summary>冲突预检抛异常后不能留下已进入的批量作用域，下一次启用重新构建索引。</summary>
    [Fact]
    public void FindLooseFileConflicts_Throws_DoesNotKeepPreparedMount()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, ModRepository store) = TestBox.OpenRepository(temp);
        ModRecord broken = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["../escape.mod3"],
        };
        store.Add(broken);
        store.Save();
        ModActivationService service = new(
            paths,
            store,
            temp.Dir("game"),
            TestGames.Wilds,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint(),
            new ActivationOptions
            {
                FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            });

        Assert.Throws<ArgumentException>(() => { service.FindLooseFileConflicts(broken); });

        Assert.False(service.HasPreparedMount);
    }

    /// <summary>同一坏记录在批次内只告警一次，后续报告不再重复。</summary>
    [Fact]
    public void Batch_InvalidDisabledRecord_WarnsOnlyOnce()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord broken = new()
        {
            Id = 9,
            Index = 9,
            GameId = GameId,
            Metadata = new ModMetadata { Name = "坏候选" },
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["../escape.mod3"],
        };
        ModRecord first = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        ModRecord second = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = ["natives/stm/art/model/character/ch03/other.mod3"],
        };
        WriteSourceFile(paths, first.Id, LooseFile, "first");
        WriteSourceFile(paths, second.Id, second.RelativeFiles[0], "second");
        string destination = GameFile(gameRoot, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "game-original");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [first, second, broken],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        try
        {
            MountReport firstReport = service.Enable(first);
            MountReport secondReport = service.Enable(second);

            Assert.Contains(firstReport.Warnings, warning => warning.Contains("坏候选", StringComparison.Ordinal));
            Assert.DoesNotContain(secondReport.Warnings, warning => warning.Contains("坏候选", StringComparison.Ordinal));
        }
        finally
        {
            service.EndBatch();
        }
    }

    /// <summary>批次内后发生的候选告警仍会出现在对应报告里。</summary>
    [Fact]
    public void Batch_LaterCandidateWarning_AppearsInLaterReport()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        const string otherFile = "natives/stm/art/model/character/ch03/other.mod3";
        ModRecord first = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        ModRecord later = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Enabled = true,
            Metadata = new ModMetadata { Name = "后禁用者" },
            Kind = ModKind.LooseFiles,
            RelativeFiles = [otherFile, "../escape.mod3"],
        };
        WriteSourceFile(paths, first.Id, LooseFile, "first");
        WriteSourceFile(paths, later.Id, otherFile, "later");
        string destination = GameFile(gameRoot, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "game-original");
        string laterGameFile = GameFile(gameRoot, otherFile);
        Directory.CreateDirectory(Path.GetDirectoryName(laterGameFile)!);
        File.WriteAllText(laterGameFile, "game-later");
        string laterBackup = paths.BackupFile(GameId, otherFile);
        Directory.CreateDirectory(Path.GetDirectoryName(laterBackup)!);
        File.WriteAllText(laterBackup, "game-later");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [first, later],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        try
        {
            MountReport firstReport = service.Enable(first);
            MountReport disableReport = service.Disable(later, [otherFile]);

            Assert.DoesNotContain(firstReport.Warnings, warning => warning.Contains("后禁用者", StringComparison.Ordinal));
            Assert.Contains(disableReport.Warnings, warning => warning.Contains("后禁用者", StringComparison.Ordinal));
        }
        finally
        {
            service.EndBatch();
        }
    }

    /// <summary>批量索引维护失败不能影响已完成的启用，也不能触发已提交日志的回滚。</summary>
    [Fact]
    public void Enable_BatchIndexUpdateFails_KeepsCopiedFilesAndReportsWarning()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            // 文件清单含非法路径：本次只启用合法子集，随后的批量索引维护会因全量映射失败而抛异常。
            RelativeFiles = [LooseFile, "../escape.mod3"],
        };
        WriteSourceFile(paths, mod.Id, LooseFile, "from-mod");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [mod],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        MountReport report;
        try
        {
            report = service.Enable(mod, [LooseFile]);
        }
        finally
        {
            service.EndBatch();
        }

        Assert.Equal(1, report.Copied);
        Assert.Contains(report.Warnings, warning => warning.Contains("批量索引更新失败", StringComparison.Ordinal));
        Assert.Equal("from-mod", File.ReadAllText(GameFile(gameRoot, LooseFile)));
    }

    /// <summary>批次内先启用再禁用后，该 MOD 的残留仍应参与后续启用的残留告警。</summary>
    [Fact]
    public void Enable_AfterDisableInSameBatch_StillReportsLeftoverFromDisabledMod()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot) = TestBox.Layout(temp);
        ModRecord first = new()
        {
            Id = 1,
            Index = 1,
            GameId = GameId,
            Metadata = new ModMetadata { Name = "先启用者" },
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        ModRecord second = new()
        {
            Id = 2,
            Index = 2,
            GameId = GameId,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        WriteSourceFile(paths, first.Id, LooseFile, "first-content");
        WriteSourceFile(paths, second.Id, LooseFile, "second-content");
        string destination = GameFile(gameRoot, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "game-original");

        MountService service = new(
            paths,
            gameRoot,
            GameId,
            [first, second],
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            new HunterBox.Infrastructure.Paks.FileContentFingerprint());

        service.BeginBatch();
        try
        {
            service.Enable(first);
            first.Enabled = true;
            // 正常撤出：备份还原游戏原文件，first 不再占用该路径。
            service.Disable(first);
            first.Enabled = false;
            // 模拟外部把 first 的内容重新放回游戏目录（已禁用 MOD 残留）。
            File.WriteAllText(destination, "first-content");

            MountReport report = service.Enable(second);

            Assert.Equal(1, report.BackedUp);
            Assert.Contains(
                report.Warnings,
                warning => warning.Contains("先启用者", StringComparison.Ordinal));
        }
        finally
        {
            service.EndBatch();
        }
    }

    private static void WriteSourceFile(BoxPaths paths, int modId, string relative, string content)
    {
        string source = Path.Combine(paths.FilesDirectory(modId), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, content);
    }

    private static ModRecord NewMod(int id, string name, string file, string? targetDirectory = null) => new()
    {
        Id = id,
        Index = id,
        GameId = GameId,
        Metadata = new ModMetadata { Name = name },
        Kind = ModKind.LooseFiles,
        TargetDirectory = targetDirectory,
        RelativeFiles = [file],
    };

    private static void WriteModFile(BoxPaths paths, ModRecord mod, string content)
    {
        string relative = mod.RelativeFiles[0];
        string source = Path.Combine(paths.FilesDirectory(mod.Id), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, content);
    }
}
