using HunterBox.Domain.Mods;
using HunterBox.Domain.Paks;

namespace HunterBox.Application.Paks;

/// <summary>
/// pak 挂载端口：<c>ModActivationService</c> 编排启用/禁用所需的最小 pak 操作契约。
/// </summary>
/// <remarks>
/// <para>生产实现为 <see cref="PakMountService"/>；命名计划、内容去重与写入/删除的撤销日志仍由实现负责。</para>
/// <para>
/// 端口只暴露用例实际调用的三个方法：计划先算（可能抛冲突/源缺失），通过后才落盘。
/// 测试可注入假实现，模拟 pak 写入/撤出失败并断言补偿路径是否按预期调用
/// <see cref="PlanEnable"/> / <see cref="Enable(ModRecord, PakProfile)"/> / <see cref="Disable"/>。
/// </para>
/// </remarks>
public interface IPakMountService
{
    /// <summary>计算「把该 MOD 也启用后」的 pak 放置计划，不落盘。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <param name="profile">该游戏的 pak 配置。</param>
    /// <param name="warnings">收集「其它 MOD 源文件缺失」等不致命问题的列表；可为 <see langword="null"/>。</param>
    /// <returns>放置计划；存在冲突时 <see cref="PakPlan.CanEnable"/> 为 <see langword="false"/>。</returns>
    PakPlan PlanEnable(ModRecord mod, PakProfile profile, ICollection<string>? warnings = null);

    /// <summary>把该 MOD 的 pak 写入 <c>pak_mods</c>。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <param name="profile">该游戏的 pak 配置。</param>
    /// <returns>写入统计。</returns>
    PakMountReport Enable(ModRecord mod, PakProfile profile);

    /// <summary>按已算好的计划写入，避免再次枚举全部启用 MOD 的 pak。</summary>
    /// <param name="plan">由 <see cref="PlanEnable"/> 得到且 <see cref="PakPlan.CanEnable"/> 为真的计划。</param>
    /// <returns>写入统计。</returns>
    PakMountReport Enable(PakPlan plan);

    /// <summary>把该 MOD 的 pak 从 <c>pak_mods</c> 撤出（其它 MOD 还需要则保留）。</summary>
    /// <param name="mod">待禁用的 MOD。</param>
    /// <param name="profile">该游戏的 pak 配置。</param>
    /// <returns>删除统计。</returns>
    PakMountReport Disable(ModRecord mod, PakProfile profile);
}
