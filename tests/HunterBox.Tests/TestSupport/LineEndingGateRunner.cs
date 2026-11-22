using System.Diagnostics;

namespace HunterBox.Tests;

/// <summary>
/// 行尾门禁用例的进程运行与夹具复制：接线用例与脚本用例共用，避免两份进程启动代码漂移。
/// </summary>
internal static class LineEndingGateRunner
{
    /// <summary>把真实门禁目标与脚本复制到临时仓库，确保执行的是仓库当前配置。</summary>
    /// <param name="destination">临时目录。</param>
    internal static void CopyGateFixture(string destination)
    {
        string root = SourceTreeLocator.RootOrThrow();
        File.Copy(
            Path.Combine(root, "Directory.Build.targets"),
            Path.Combine(destination, "Directory.Build.targets"));
        string tools = Path.Combine(destination, "tools");
        _ = Directory.CreateDirectory(tools);
        foreach (string name in new[]
        {
            "verify-line-endings.ps1", "text-file-extensions.txt", "gate-pruned-directories.txt",
        })
        {
            File.Copy(Path.Combine(root, "tools", name), Path.Combine(tools, name));
        }
    }

    /// <summary>执行临时仓库里的 `VerifyTextFileLineEndings` 目标并返回进程结果。</summary>
    /// <param name="projectDirectory">包含 `HunterBox.Domain.csproj` 的临时目录。</param>
    /// <returns>退出码、标准输出与标准错误。</returns>
    internal static (int ExitCode, string Output, string Error) RunMsBuildTarget(string projectDirectory)
    {
        ProcessStartInfo info = new("dotnet")
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("msbuild");
        info.ArgumentList.Add(Path.Combine(projectDirectory, "HunterBox.Domain.csproj"));
        info.ArgumentList.Add("-t:VerifyTextFileLineEndings");
        info.ArgumentList.Add("-nologo");
        info.ArgumentList.Add("-v:m");
        return RunProcess(info, 60_000);
    }

    /// <summary>用仓库脚本运行门禁，并通过 `-Extensions` 覆盖扩展名。</summary>
    /// <param name="root">要扫描的根目录。</param>
    /// <param name="extension">扩展名（含点）。</param>
    /// <param name="fix">是否传入 `-Fix` 执行一键修复。</param>
    /// <returns>退出码、标准输出与标准错误。</returns>
    internal static (int ExitCode, string Output, string Error) RunScript(
        string root,
        string extension,
        bool fix = false) =>
        RunScriptAt(
            Path.Combine(SourceTreeLocator.RootOrThrow(), "tools", "verify-line-endings.ps1"),
            root,
            extension,
            fix);

    /// <summary>用指定脚本路径运行门禁，供复制脚本后验证自定义配置。</summary>
    /// <param name="script">脚本文本路径。</param>
    /// <param name="root">要扫描的根目录。</param>
    /// <param name="extension">扩展名（含点）。</param>
    /// <param name="fix">是否传入 `-Fix` 执行一键修复。</param>
    /// <returns>退出码、标准输出与标准错误。</returns>
    internal static (int ExitCode, string Output, string Error) RunScriptAt(
        string script,
        string root,
        string extension,
        bool fix = false)
    {
        ProcessStartInfo info = new("powershell")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("-Root");
        info.ArgumentList.Add(root);
        info.ArgumentList.Add("-Extensions");
        info.ArgumentList.Add(extension);
        if (fix)
        {
            info.ArgumentList.Add("-Fix");
        }

        return RunProcess(info, 30_000);
    }

    /// <summary>断言文件开头是 UTF-8 BOM 且全文为纯 CRLF。</summary>
    /// <param name="path">文件路径。</param>
    internal static void AssertBomAndCrlf(string path)
    {
        byte[] head = new byte[3];
        using (FileStream stream = File.OpenRead(path))
        {
            Assert.Equal(3, stream.Read(head, 0, head.Length));
        }

        byte[] expected = [0xEF, 0xBB, 0xBF];
        Assert.Equal(expected, head);
        Assert.False(SourceLineEndingChecks.Inspect(path).Mixed);
    }

    /// <summary>启动进程并异步读取两个输出流，避免串行读造成的缓冲区互锁。</summary>
    /// <param name="info">进程启动参数。</param>
    /// <param name="timeoutMilliseconds">等待超时。</param>
    /// <returns>退出码、标准输出与标准错误。</returns>
    /// <exception cref="TimeoutException">进程超时未退出。</exception>
    private static (int ExitCode, string Output, string Error) RunProcess(
        ProcessStartInfo info,
        int timeoutMilliseconds)
    {
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"门禁进程 {timeoutMilliseconds} 毫秒内未退出");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}

/// <summary>需要源码树且当前环境能创建目录链接时运行；任一条件不满足在发现阶段跳过。</summary>
internal sealed class SourceTreeDirectoryLinkFactAttribute : FactAttribute
{
    /// <summary>按源码树与目录链接能力设置跳过原因。</summary>
    public SourceTreeDirectoryLinkFactAttribute()
    {
        if (SourceTreeLocator.TryFind() is null)
        {
            Skip = "找不到 HunterBox.slnx，跳过依赖源码树布局的用例。";
        }
        else if (!LinkTestSupport.CanCreateDirectoryLink)
        {
            Skip = "当前环境无法创建目录链接（符号链接与 junction 均不可用），跳过链接回环用例。";
        }
    }
}
