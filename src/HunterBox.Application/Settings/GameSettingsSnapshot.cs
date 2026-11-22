using HunterBox.Application.Install;

namespace HunterBox.Application.Settings;

/// <summary>
/// 「游戏设置」的只读快照：INI 键名、默认值与取值范围由 <see cref="SettingsCoordinator"/> 统一处理，
/// 界面只负责控件值与快照之间的映射。
/// </summary>
/// <param name="GamePath">游戏安装目录；空串表示尚未设置。</param>
/// <param name="SavePath">存档目录；空串表示尚未设置。</param>
/// <param name="InstallOption">安装源归档方式（不复制/复制/移动）。</param>
/// <param name="FixPakNumber">是否自动修正 pak 编号。</param>
/// <param name="CheckPakFiles">启用前是否检查 pak 内部文件。</param>
/// <param name="CheckGameRunning">启用/清理前是否检查游戏正在运行。</param>
/// <param name="Theme">主题偏好的 INI 原始值（<c>system</c>/<c>light</c>/<c>dark</c>）；解析由表示层负责。</param>
public sealed record GameSettingsSnapshot(
    string GamePath,
    string SavePath,
    InstallSourceMode InstallOption,
    bool FixPakNumber,
    bool CheckPakFiles,
    bool CheckGameRunning,
    string Theme);
