using HunterBox.Application.Activation;
using HunterBox.Application.Cleanup;
using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;
using HunterBox.Infrastructure.Storage;

namespace HunterBox.Tests;

/// <summary>「删除全部MOD文件」取消时报告已完成项，且不执行后续破坏性清理。</summary>
public class ModCleanupCancellationTests
{
    private const string LooseA = "natives/stm/art/model/character/ch03/a.mod3";
    private const string LooseB = "natives/stm/art/model/character/ch03/b.mod3";

    private static (BoxPaths Paths, string GameRoot, ModRepository Store, ModActivationService Activation) Setup(TempDir temp)
    {
        (BoxPaths paths, string gameRoot, _, ModRepository store) = TestBox.Create(temp);
        ModActivationService activation = new(paths, store, gameRoot, TestGames.Wilds, new FileContentFingerprint(), new ActivationOptions { FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance });
        return (paths, gameRoot, store, activation);
    }

    private static ModRecord AddMod(
        BoxPaths paths, ModRepository store, ModActivationService activation, int id, string file)
    {
        ModRecord mod = new()
        {
            Id = id,
            Index = id,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [file],
        };
        string source = Path.Combine(paths.FilesDirectory(id), file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, $"content-{id}");
        store.Add(mod);
        store.Save();
        activation.Enable(mod, PakNamingMode.PatchNumbered);
        return mod;
    }

    [Fact]
    public void Cleanup_CanceledAfterFirst_KeepsRestEnabledAndSkipsEverythingDeletion()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModActivationService activation) = Setup(temp);
        ModRecord first = AddMod(paths, store, activation, 1, LooseA);
        ModRecord second = AddMod(paths, store, activation, 2, LooseB);

        // 清理前的残留目录：若取消发生在禁用阶段，这些目录必须保留（不做破坏性删除）。
        string autorun = Path.Combine(gameRoot, "reframework", "autorun");
        Directory.CreateDirectory(autorun);
        File.WriteAllText(Path.Combine(autorun, "x.lua"), "lua");

        using CancellationTokenSource cts = new();
        IProgress<(int Done, int Total)> progress = new SynchronousProgress<(int Done, int Total)>(value =>
        {
            if (value.Done == 1)
            {
                cts.Cancel();
            }
        });

        ModCleanupService service = new(paths, store, gameRoot, TestGames.Wilds, TestFingerprints.Create(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, PakNamingMode.PatchNumbered);
        CleanupReport report = service.Cleanup(CleanupScope.Everything, cts.Token, progress);

        Assert.True(report.Canceled);
        Assert.Equal(1, report.Disabled);
        Assert.Equal(2, report.Total);
        Assert.False(first.Enabled);
        Assert.True(second.Enabled, "未处理的 MOD 不能被当成已处理");
        Assert.False(File.Exists(Path.Combine(gameRoot, LooseA.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(gameRoot, LooseB.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(Directory.Exists(autorun), "取消后不应执行 Everything 的破坏性删除");
        Assert.True(File.Exists(Path.Combine(autorun, "x.lua")));
    }

    [Fact]
    public void Cleanup_WithoutCancel_ReportsCompleteAndKeepsUserFiles()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModActivationService activation) = Setup(temp);
        AddMod(paths, store, activation, 1, LooseA);

        string autorun = Path.Combine(gameRoot, "reframework", "autorun");
        Directory.CreateDirectory(autorun);
        File.WriteAllText(Path.Combine(autorun, "x.lua"), "lua");

        ModCleanupService service = new(paths, store, gameRoot, TestGames.Wilds, TestFingerprints.Create(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, PakNamingMode.PatchNumbered);
        CleanupReport report = service.Cleanup(CleanupScope.Everything, CancellationToken.None);

        Assert.False(report.Canceled);
        Assert.Equal(1, report.Disabled);
        Assert.True(Directory.Exists(autorun), "不在 MOD 记录里的用户目录/文件必须保留");
        Assert.True(File.Exists(Path.Combine(autorun, "x.lua")));
    }

    /// <summary>清理无取消版本仍返回「已禁用数量」（旧调用方/测试兼容）。</summary>
    [Fact]
    public void Cleanup_LegacyIntOverload_StillReturnsDisabledCount()
    {
        using TempDir temp = TempDir.Create();
        (BoxPaths paths, string gameRoot, ModRepository store, ModActivationService activation) = Setup(temp);
        AddMod(paths, store, activation, 1, LooseA);

        ModCleanupService service = new(paths, store, gameRoot, TestGames.Wilds, TestFingerprints.Create(), HunterBox.Infrastructure.IO.SystemFileSystem.Instance, PakNamingMode.PatchNumbered);
        Assert.Equal(1, service.Cleanup(CleanupScope.BoxInstalledOnly));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
