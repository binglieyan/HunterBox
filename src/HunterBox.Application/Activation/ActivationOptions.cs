using HunterBox.Application.Common;
using HunterBox.Application.Mount;
using HunterBox.Application.Paks;

namespace HunterBox.Application.Activation;

/// <summary>
/// 启用/禁用用例的可选装配项（A-08）：把守卫、设置开关与端口工厂收敛为一个对象。
/// </summary>
/// <remarks>
/// <para>
/// 收敛前这些协作者平铺在 <see cref="ModActivationService"/> 的构造参数上，调用方容易只传
/// 「检查 PAK 内部文件」开关却漏掉配套的读取器。现在开关与配套依赖放在同一对象里，
/// 装配点（组合根）能直观看到它们的关系。
/// </para>
/// <para>
/// 文件系统端口是必要依赖，因此标为 <see langword="required"/>；其余项留空时使用默认实现或关闭对应能力。
/// 例外：<see cref="CheckPakInternalFiles"/> 为 <see langword="true"/> 时必须同时提供
/// <see cref="PakReader"/>，否则 <see cref="ModActivationService"/> 构造函数抛 <see cref="ArgumentException"/>。
/// </para>
/// </remarks>
public sealed class ActivationOptions
{
    /// <summary>文件系统端口（必需）：挂载失败补偿与清理的唯一入口。</summary>
    public required IFileSystem FileSystem { get; init; }

    /// <summary>「检测游戏运行」守卫；为 <see langword="null"/> 表示不检测（设置关闭或未接线）。</summary>
    public GameRunningGuard? GameRunningGuard { get; init; }

    /// <summary>是否在启用前展开 pak 内部文件做重叠提示（设置里的「检查PAK内部文件」）。</summary>
    public bool CheckPakInternalFiles { get; init; }

    /// <summary>
    /// 进程级 pak 指纹缓存；为 <see langword="null"/> 时每个激活服务实例自建一个。
    /// 共享实例可跨操作复用「长度 + 最后写入时间」校验过的整文件哈希。
    /// </summary>
    public PakFingerprintCache? PakFingerprintCache { get; init; }

    /// <summary>
    /// pak 头部/条目表读取器；<see cref="CheckPakInternalFiles"/> 为 <see langword="true"/> 时必须提供，
    /// 否则 <see cref="ModActivationService"/> 构造函数抛 <see cref="ArgumentException"/>。
    /// </summary>
    public IPakArchiveReader? PakReader { get; init; }

    /// <summary>
    /// 散装挂载端口工厂；为 <see langword="null"/> 时使用默认实现：首次使用时创建
    /// <see cref="MountService"/>，同一激活服务实例内复用（参数与既有装配完全一致）。
    /// 注入后便于替换失败补偿路径的协作者。
    /// </summary>
    public Func<ILooseFileMountService>? MountServiceFactory { get; init; }

    /// <summary>
    /// pak 挂载端口工厂；为 <see langword="null"/> 时使用默认实现：首次使用时创建
    /// <see cref="PakMountService"/>，同一激活服务实例内复用并共享指纹缓存（P-01）。
    /// </summary>
    public Func<IPakMountService>? PakServiceFactory { get; init; }
}
