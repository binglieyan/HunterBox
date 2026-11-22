using HunterBox.Application.Install;
using HunterBox.Domain.Layout;
using HunterBox.Domain.Mods;

namespace HunterBox.App;

/// <summary>
/// 安装暂存阶段需要的服务集合；统一由组合根 <see cref="AppServices.CreateInstallContext"/> 创建，
/// 避免每个处理函数各 new 一遍。
/// </summary>
internal sealed record ModInstallContext(BoxPaths Paths, IModStore Store, ModInstaller Installer, string GameRoot);
