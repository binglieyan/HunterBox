using HunterBox.Application.Models;
using HunterBox.Infrastructure.Mods;

namespace HunterBox.App;

/// <summary>
/// 把 Infrastructure 的静态名称表解析适配成 Application 的 <see cref="IModelNameResolver"/> 端口。
/// </summary>
/// <remarks>
/// 组合根职责：Application 不能引用 Infrastructure 的名称表实现，适配器放在 App 层
/// （依赖方向仍是 App → Application + Infrastructure）。名称表只读且线程安全，单例复用即可。
/// </remarks>
internal sealed class AppModelNameResolver : IModelNameResolver
{
    /// <inheritdoc />
    public string? Resolve(string path) => ModelNameResolver.Resolve(path);
}
