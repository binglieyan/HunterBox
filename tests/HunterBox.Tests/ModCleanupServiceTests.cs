using HunterBox.Application.Cleanup;
using HunterBox.Application.Common;
using HunterBox.Application.Mount;
using HunterBox.Application.Paks;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

public class ModCleanupServiceTests
{
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";
    private const string SharedFile = "natives/stm/art/model/character/ch03/shared.mod3";

    private static (BoxPaths Paths, string GameRoot, ModRepository Store, ModRecord Mod) Setup(
        TempDir temp,
        GameProfile? game = null)
    {
        GameProfile owner = game ?? TestGames.Wilds;
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp, owner);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = owner.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mod");
        store.Add(mod);
        store.Save();

        return (paths, gameRoot, store, mod);
    }

    private static ModCleanupService Create(BoxPaths paths, ModRepository store, string gameRoot, GameProfile? game = null) =>
        new(paths, store, gameRoot, game ?? TestGames.Wilds, TestFingerprints.Create(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, PakNamingMode.PatchNumbered);

    [Fact]
    public void Cleanup_BoxOnly_DisablesModsAndRemovesTheirFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);

        new ModActivationServiceFactory(paths, store, gameRoot).Enable(mod);
        Assert.True(File.Exists(Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar))));

        int disabled = Create(paths, store, gameRoot).Cleanup(CleanupScope.BoxInstalledOnly);

        Assert.Equal(1, disabled);
        Assert.False(mod.Enabled);
        Assert.False(File.Exists(Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(Directory.Exists(gameRoot), "游戏根目录本身要保留");
    }

    /// <summary>清理全部共用批作用域，禁用多个 MOD 只在最后统一保存一次索引。</summary>
    [Fact]
    public void Cleanup_BoxOnly_SavesRecordIndexOnceForWholeBatch()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, _) = TestBox.Create(temp, TestGames.Wilds);

        FakeModStore store = new();
        ModRecord first = new()
        {
            Id = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
            Enabled = true,
        };
        ModRecord second = new()
        {
            Id = 2,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [SharedFile],
            Enabled = true,
        };
        store.Add(first);
        store.Add(second);
        foreach (string relative in new[] { LooseFile, SharedFile })
        {
            string target = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "game");
        }

        int saves = 0;
        store.OnSave = () => saves++;

        int disabled = new ModCleanupService(
            paths,
            store,
            gameRoot,
            TestGames.Wilds,
            TestFingerprints.Create(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            PakNamingMode.PatchNumbered).Cleanup(CleanupScope.BoxInstalledOnly);

        Assert.Equal(2, disabled);
        Assert.Equal(1, saves);
        Assert.False(first.Enabled);
        Assert.False(second.Enabled);
    }

    /// <summary>清理多个 MOD 时每 50 项落盘一次心跳，崩溃最多丢失一个窗口。</summary>
    [Fact]
    public void Cleanup_BoxOnly_WritesSaveHeartbeatEveryFiftyMods()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, _) = TestBox.Create(temp, TestGames.Wilds);

        FakeModStore store = new();
        for (int id = 1; id <= 51; id++)
        {
            string relative = $"natives/stm/test/f{id:000}.mod3";
            store.Add(new ModRecord
            {
                Id = id,
                GameId = TestGames.Wilds.Id,
                Kind = ModKind.LooseFiles,
                RelativeFiles = [relative],
                Enabled = true,
            });

            string target = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "game");
        }

        int saves = 0;
        store.OnSave = () => saves++;

        int disabled = new ModCleanupService(
            paths,
            store,
            gameRoot,
            TestGames.Wilds,
            TestFingerprints.Create(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            PakNamingMode.PatchNumbered).Cleanup(CleanupScope.BoxInstalledOnly);

        Assert.Equal(51, disabled);
        Assert.Equal(2, saves);
    }

    /// <summary>仅心跳保存失败（最终保存成功）时，异常消息仍带已禁用数量与保守保留警告。</summary>
    [Fact]
    public void Cleanup_BoxOnly_HeartbeatFailure_ReportsProgressContext()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, _) = TestBox.Create(temp, TestGames.Wilds);

        FakeModStore store = new();
        for (int id = 1; id <= 50; id++)
        {
            string relative = $"natives/stm/test/f{id:000}.mod3";
            store.Add(new ModRecord
            {
                Id = id,
                GameId = TestGames.Wilds.Id,
                Kind = ModKind.LooseFiles,
                RelativeFiles = [relative],
                Enabled = true,
            });

            string target = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "game");
        }

        int saves = 0;
        store.OnSave = () =>
        {
            saves++;
            if (saves == 1)
            {
                throw new IOException("模拟心跳写入失败");
            }
        };

        OperationBusinessAggregateException ex = Assert.Throws<OperationBusinessAggregateException>(() => new ModCleanupService(
            paths,
            store,
            gameRoot,
            TestGames.Wilds,
            TestFingerprints.Create(),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            PakNamingMode.PatchNumbered).Cleanup(CleanupScope.BoxInstalledOnly));

        Assert.Contains("已禁用 50/50", ex.Message, StringComparison.Ordinal);
        Assert.Contains("心跳保存失败", ex.Message, StringComparison.Ordinal);
        Assert.Contains("警告：", ex.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(Assert.Single(ex.InnerExceptions));
    }

    [Fact]
    public void Cleanup_Everything_QuarantinesOnlyProvenResidualAndKeepsUserFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);

        const string BoxResidual = "reframework/plugins/box.dll";
        const string UserFile = "reframework/autorun/user.lua";
        mod.RelativeFiles.Add(BoxResidual);
        // 归属证明要求残留内容与 MOD 源文件一致：这里保留源文件副本。
        string residualSource = Path.Combine(paths.FilesDirectory(1), BoxResidual.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(residualSource)!);
        File.WriteAllText(residualSource, "residual");
        store.Save();

        string residualFull = Path.Combine(gameRoot, BoxResidual.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(residualFull)!);
        File.WriteAllText(residualFull, "residual");
        string userFull = Path.Combine(gameRoot, UserFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(userFull)!);
        File.WriteAllText(userFull, "user");

        // 清理不删除备份目录（失败残留的备份可能是唯一可恢复来源）；其它游戏的备份也必须保留。
        Directory.CreateDirectory(paths.BackupDirectory(TestGames.Wilds.Id));
        string otherGameBackup = paths.BackupFile(TestGames.World.Id, LooseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(otherGameBackup)!);
        File.WriteAllText(otherGameBackup, "other-game-backup");

        Create(paths, store, gameRoot).Cleanup(CleanupScope.Everything);

        Assert.False(File.Exists(residualFull), "记录能证明内容一致的HunterBox残留应从游戏目录移走");
        string quarantined = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.ModsDirectory, ".cleanup-quarantine"),
            "box.dll",
            SearchOption.AllDirectories));
        Assert.Equal("residual", File.ReadAllText(quarantined));
        Assert.True(File.Exists(userFull), "不在任何记录里的用户文件必须保留");
        Assert.True(Directory.Exists(paths.BackupDirectory(TestGames.Wilds.Id)), "备份目录不应被清理整删");
        Assert.True(File.Exists(otherGameBackup), "其它游戏的备份不能被当前游戏的清理删掉");
    }

    /// <summary>隔离文件保留游戏内相对路径，用户可按原路径手工恢复。</summary>
    [Fact]
    public void Cleanup_Everything_QuarantineKeepsRelativePathForRestore()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);

        const string Residual = "reframework/plugins/box.dll";
        mod.RelativeFiles.Add(Residual);
        string residualSource = Path.Combine(paths.FilesDirectory(1), Residual.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(residualSource)!);
        File.WriteAllText(residualSource, "residual");
        store.Save();

        string residualFull = Path.Combine(gameRoot, Residual.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(residualFull)!);
        File.WriteAllText(residualFull, "residual");

        Create(paths, store, gameRoot).Cleanup(CleanupScope.Everything);

        string gameQuarantine = Path.Combine(
            paths.ModsDirectory,
            ".cleanup-quarantine",
            TestGames.Wilds.Id);
        string batch = Assert.Single(Directory.GetDirectories(gameQuarantine));
        string quarantined = Assert.Single(Directory.EnumerateFiles(batch, "box.dll", SearchOption.AllDirectories));
        string relative = Path.GetRelativePath(batch, quarantined).Replace('\\', '/');
        Assert.Equal(Residual, relative);
        Assert.Equal("residual", File.ReadAllText(quarantined));
    }

    [Fact]
    public void Cleanup_Everything_KeepsRestoredGameOriginalFile()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mod-content");
        store.Add(mod);
        store.Save();

        string gameFile = Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");

        new ModActivationServiceFactory(paths, store, gameRoot).Enable(mod);
        Assert.Equal("mod-content", File.ReadAllText(gameFile));

        Create(paths, store, gameRoot).Cleanup(CleanupScope.Everything);

        Assert.True(File.Exists(gameFile), "Everything 清理必须保留刚还原的游戏原文件");
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(gameFile));
        Assert.False(mod.Enabled);
    }

    /// <summary>
    /// N-01 回归：第一次清理还原并删除备份后，第二次清理没有 protectedPaths，
    /// 只能靠「内容指纹仍等于 MOD 源文件」证明归属；游戏原文件必须保留。
    /// </summary>
    [Fact]
    public void Cleanup_Everything_SecondRun_MovesSameContentFileToQuarantine()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        // 关键场景：MOD 源文件与游戏原文件字节完全相同（整目录打包时很常见）。
        File.WriteAllText(source, "GAME-ORIGINAL");
        store.Add(mod);
        store.Save();

        string gameFile = Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");

        new ModActivationServiceFactory(paths, store, gameRoot).Enable(mod);
        ModCleanupService cleanup = Create(paths, store, gameRoot);

        cleanup.Cleanup(CleanupScope.Everything, CancellationToken.None);
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(gameFile));

        // 第二次清理：备份已在第一次还原后删除，protectedPaths 为空；内容相同无法证明归属，
        // 因此文件被移入隔离目录而不是直接删除。
        CleanupReport second = cleanup.Cleanup(CleanupScope.Everything, CancellationToken.None);

        Assert.False(File.Exists(gameFile), "第二次清理不再直接保留同内容文件，而是移入隔离目录");
        string preserved = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.ModsDirectory, ".cleanup-quarantine"),
            Path.GetFileName(gameFile),
            SearchOption.AllDirectories));
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(preserved));
        Assert.Contains(second.Warnings, warning => warning.Contains("隔离目录"));
    }

    /// <summary>
    /// N-01 回归：pak 记录的根层 *.pak 实际部署在 pak_mods，不能按 MapToGamePath 删除游戏根目录同名官方 pak。
    /// </summary>
    [Fact]
    public void Cleanup_Everything_PakRecord_DoesNotDeleteOfficialRootPak()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);

        const string rootPak = "re_chunk_000.pak.patch_999.pak";
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [rootPak],
        };
        string source = Path.Combine(paths.FilesDirectory(1), rootPak);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mod-pak");
        store.Add(mod);
        store.Save();

        string official = Path.Combine(gameRoot, rootPak);
        File.WriteAllText(official, "OFFICIAL-PAK");

        Create(paths, store, gameRoot).Cleanup(CleanupScope.Everything);

        Assert.True(File.Exists(official), "pak 记录的根层 pak 不能在游戏根目录被当残留删除");
        Assert.Equal("OFFICIAL-PAK", File.ReadAllText(official));
    }

    [Fact]
    public void Cleanup_Everything_MismatchedResidual_IsKeptWithWarning()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);

        // 目标存在但内容与 MOD 源文件不一致：无法证明归属，必须保留。
        string gameFile = Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "not-from-this-mod");

        CleanupReport report = Create(paths, store, gameRoot)
            .Cleanup(CleanupScope.Everything, CancellationToken.None);

        Assert.True(File.Exists(gameFile), "无法证明归属的残留文件必须保留");
        Assert.Contains(report.Warnings, warning => warning.Contains("不一致") && warning.Contains("已保留"));
    }

    /// <summary>N-08：禁用阶段因无法证明归属而保留文件时，告警必须并入清理报告。</summary>
    [Fact]
    public void Cleanup_Everything_IncludesDisableWarnings()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);

        new ModActivationServiceFactory(paths, store, gameRoot).Enable(mod);
        string gameFile = Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        // 没有原文件备份；目标被改写成无法证明归属的内容。
        File.WriteAllText(gameFile, "tampered");

        CleanupReport report = Create(paths, store, gameRoot)
            .Cleanup(CleanupScope.Everything, CancellationToken.None);

        Assert.Contains(report.Warnings, warning => warning.Contains("无法证明") && warning.Contains("已保留"));
    }

    /// <summary>取消前已隔离的文件必须在报告里给出隔离目录路径。</summary>
    [Fact]
    public void Cleanup_Everything_CanceledAfterQuarantine_ReportsQuarantinePath()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);
        const string secondFile = "natives/stm/art/model/character/ch03/y.mod3";
        mod.RelativeFiles.Add(secondFile);
        string secondSource = Path.Combine(paths.FilesDirectory(1), secondFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(secondSource)!);
        File.WriteAllText(secondSource, "second");
        store.Save();
        string secondGame = Path.Combine(gameRoot, secondFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(secondGame)!);
        File.WriteAllText(secondGame, "second");
        File.WriteAllText(
            Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar)),
            "mod");

        using CancellationTokenSource cts = new();
        CancelingFingerprint fingerprint = new(new FileContentFingerprint(), cts);
        ModCleanupService cleanup = new(
            paths,
            store,
            gameRoot,
            TestGames.Wilds,
            new PakFingerprintCache(fingerprint, HunterBox.Infrastructure.IO.SystemFileSystem.Instance),
            HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
            PakNamingMode.PatchNumbered);

        CleanupReport report = cleanup.Cleanup(CleanupScope.Everything, cts.Token);

        Assert.True(report.Canceled);
        Assert.Contains("隔离目录", report.Warnings[0]);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(paths.ModsDirectory, ".cleanup-quarantine"),
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Cleanup_IgnoresOtherGameRecords()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModRecord mod) = Setup(temp);

        ModRecord otherGame = new()
        {
            Id = 2,
            Index = 1,
            GameId = TestGames.World.Id,
            Enabled = true,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [SharedFile],
        };
        store.Add(otherGame);
        store.Save();

        // 另一个游戏「已启用」的记录：清理当前游戏不得改它的启用状态。
        int disabled = Create(paths, store, gameRoot).Cleanup(CleanupScope.BoxInstalledOnly);

        Assert.Equal(0, disabled);
        Assert.True(otherGame.Enabled);
        Assert.All(store.ModsFor(TestGames.World.Id), m => Assert.True(m.Enabled));
    }

    [Fact]
    public void Cleanup_RestoresBackupUsingMappedGamePath()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);

        const string luaFile = "tweak.lua";
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.RefScripts,
            TargetDirectory = "reframework/autorun",
            RelativeFiles = [luaFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), luaFile);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mod-lua");
        store.Add(mod);
        store.Save();

        // 游戏原本就有同名脚本：启用时必须按「映射后的游戏相对路径」备份。
        string gameFile = Path.Combine(gameRoot, "reframework", "autorun", luaFile);
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "from-game");

        new ModActivationServiceFactory(paths, store, gameRoot).Enable(mod);

        string mapped = MountService.MapToGamePath(mod, luaFile);
        string backup = paths.BackupFile(TestGames.Wilds.Id, mapped);
        Assert.True(File.Exists(backup), "备份应按游戏根目录相对路径存放");
        Assert.Equal("from-game", File.ReadAllText(backup));

        // 清理对「已启用」的 MOD 执行安全禁用，禁用时按映射路径还原备份。
        Create(paths, store, gameRoot).Cleanup(CleanupScope.BoxInstalledOnly);

        Assert.False(File.Exists(backup), "有备份时应还原并删除备份");
        Assert.Equal("from-game", File.ReadAllText(gameFile));
    }

    [Fact]
    public void Cleanup_BoxOnly_KeepsRestoredGameOriginalFile()
    {
        // / 回归：清理绝不能把「禁用时已还原的游戏原文件」再当残留删掉。
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mod-content");
        store.Add(mod);
        store.Save();

        string gameFile = Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");

        new ModActivationServiceFactory(paths, store, gameRoot).Enable(mod);
        Assert.Equal("mod-content", File.ReadAllText(gameFile));

        Create(paths, store, gameRoot).Cleanup(CleanupScope.BoxInstalledOnly);

        Assert.True(File.Exists(gameFile), "游戏原文件必须保留");
        Assert.Equal("GAME-ORIGINAL", File.ReadAllText(gameFile));
        Assert.False(mod.Enabled);
    }

    [Fact]
    public void Disable_MissingBackup_KeepsDestinationWithWarning()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);

        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [LooseFile],
        };
        string source = Path.Combine(paths.FilesDirectory(1), LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "mod-content");
        store.Add(mod);
        store.Save();

        string gameFile = Path.Combine(gameRoot, LooseFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(gameFile)!);
        File.WriteAllText(gameFile, "GAME-ORIGINAL");

        ModActivationServiceFactory factory = new(paths, store, gameRoot);
        factory.Enable(mod);

        // 模拟备份丢失 + 目标内容已无法证明属于该 MOD：禁用必须保守保留。
        string backup = paths.BackupFile(TestGames.Wilds.Id, MountService.MapToGamePath(mod, LooseFile));
        File.Delete(backup);
        File.WriteAllText(gameFile, "content-not-matching-mod-source");

        Application.Activation.ActivationReport report = factory.Disable(mod);

        Assert.True(File.Exists(gameFile), "无备份且无归属证据时禁用必须保留目标文件");
        Assert.Contains(report.Warnings, warning => warning.Contains("无法证明") && warning.Contains("已保留"));
    }

    /// <summary>前两次指纹计算后取消的测试替身（用于精确卡在隔离阶段）。</summary>
    private sealed class CancelingFingerprint : IContentFingerprint
    {
        private readonly IContentFingerprint _inner;
        private readonly CancellationTokenSource _cts;
        private int _calls;

        public CancelingFingerprint(IContentFingerprint inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        public string GetFingerprint(string fullPath)
        {
            string value = _inner.GetFingerprint(fullPath);
            if (++_calls == 2)
            {
                _cts.Cancel();
            }

            return value;
        }
    }

    /// <summary>测试里反复需要「启用某个 MOD」，封装一下避免重复装配。</summary>
    private sealed class ModActivationServiceFactory
    {
        private readonly Application.Activation.ModActivationService _service;

        public ModActivationServiceFactory(BoxPaths paths, ModRepository store, string gameRoot) =>
            _service = new(paths, store, gameRoot, TestGames.Wilds, new FileContentFingerprint(), new Application.Activation.ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });

        public void Enable(ModRecord mod) => _service.Enable(mod, PakNamingMode.PatchNumbered);

        public Application.Activation.ActivationReport Disable(ModRecord mod) =>
            _service.Disable(mod, PakNamingMode.PatchNumbered);
    }
}
