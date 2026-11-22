using HunterBox.Application.Activation;
using HunterBox.Application.Mount;
using HunterBox.Application.Paks;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;
using HunterBox.Infrastructure.Paks;

namespace HunterBox.Tests;

/// <summary>
/// A-05：<see cref="ModActivationService"/> 的挂载协作者改由最小端口注入。
/// </summary>
/// <remarks>
/// <para>
/// 用假端口模拟「pak 写入抛异常」「落盘抛异常」「散装撤出抛异常」等故障，
/// 固定跨服务补偿（回滚）路径：pak 失败要撤销已铺的散装文件、落盘失败要同时撤销 pak 与散装文件、
/// 禁用失败要恢复已撤出的文件；成功路径不得触发多余回滚。
/// </para>
/// <para>
/// 假端口不读写任何文件，因此这里只验证编排/补偿的调用契约；
/// 真实文件语义仍由 <see cref="ModActivationServiceTests"/>、<see cref="ActivationSafetyTests"/> 等集成测试覆盖。
/// </para>
/// </remarks>
public sealed class ActivationPortRollbackTests
{
    private const string LooseFile = "natives/stm/art/model/character/ch03/x.mod3";
    private const string PakFile = "cool.pak";

    private sealed record Harness(
        FakeModStore Store,
        FakeLooseFileMountService Mount,
        FakePakMountService Pak,
        ModActivationService Service,
        ModRecord Mod);

    /// <summary>装配注入假端口的用例；MOD 同时含根层 pak 与散装文件，两条链路都会参与。</summary>
    private static Harness CreateHarness(TempDir temp)
    {
        BoxPaths paths = new(temp.Path);
        FakeModStore store = new();
        ModRecord mod = new()
        {
            Id = 1,
            Index = 1,
            GameId = TestGames.Wilds.Id,
            Kind = ModKind.LooseFiles,
            RelativeFiles = [PakFile, LooseFile],
        };
        store.Add(mod);

        FakeLooseFileMountService mount = new();
        FakePakMountService pak = new();
        ModActivationService service = new(
            paths,
            store,
            temp.Dir("game"),
            TestGames.Wilds,
            new FileContentFingerprint(),
            new ActivationOptions
            {
                FileSystem = HunterBox.Infrastructure.IO.SystemFileSystem.Instance,
                MountServiceFactory = () => mount,
                PakServiceFactory = () => pak,
            });
        return new Harness(store, mount, pak, service, mod);
    }

    [Fact]
    public void FindLooseFileConflicts_GoesThroughInjectedPort()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Mount.Conflicts = ["natives/stm/shared.tex"];

        IReadOnlyList<string> conflicts = harness.Service.FindLooseFileConflicts(harness.Mod);

        Assert.Equal(["natives/stm/shared.tex"], conflicts);
        Assert.Equal(1, harness.Mount.FindConflictCalls);
        Assert.Equal(0, harness.Pak.PlanCalls);
    }

    [Fact]
    public void Enable_Success_UsesPortsWithoutRollback()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);

        ActivationReport report = harness.Service.Enable(harness.Mod, PakNamingMode.PatchNumbered);

        Assert.True(harness.Mod.Enabled);
        Assert.Equal(1, report.Loose.Copied);
        Assert.Equal(1, report.Pak!.Written);
        Assert.Equal(1, harness.Mount.EnableCalls);
        Assert.Equal(1, harness.Pak.PlanCalls);
        Assert.Equal(1, harness.Pak.EnableCalls);
        Assert.Equal(0, harness.Mount.DisableCalls);
        Assert.Equal(0, harness.Pak.DisableCalls);
    }

    [Fact]
    public void Enable_PakWriteThrows_RollsBackLooseFiles()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Pak.EnableFailure = new IOException("模拟 pak 写入失败");

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Enable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("模拟 pak 写入失败", failure.Message);
        Assert.Equal(1, harness.Mount.EnableCalls);
        // pak 写入即抛，本次没有可撤销的 pak；已铺的散装文件必须被撤销。
        Assert.Equal(1, harness.Mount.DisableCalls);
        Assert.True(
            harness.Mount.ModEnabledDuringDisable,
            "回滚散装时应临时把 MOD 标记为已启用，让挂载实现能认出自己的文件");
        Assert.Equal(0, harness.Pak.DisableCalls);
        Assert.False(harness.Mod.Enabled);
        Assert.False(harness.Store.Mods.Single().Enabled);
    }

    [Fact]
    public void Enable_SaveFails_RollsBackPakThenLooseFiles()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Store.FailSave = true;

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Enable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("模拟索引写入失败", failure.Message);
        // pak 已写入、散装已铺好，落盘失败时两者都要撤销。
        Assert.Equal(1, harness.Pak.DisableCalls);
        Assert.Equal(1, harness.Mount.DisableCalls);
        Assert.False(harness.Mod.Enabled);
        Assert.False(harness.Store.Mods.Single().Enabled);
    }

    [Fact]
    public void Enable_RollbackFailure_ReportsIncompleteRollback()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Pak.EnableFailure = new IOException("模拟 pak 写入失败");
        harness.Mount.DisableFailure = new IOException("模拟散装回滚失败");

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Enable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("未能完整回滚", failure.Message);
        Assert.Equal(1, harness.Mount.DisableCalls);
        Assert.False(harness.Mod.Enabled);
    }

    /// <summary>N-07：回滚成功时 warnings（例如无备份而保留）也必须进入抛出的异常文案。</summary>
    [Fact]
    public void Enable_PakWriteThrows_IncludesRollbackWarnings()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Pak.EnableFailure = new IOException("模拟 pak 写入失败");
        harness.Mount.DisableWarnings = ["无备份且无法证明归属，已保留：natives/stm/x.mod3"];

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Enable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("已保留", failure.Message);
        Assert.Contains("模拟 pak 写入失败", failure.Message);
    }

    [Fact]
    public void Enable_PakPlanConflict_TouchesNeitherMountNorPakWrite()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        PakSource incoming = new(1, "本 MOD", PakFile, Path.Combine(temp.Path, "incoming.pak"));
        PakSource owner = new(2, "其它 MOD", PakFile, Path.Combine(temp.Path, "owner.pak"));
        harness.Pak.Plan = new PakPlan([], [new PakConflict(PakFile, incoming, owner)]);

        Assert.Throws<PakConflictException>(() => harness.Service.Enable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Equal(0, harness.Mount.EnableCalls);
        Assert.Equal(0, harness.Pak.EnableCalls);
        Assert.False(harness.Mod.Enabled);
    }

    [Fact]
    public void Disable_LooseFailure_RestoresPakThroughPort()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Mod.Enabled = true;
        harness.Mount.DisableFailure = new IOException("模拟散装撤出失败");

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Disable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("模拟散装撤出失败", failure.Message);
        Assert.Equal(1, harness.Pak.DisableCalls);
        // 散装撤出失败时，已经删掉的 pak 必须靠端口重新写回。
        Assert.Equal(1, harness.Pak.EnableCalls);
        Assert.Equal(1, harness.Mount.DisableCalls);
        Assert.True(harness.Mod.Enabled);
    }

    [Fact]
    public void Disable_PakRestoreFailure_ReportsIncompleteRestore()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Mod.Enabled = true;
        harness.Mount.DisableFailure = new IOException("模拟散装撤出失败");
        harness.Pak.EnableFailure = new IOException("模拟 pak 恢复失败");

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Disable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("未能恢复", failure.Message);
        Assert.Equal(1, harness.Pak.DisableCalls);
        Assert.Equal(1, harness.Pak.EnableCalls);
        Assert.True(harness.Mod.Enabled);
    }

    [Fact]
    public void Disable_SaveFails_RestoresLooseAndPakThroughPorts()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Mod.Enabled = true;
        harness.Store.FailSave = true;

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Disable(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("模拟索引写入失败", failure.Message);
        // 文件已撤出但落盘失败：散装与 pak 都要恢复，MOD 记录保持启用。
        Assert.Equal(1, harness.Mount.DisableCalls);
        Assert.Equal(1, harness.Mount.EnableCalls);
        Assert.Equal(1, harness.Pak.DisableCalls);
        Assert.Equal(1, harness.Pak.EnableCalls);
        Assert.True(harness.Mod.Enabled);
    }

    [Fact]
    public void Recopy_LooseFailure_DoesNotRollbackPak()
    {
        using TempDir temp = TempDir.Create();
        Harness harness = CreateHarness(temp);
        harness.Mod.Enabled = true;
        harness.Mount.EnableFailure = new IOException("模拟散装复制失败");

        IOException failure = Assert.Throws<IOException>(
            () => harness.Service.Recopy(harness.Mod, PakNamingMode.PatchNumbered));

        Assert.Contains("重新复制失败", failure.Message);
        Assert.Equal(1, harness.Pak.EnableCalls);
        Assert.Equal(1, harness.Mount.EnableCalls);
        // 重新复制不能回滚可能来自上一次启用的既有 pak，否则 MOD 显示启用但游戏内失效。
        Assert.Equal(0, harness.Pak.DisableCalls);
        Assert.True(harness.Mod.Enabled);
    }

    /// <summary>假散装挂载端口：只记录调用并可注入失败，不触碰文件系统。</summary>
    private sealed class FakeLooseFileMountService : ILooseFileMountService
    {
        public int FindConflictCalls { get; private set; }

        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public IReadOnlyList<string> Conflicts { get; set; } = [];

        /// <summary>设置后 <see cref="Enable"/> 抛出该异常。</summary>
        public Exception? EnableFailure { get; set; }

        /// <summary>设置后 <see cref="Disable"/> 抛出该异常。</summary>
        public Exception? DisableFailure { get; set; }

        /// <summary>非空时 <see cref="Disable"/> 返回携带这些告警的报告。</summary>
        public IReadOnlyList<string> DisableWarnings { get; set; } = [];

        /// <summary>最近一次 <see cref="Disable"/> 调用时记录的 <see cref="ModRecord.Enabled"/>。</summary>
        public bool? ModEnabledDuringDisable { get; private set; }

        public IReadOnlyList<string> FindLooseFileConflicts(ModRecord mod)
        {
            FindConflictCalls++;
            return Conflicts;
        }

        public MountReport Enable(ModRecord mod, IEnumerable<string>? files = null)
        {
            EnableCalls++;
            return EnableFailure is not null
                ? throw EnableFailure
                : new MountReport(1, 0, 0, 0);
        }

        public MountReport Disable(ModRecord mod, IEnumerable<string>? files = null)
        {
            DisableCalls++;
            ModEnabledDuringDisable = mod.Enabled;
            return DisableFailure is not null
                ? throw DisableFailure
                : new MountReport(0, 0, 0, 1) { Warnings = DisableWarnings };
        }
    }

    /// <summary>假 pak 挂载端口：只记录调用并可注入失败/冲突计划，不触碰文件系统。</summary>
    private sealed class FakePakMountService : IPakMountService
    {
        public int PlanCalls { get; private set; }

        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        /// <summary>计划结果；默认无冲突、无分配。</summary>
        public PakPlan Plan { get; set; } = new([], []);

        /// <summary>设置后 <see cref="Enable"/> 抛出该异常。</summary>
        public Exception? EnableFailure { get; set; }

        /// <summary>设置后 <see cref="Disable"/> 抛出该异常。</summary>
        public Exception? DisableFailure { get; set; }

        public PakPlan PlanEnable(ModRecord mod, PakProfile profile, ICollection<string>? warnings = null)
        {
            PlanCalls++;
            return Plan;
        }

        public PakMountReport Enable(ModRecord mod, PakProfile profile)
        {
            EnableCalls++;
            return EnableFailure is not null ? throw EnableFailure : new PakMountReport(1, 0);
        }

        public PakMountReport Enable(PakPlan plan)
        {
            EnableCalls++;
            return EnableFailure is not null ? throw EnableFailure : new PakMountReport(1, 0);
        }

        public PakMountReport Disable(ModRecord mod, PakProfile profile)
        {
            DisableCalls++;
            return DisableFailure is not null ? throw DisableFailure : new PakMountReport(0, 1);
        }
    }
}
