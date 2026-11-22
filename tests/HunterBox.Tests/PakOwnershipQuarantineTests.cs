using HunterBox.Application.Paks;
using HunterBox.Domain.Games;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.IO;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>无法证明归属的同名 pak 不永久覆盖，禁用时大小写变体不误删。</summary>
public class PakOwnershipQuarantineTests
{
    private const string PakName = "foo.pak";

    private static PakMountService CreateService(BoxPaths paths, string gameRoot, params ModRecord[] mods) =>
        new(paths, gameRoot, TestGames.Wilds.Id, mods, SystemFileSystem.Instance, new FileContentFingerprint());

    [Fact]
    public void Enable_DifferentUnownedSameName_MovesOldFileToQuarantineAndWarns()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string gameRoot = temp.Dir("game");
        string pakMods = Path.Combine(gameRoot, "pak_mods");
        Directory.CreateDirectory(pakMods);
        File.WriteAllText(Path.Combine(pakMods, PakName), "user-content");
        string sourceFile = temp.File("source/foo.pak", "mod-content");
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [PakName],
        };
        PakMountService service = CreateService(paths, gameRoot, mod);
        PakPlan plan = new([new PakAssignment(PakName, new PakSource(mod.Id, "测试 MOD", PakName, sourceFile))], []);

        PakMountReport report = service.Enable(plan);

        Assert.Equal("mod-content", File.ReadAllText(Path.Combine(pakMods, PakName)));
        string quarantine = Assert.Single(Directory.GetDirectories(pakMods, ".hb-overwritten"));
        Assert.Equal("user-content", File.ReadAllText(Path.Combine(quarantine, PakName)));
        Assert.Contains(report.Warnings, warning => warning.Contains(quarantine, StringComparison.Ordinal));
    }

    /// <summary>连续冲突复用同一隔离目录，不为每次冲突新建 GUID 目录。</summary>
    [Fact]
    public void Enable_RepeatedConflict_ReusesSingleQuarantineDirectory()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string gameRoot = temp.Dir("game");
        string pakMods = Path.Combine(gameRoot, "pak_mods");
        Directory.CreateDirectory(pakMods);
        string sourceFile = temp.File("source/foo.pak", "mod-content");
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [PakName],
        };
        PakMountService service = CreateService(paths, gameRoot, mod);
        PakPlan plan = new([new PakAssignment(PakName, new PakSource(mod.Id, "测试 MOD", PakName, sourceFile))], []);

        File.WriteAllText(Path.Combine(pakMods, PakName), "user-content-1");
        service.Enable(plan);
        File.WriteAllText(Path.Combine(pakMods, PakName), "user-content-2");
        service.Enable(plan);

        string quarantine = Assert.Single(Directory.GetDirectories(pakMods, ".hb-overwritten"));
        Assert.Equal(2, Directory.GetFiles(quarantine).Length);
    }

    /// <summary>隔离后复制失败且回滚完整时，不残留本次隔离副本。</summary>
    [Fact]
    public void Enable_CopyFailsAfterQuarantine_RollbackRemovesQuarantineCopy()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string gameRoot = temp.Dir("game");
        string pakMods = Path.Combine(gameRoot, "pak_mods");
        Directory.CreateDirectory(pakMods);
        File.WriteAllText(Path.Combine(pakMods, PakName), "user-content");
        string sourceFile = temp.File("source/foo.pak", "mod-content");
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [PakName],
        };
        using LockingFingerprint fingerprint = new(sourceFile);
        PakMountService service = new(
            paths,
            gameRoot,
            TestGames.Wilds.Id,
            [mod],
            SystemFileSystem.Instance,
            fingerprint);
        PakPlan plan = new([new PakAssignment(PakName, new PakSource(mod.Id, "测试 MOD", PakName, sourceFile))], []);

        Assert.ThrowsAny<IOException>(() => service.Enable(plan));

        Assert.Equal("user-content", File.ReadAllText(Path.Combine(pakMods, PakName)));
        string quarantine = Path.Combine(pakMods, ".hb-overwritten");
        Assert.True(!Directory.Exists(quarantine) || Directory.GetFiles(quarantine).Length == 0);
    }

    /// <summary>在第二次指纹计算后独占锁定源文件，让随后的复制失败（用于验证回滚清理隔离副本）。</summary>
    private sealed class LockingFingerprint : IContentFingerprint, IDisposable
    {
        private readonly string _source;
        private readonly IContentFingerprint _inner = new FileContentFingerprint();
        private FileStream? _lock;

        public LockingFingerprint(string source) => _source = source;

        public string GetFingerprint(string fullPath)
        {
            string value = _inner.GetFingerprint(fullPath);
            if (_lock is null && string.Equals(fullPath, _source, StringComparison.OrdinalIgnoreCase))
            {
                _lock = new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.None);
            }

            return value;
        }

        public void Dispose() => _lock?.Dispose();
    }

    [Fact]
    public void Disable_CaseVariantKeep_DoesNotDeleteRemainingModPak()
    {
        using TempDir temp = TempDir.Create();
        BoxPaths paths = new(temp.Path);
        string gameRoot = temp.Dir("game");
        string pakMods = Path.Combine(gameRoot, "pak_mods");
        Directory.CreateDirectory(pakMods);
        File.WriteAllText(Path.Combine(pakMods, PakName), "shared-content");
        ModRecord disabled = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = [PakName],
            Enabled = true,
        };
        ModRecord remaining = new()
        {
            Id = 2,
            Index = 2,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.PakFiles,
            RelativeFiles = ["Foo.pak"],
            Enabled = true,
        };
        string remainingSource = Path.Combine(paths.FilesDirectory(remaining.Id), "Foo.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(remainingSource)!);
        File.WriteAllText(remainingSource, "shared-content");
        PakMountService service = CreateService(paths, gameRoot, disabled, remaining);

        PakMountReport report = service.Disable(
            disabled,
            TestGames.Wilds.CreatePakProfile(PakNamingMode.KeepOriginalName));

        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(Path.Combine(pakMods, PakName)));
    }
}
