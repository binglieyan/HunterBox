using HunterBox.Domain.Mods;

namespace HunterBox.Application.Mount;

/// <summary>
/// 散装文件挂载端口：<c>ModActivationService</c> 编排启用/禁用所需的最小散装文件操作契约。
/// </summary>
/// <remarks>
/// <para>生产实现为 <see cref="MountService"/>，参数装配保持不变；启用/禁用的文件级事务仍由实现负责。</para>
/// <para>
/// 端口只暴露用例实际调用的三个方法，用于把协作者从用例中解耦：
/// 测试可注入假实现，在不触碰文件系统的前提下验证失败补偿（回滚）是否按预期调用
/// <see cref="Enable"/> / <see cref="Disable"/>。
/// </para>
/// </remarks>
public interface ILooseFileMountService
{
    /// <summary>查询某 MOD 与其它已启用 MOD 会映射到同一游戏文件的路径（启用前冲突提示）。</summary>
    /// <param name="mod">待启用的 MOD。</param>
    /// <returns>冲突的游戏相对路径（去重，忽略大小写）。</returns>
    IReadOnlyList<string> FindLooseFileConflicts(ModRecord mod);

    /// <summary>把 MOD 的散装文件写进游戏根目录。</summary>
    /// <param name="mod">要启用的 MOD。</param>
    /// <param name="files">参与散装复制的文件清单；为 <see langword="null"/> 时用完整的 <see cref="ModRecord.RelativeFiles"/>。</param>
    /// <returns>实际执行的变更统计。</returns>
    MountReport Enable(ModRecord mod, IEnumerable<string>? files = null);

    /// <summary>把 MOD 的文件从游戏根目录撤出（有备份则还原）。</summary>
    /// <param name="mod">要禁用的 MOD。</param>
    /// <param name="files">参与撤出的文件清单；为 <see langword="null"/> 时用完整的 <see cref="ModRecord.RelativeFiles"/>。</param>
    /// <returns>实际执行的变更统计。</returns>
    MountReport Disable(ModRecord mod, IEnumerable<string>? files = null);
}
