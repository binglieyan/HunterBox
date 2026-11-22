using System.Diagnostics;
using HunterBox.Application.Activation;

namespace HunterBox.Infrastructure.Games;

/// <summary>用进程名检测游戏主程序是否正在运行（「检测游戏运行」的基础设施实现）。</summary>
/// <remarks>
/// Application 层只放 <see cref="IGameProcessDetector"/> 抽象；本实现依赖进程枚举，放在 Infrastructure，
/// 由组合根装配进 <see cref="GameRunningGuard"/> 后传给启用/禁用/清理用例。
/// </remarks>
public sealed class GameProcessDetector : IGameProcessDetector
{
    /// <summary>判断是否存在同名进程。</summary>
    /// <param name="executableName">主程序文件名（可带或不带 <c>.exe</c>）。</param>
    /// <returns>有同名进程时返回 <see langword="true"/>；参数非法或查询失败时返回 <see langword="false"/>。</returns>
    public bool IsRunning(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(executableName.Trim());
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        try
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // 查询进程失败按「未运行」处理，避免误拦用户操作。
            return false;
        }
    }
}
