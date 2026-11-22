using HunterBox.Infrastructure.Startup;

namespace HunterBox.Tests;

/// <summary>单实例互斥体命名、所有权持有/释放与「第二个实例」检测。</summary>
/// <remarks>
/// <see cref="SingleInstanceGuard"/> 通过 <c>WaitOne(0)</c> 持有互斥体所有权，而 Win32 互斥体所有权是
/// <b>线程级</b>的：同一线程重复创建同名 guard 会被 Mutex 当成递归取得所有权，无法模拟「另一个实例」。
/// 因此「第二个实例」统一由独立工作线程创建（等价于另一个进程持有所有权），测试结束时再释放。
/// </remarks>
public class SingleInstanceGuardTests
{
    [Fact]
    public void SecondGuardWithSameName_IsNotFirstInstance()
    {
        string name = UniqueName("second");
        using WorkerInstance first = new(name, releaseOnStop: true);
        Assert.True(first.Guard.IsFirstInstance);

        using SingleInstanceGuard second = new(name);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void SecondGuardDisposed_DoesNotReleaseFirstInstanceOwnership()
    {
        string name = UniqueName("second-dispose");
        using WorkerInstance first = new(name, releaseOnStop: true);
        SingleInstanceGuard second = new(name);
        Assert.False(second.IsFirstInstance);

        // 第二个实例从未取得所有权，Dispose 只能关闭自己的句柄，不能把第一个实例的所有权放掉。
        second.Dispose();
        using SingleInstanceGuard third = new(name);
        Assert.False(third.IsFirstInstance);
    }

    [Fact]
    public void DifferentSessionOrUser_GetsIndependentInstance()
    {
        string suffix = Guid.NewGuid().ToString("N");
        using SingleInstanceGuard sessionOne = new(
            SingleInstanceGuard.BuildName("HunterBox", 1, "S-1-5-21-" + suffix));
        using SingleInstanceGuard sessionTwo = new(
            SingleInstanceGuard.BuildName("HunterBox", 2, "S-1-5-21-" + suffix));
        using SingleInstanceGuard otherUser = new(
            SingleInstanceGuard.BuildName("HunterBox", 1, "S-1-5-21-other-" + suffix));

        Assert.True(sessionOne.IsFirstInstance);
        Assert.True(sessionTwo.IsFirstInstance);
        Assert.True(otherUser.IsFirstInstance);
    }

    [Fact]
    public void BuildName_IsSessionLocalAndSanitized()
    {
        string name = SingleInstanceGuard.BuildName(@"Hunter\Box", 3, @"DOMAIN\user");

        Assert.StartsWith(@"Local\", name);
        Assert.Contains(".3.", name);
        Assert.DoesNotContain(@"\", name[6..]);
        Assert.DoesNotContain("/", name);
    }

    [Fact]
    public void BuildName_BlankUserFallsBackToDefault()
    {
        string name = SingleInstanceGuard.BuildName("HunterBox", 1, "  ");

        Assert.Equal(@"Local\HunterBox.1.default", name);
    }

    [Fact]
    public void CurrentName_ContainsSessionAndUser()
    {
        string name = SingleInstanceGuard.CurrentName();

        Assert.StartsWith(@"Local\HunterBox.", name);
    }

    [Fact]
    public void Dispose_ReleasesNameForNextLaunch()
    {
        string name = SingleInstanceGuard.BuildName("HunterBox", 1, "S-1-5-21-dispose");
        SingleInstanceGuard first = new(name);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        using SingleInstanceGuard second = new(name);
        Assert.True(second.IsFirstInstance);
    }

    /// <summary>
    /// C-30 回归：A 运行 → B 提示中 → A 退出 → C 启动。
    /// 只依赖 <c>createdNew</c> 的旧实现会因 B 仍持有互斥体句柄（内核对象存在）而把 C 误判为「已有实例」；
    /// 改为 <c>WaitOne(0)</c> 取得所有权后，C 必须能成为第一个实例。
    /// </summary>
    [Fact]
    public void FirstInstanceExits_WhileSecondStillAlive_ThirdInstanceIsFirst()
    {
        string name = UniqueName("handoff");

        // A（后台线程 = 另一个进程）持有所有权；B 在 A 存活时被判定为第二个实例。
        using WorkerInstance first = new(name, releaseOnStop: true);
        using SingleInstanceGuard second = new(name);
        Assert.False(second.IsFirstInstance);

        // A 正常退出：释放所有权，但 B 仍持有句柄，内核对象不会销毁。
        first.Dispose();

        using SingleInstanceGuard third = new(name);
        Assert.True(third.IsFirstInstance);
    }

    /// <summary>
    /// C-30 的崩溃时序：A 异常退出（未释放所有权，互斥体 abandoned，B 仍持有句柄）→ C 启动。
    /// C 的 <c>WaitOne(0)</c> 会抛 <see cref="AbandonedMutexException"/>，必须按第一个实例接管所有权，
    /// 否则「崩溃一次后再也打不开」。
    /// </summary>
    [Fact]
    public void FirstInstanceAbandoned_WhileSecondStillNotifying_ThirdInstanceStarts()
    {
        string name = UniqueName("crash-handoff");

        // A 退出时不释放所有权：模拟实例被强制结束。
        using WorkerInstance first = new(name, releaseOnStop: false);
        using SingleInstanceGuard second = new(name);
        Assert.False(second.IsFirstInstance);

        first.Dispose();

        // 测试稳定性：线程退出到内核标记 abandoned 之间有极短窗口，门禁用例并行跑 msbuild 时
        // 首次 WaitOne 可能仍未接管；与 AbandonedMutex_IsTreatedAsFirstInstance 一致做少量重试。
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using SingleInstanceGuard third = new(name);
            if (third.IsFirstInstance)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("崩溃实例的互斥体未在重试内按 abandoned 语义被接管");
    }

    /// <summary>上一个持有者线程已结束（abandoned）时，新 guard 必须按第一个实例接管。</summary>
    [Fact]
    public void AbandonedMutex_IsTreatedAsFirstInstance()
    {
        string name = UniqueName("abandoned");
        Mutex? crashedOwner = null;
        using ManualResetEventSlim acquired = new(false);

        Thread crashed = new(() =>
        {
            crashedOwner = new Mutex(initiallyOwned: false, name, out _);
            crashedOwner.WaitOne();
            acquired.Set();
            // 线程结束前不 ReleaseMutex：模拟实例崩溃/被结束，互斥体进入 abandoned 状态。
        });
        crashed.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(crashed.Join(TimeSpan.FromSeconds(10)));

        using Mutex holder = crashedOwner!;

        // 线程退出到内核标记 abandoned 之间可能有极短窗口；并行测试负载下偶发首次 WaitOne 未拿到。
        // 按真实语义重试少量次数（每次新建 guard），不改变产品断言方向。
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using (SingleInstanceGuard guard = new(name))
            {
                if (guard.IsFirstInstance)
                {
                    return;
                }
            }

            Thread.Sleep(1);
        }

        Assert.Fail("crashed owner 的互斥体未按 abandoned 语义被接管");
    }

    /// <summary>每个用例用独立的互斥体名，避免并行测试互相干扰（同进程内名字必须唯一）。</summary>
    /// <param name="tag">用例标识（便于排查）。</param>
    /// <returns>带 <c>Local\</c> 前缀的唯一互斥体名。</returns>
    private static string UniqueName(string tag) =>
        SingleInstanceGuard.BuildName(
            "HunterBoxTests",
            Environment.ProcessId,
            $"{tag}-{Guid.NewGuid():N}");

    /// <summary>
    /// 在独立线程上创建并保持一个 guard（模拟另一个进程实例），可在停止时选择是否释放所有权。
    /// </summary>
    private sealed class WorkerInstance : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly bool _releaseOnStop;
        private readonly Thread _thread;
        private SingleInstanceGuard? _guard;
        private bool _disposed;

        /// <summary>启动后台线程并等待它完成 guard 创建与所有权判定。</summary>
        /// <param name="name">互斥体名字。</param>
        /// <param name="releaseOnStop">停止线程时是否 <c>Dispose</c>（释放所有权）；<see langword="false"/> 模拟崩溃。</param>
        /// <exception cref="TimeoutException">后台线程未在超时内取得互斥体。</exception>
        public WorkerInstance(string name, bool releaseOnStop)
        {
            _releaseOnStop = releaseOnStop;
            _thread = new Thread(() =>
            {
                SingleInstanceGuard guard = new(name);
                // 字段持有引用：不释放时互斥体句柄保持打开（模拟仍有进程句柄存在的 abandoned 状态）。
                _guard = guard;
                _ready.Set();
                _stop.Wait();
                if (_releaseOnStop)
                {
                    guard.Dispose();
                }
            })
            {
                IsBackground = true, // 断言失败提前退出时也不能把测试进程卡住
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("后台实例未在超时内取得单实例互斥体");
            }
        }

        /// <summary>后台线程上创建的 guard（在线程存活期间读取）。</summary>
        public SingleInstanceGuard Guard => _guard
            ?? throw new InvalidOperationException("后台实例尚未创建 guard");

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "后台实例线程未在超时内退出");
            _ready.Dispose();
            _stop.Dispose();
        }
    }
}
