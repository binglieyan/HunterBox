using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace HunterBox.Tests;

/// <summary>
/// 计数脚本的门禁模式、失败路径、保留策略、多记录器、部署一致性与 MSBuild 接线；
/// 脚本场景在同一个 PowerShell 进程内执行，接线通过 <c>dotnet msbuild</c> 做行为断言。
/// </summary>
public class VerifyTestCountsScriptTests
{
    /// <summary>门禁与用例共用的 TRX 保留份数。</summary>
    private const int KeepLatestTrx = 5;

    /// <summary>传给脚本的保留份数文本（避免多处字面量）。</summary>
    private static readonly string KeepLatestTrxText = KeepLatestTrx.ToString(CultureInfo.InvariantCulture);

    /// <summary>覆盖全部门禁模式、失败路径、多记录器与保留策略；每个场景输出独立的 CASE 行供断言。</summary>
    [Fact]
    public void Script_HandlesGateModesFailurePathsAndRetentionInSingleProcess()
    {
        using TempDir temp = TempDir.Create();
        string script = DeployedScript();
        string changelog = Path.Combine(temp.Path, "CHANGELOG.md");
        // PowerShell 5 读取无 BOM 的 UTF-8 会按 ANSI 解码，中文统计行必须带 BOM。
        File.WriteAllText(changelog, Changelog(passed: 3, skipped: 1), new UTF8Encoding(true));

        string validTrx = Path.Combine(temp.Path, "valid.trx");
        File.WriteAllText(validTrx, Trx(passed: 3, skipped: 1));

        string filteredTrx = Path.Combine(temp.Path, "filtered.trx");
        File.WriteAllText(filteredTrx, Trx(passed: 2, skipped: 0));

        string truncatedTrx = Path.Combine(temp.Path, "truncated.trx");
        File.WriteAllText(truncatedTrx, "<TestRun><Results>");

        string missingDirectory = Path.Combine(temp.Path, "missing");
        string skipDirectory = Path.Combine(temp.Path, "skips");
        Directory.CreateDirectory(skipDirectory);
        string future = DateTime.UtcNow.AddHours(1).ToString("o");

        // 未来时间戳的 TRX 不可信。
        string futureTrx = Path.Combine(temp.Path, "future.trx");
        File.WriteAllText(futureTrx, Trx(passed: 3, skipped: 1));
        File.SetLastWriteTimeUtc(futureTrx, DateTime.UtcNow.AddHours(1));

        // 每个场景使用独立的临时「仓库根」，避免相对/绝对/清理场景互相影响。
        string relativeRoot = CreateFakeRepository(temp.Path, script, truncatedNewest: false, resultsCount: 7);
        string absoluteRoot = CreateFakeRepository(temp.Path, script, truncatedNewest: false, resultsCount: 7);
        string failedRoot = CreateFakeRepository(temp.Path, script, truncatedNewest: true, resultsCount: 7);
        string pruneRoot = CreateFakeRepository(temp.Path, script, truncatedNewest: false, resultsCount: 7);
        string customRoot = CreateFakeRepository(temp.Path, script, truncatedNewest: false, resultsCount: 0);
        string customResults = Path.Combine(customRoot, "custom-results");
        Directory.CreateDirectory(customResults);
        for (int index = 0; index < 7; index++)
        {
            // 使用与本机真实 VSTest 输出一致的命名（时间段用下划线分隔并带 TFM 后缀）：
            // 旧实现按 '_\d{2}-\d{2}-\d{2}' 匹配文件名，真实命名不会被它匹配，回退即失败。
            string path = Path.Combine(
                customResults,
                $"user_machine_2026-11-19_20_00_0{index}_net10.0.trx");
            File.WriteAllText(path, Trx(passed: 3, skipped: 1));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-index));
        }

        // 运行前把修改时间设为当前时间 +10/+45 秒；
        // 允许侧留 20 秒余量，拒绝侧留 15 秒余量，避免脚本启动抖动造成边界误判。
        string future10Trx = Path.Combine(temp.Path, "future10.trx");
        File.WriteAllText(future10Trx, Trx(passed: 3, skipped: 1));
        string future45Trx = Path.Combine(temp.Path, "future45.trx");
        File.WriteAllText(future45Trx, Trx(passed: 3, skipped: 1));

        // 显式 -TrxPath 位于共享目录时不得清理兄弟文件。
        string sharedDirectory = Path.Combine(temp.Path, "shared-results");
        Directory.CreateDirectory(sharedDirectory);
        string sharedTrx = Path.Combine(sharedDirectory, "shared_2026-11-19_21_00_00_net10.0.trx");
        File.WriteAllText(sharedTrx, Trx(passed: 3, skipped: 1));
        File.SetLastWriteTimeUtc(sharedTrx, DateTime.UtcNow);
        for (int index = 1; index <= 2; index++)
        {
            string sibling = Path.Combine(sharedDirectory, $"sibling-{index}.trx");
            File.WriteAllText(sibling, Trx(passed: 3, skipped: 1));
            File.SetLastWriteTimeUtc(sibling, DateTime.UtcNow.AddMinutes(-index));
        }

        // 显式 -TrxPath 位于结果目录内（且路径含 ".."）时应按目录清理。
        string inResultsDirectory = Path.Combine(temp.Path, "trxpath-results");
        Directory.CreateDirectory(inResultsDirectory);
        string newestInResults = Path.Combine(inResultsDirectory, "newest_2026-11-19_22_00_00_net10.0.trx");
        File.WriteAllText(newestInResults, Trx(passed: 3, skipped: 1));
        File.SetLastWriteTimeUtc(newestInResults, DateTime.UtcNow);
        for (int index = 1; index <= 2; index++)
        {
            string older = Path.Combine(inResultsDirectory, $"older-{index}.trx");
            File.WriteAllText(older, Trx(passed: 3, skipped: 1));
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-index));
        }

        string dottedNewest = Path.Combine(
            inResultsDirectory,
            "..",
            "trxpath-results",
            Path.GetFileName(newestInResults));

        List<ScriptScenario> scenarios =
        [
            new("valid", script, [("-TrxPath", validTrx), ("-ChangelogPath", changelog)]),
            new("missing", script, [("-TrxDirectory", missingDirectory), ("-ChangelogPath", changelog)]),
            new("stale", script, [("-TrxPath", validTrx), ("-ChangelogPath", changelog), ("-NotBeforeUtc", future)]),
            new("filtered", script, [("-TrxPath", filteredTrx), ("-ChangelogPath", changelog)]),
            new("truncated", script, [("-TrxPath", truncatedTrx), ("-ChangelogPath", changelog)]),
            new("futuretrx", script, [("-TrxPath", futureTrx), ("-ChangelogPath", changelog)]),
            // +10 秒允许，+45 秒拒绝（两侧都留足余量）。
            new(
                "future10",
                script,
                [("-TrxPath", future10Trx), ("-ChangelogPath", changelog)],
                RefreshTrxPath: future10Trx,
                RefreshFutureSeconds: 10),
            new(
                "future45",
                script,
                [("-TrxPath", future45Trx), ("-ChangelogPath", changelog)],
                RefreshTrxPath: future45Trx,
                RefreshFutureSeconds: 45),
            // 显式 TRX 在共享目录时只校验、不清理兄弟文件。
            new(
                "trxpathshared",
                script,
                [("-TrxPath", sharedTrx), ("-ChangelogPath", changelog), ("-KeepLatestTrx", "1")]),
            // 显式 TRX 在结果目录内（路径含 ".."）时按目录清理，保留最近 1 份。
            new(
                "trxpathinresults",
                script,
                [
                    ("-TrxPath", dottedNewest),
                    ("-TrxDirectory", inResultsDirectory),
                    ("-ChangelogPath", changelog),
                    ("-KeepLatestTrx", "1"),
                ]),
            new("debug", script, [("-Configuration", "Debug"), ("-TrxDirectory", skipDirectory)]),
            new("filteredskip", script, [("-TestCaseFilter", "FullyQualifiedName~X"), ("-TrxDirectory", skipDirectory)]),
            new("listtests", script, [("-ListTests", "true"), ("-TrxDirectory", skipDirectory)]),
            new("logger", script, [("-Logger", "console;verbosity=detailed"), ("-TrxDirectory", skipDirectory)]),
            new(
                "trxlogger",
                script,
                [("-TrxPath", validTrx), ("-ChangelogPath", changelog), ("-Logger", "trx;LogFileName=custom.trx")]),
            new(
                "multilogger",
                script,
                [("-TrxPath", validTrx), ("-ChangelogPath", changelog), ("-Logger", "console;verbosity=detailed;trx")]),
            new(
                "fakelogger",
                script,
                [("-TrxPath", validTrx), ("-ChangelogPath", changelog), ("-Logger", "console;LogFileName=trx.txt")]),
            new(
                "relative",
                Path.Combine(relativeRoot, "tools", "verify-test-counts.ps1"),
                [("-TrxDirectory", "TestResults"), ("-ChangelogPath", "CHANGELOG.md"), ("-KeepLatestTrx", KeepLatestTrxText)]),
            new(
                "absolute",
                Path.Combine(absoluteRoot, "tools", "verify-test-counts.ps1"),
                [
                    ("-TrxDirectory", Path.Combine(absoluteRoot, "TestResults")),
                    ("-ChangelogPath", Path.Combine(absoluteRoot, "CHANGELOG.md")),
                    ("-KeepLatestTrx", KeepLatestTrxText),
                ]),
            new(
                "custom",
                Path.Combine(customRoot, "tools", "verify-test-counts.ps1"),
                [("-TrxDirectory", "custom-results"), ("-ChangelogPath", "CHANGELOG.md"), ("-KeepLatestTrx", KeepLatestTrxText)]),
            new(
                "failed",
                Path.Combine(failedRoot, "tools", "verify-test-counts.ps1"),
                [("-TrxDirectory", "TestResults"), ("-ChangelogPath", "CHANGELOG.md"), ("-KeepLatestTrx", KeepLatestTrxText)]),
            new(
                "pruneonly",
                Path.Combine(pruneRoot, "tools", "verify-test-counts.ps1"),
                [("-PruneOnly", null), ("-TrxDirectory", "TestResults"), ("-KeepLatestTrx", KeepLatestTrxText)]),
        ];

        ScriptRun run = RunScenarios(temp.Path, scenarios);

        // 断言所有场景都输出了 CASE 行（不再断言恒真的包装器退出码）。
        Assert.Equal(scenarios.Count, run.ExitCodes.Count);
        Assert.Equal(0, run.ExitCodeOf("valid"));
        Assert.Equal(1, run.ExitCodeOf("missing"));
        Assert.Equal(1, run.ExitCodeOf("stale"));
        Assert.Equal(1, run.ExitCodeOf("filtered"));
        Assert.Equal(1, run.ExitCodeOf("truncated"));
        Assert.Equal(1, run.ExitCodeOf("futuretrx"));
        // 30 秒容差内允许，+45 秒视为未来时间拒绝。
        Assert.Equal(0, run.ExitCodeOf("future10"));
        Assert.Equal(1, run.ExitCodeOf("future45"));
        // 显式 TRX 在共享目录时只校验，不删除兄弟文件。
        Assert.Equal(0, run.ExitCodeOf("trxpathshared"));
        Assert.Equal(3, Directory.GetFiles(sharedDirectory, "*.trx").Length);
        Assert.Contains("跳过清理：显式 TRX 不在结果目录", run.StandardOutput, StringComparison.Ordinal);
        // 显式 TRX 在结果目录内（路径含 ".."）时仍按目录清理。
        Assert.Equal(0, run.ExitCodeOf("trxpathinresults"));
        Assert.Equal(1, Directory.GetFiles(inResultsDirectory, "*.trx").Length);
        Assert.True(File.Exists(newestInResults), "保留的应是本次校验的最新的 TRX");
        Assert.Equal(0, run.ExitCodeOf("debug"));
        Assert.Equal(0, run.ExitCodeOf("filteredskip"));
        Assert.Equal(0, run.ExitCodeOf("listtests"));
        Assert.Equal(0, run.ExitCodeOf("logger"));

        // trx 参数与多记录器中的 trx 仍执行校验，其它记录器跳过。
        Assert.Equal(0, run.ExitCodeOf("trxlogger"));
        Assert.Equal(0, run.ExitCodeOf("multilogger"));
        Assert.Equal(0, run.ExitCodeOf("fakelogger"));

        // 相对路径与绝对路径指向同一专用目录时结果一致。
        Assert.Equal(0, run.ExitCodeOf("relative"));
        Assert.Equal(0, run.ExitCodeOf("absolute"));

        // 显式结果目录按目录清理（不再依赖文件名正则），真实命名下 7 份收敛到 5 份。
        Assert.Equal(0, run.ExitCodeOf("custom"));
        Assert.Contains("清理结果目录中的历史 TRX", run.StandardOutput + run.StandardError, StringComparison.Ordinal);
        Assert.Equal(KeepLatestTrx, Directory.GetFiles(customResults, "*.trx").Length);

        // 验证前清理与 PruneOnly 预清理都只保留最近 N 份。
        Assert.Equal(1, run.ExitCodeOf("failed"));
        Assert.Equal(0, run.ExitCodeOf("pruneonly"));
        Assert.Equal(KeepLatestTrx, Directory.GetFiles(Path.Combine(relativeRoot, "TestResults"), "*.trx").Length);
        Assert.Equal(KeepLatestTrx, Directory.GetFiles(Path.Combine(absoluteRoot, "TestResults"), "*.trx").Length);
        Assert.Equal(KeepLatestTrx, Directory.GetFiles(Path.Combine(failedRoot, "TestResults"), "*.trx").Length);
        Assert.Equal(KeepLatestTrx, Directory.GetFiles(Path.Combine(pruneRoot, "TestResults"), "*.trx").Length);

        // 默认输出下跳过原因与失败原因都可见且有断言。
        Assert.Equal(5, CountOccurrences(run.StandardOutput, "跳过计数门禁"));
        // 通过校验并输出成功行的场景：valid、trxlogger、multilogger、relative、absolute、custom、
        // trxpathshared、trxpathinresults、future10。
        Assert.Equal(9, CountOccurrences(run.StandardOutput, "测试计数与 CHANGELOG 一致"));
        Assert.Contains("跳过计数门禁：当前配置为 Debug", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("筛选运行", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("list-tests 运行不产生测试结果", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("自定义测试记录器", run.StandardOutput, StringComparison.Ordinal);

        Assert.Contains("找不到 TRX 目录", run.StandardError, StringComparison.Ordinal);
        Assert.Contains("早于本次测试开始时间", run.StandardError, StringComparison.Ordinal);
        Assert.Contains("晚于当前时间", run.StandardError, StringComparison.Ordinal);
        Assert.Contains("用例总数", run.StandardError, StringComparison.Ordinal);
        Assert.Contains("TRX 无法解析", run.StandardError, StringComparison.Ordinal);
    }

    /// <summary>MSBuild 接线与部署项完整。</summary>
    [SourceTreeFact]
    public void GateWiring_ProjectFile_ContainsRequiredTargetsAndArguments()
    {
        string project = Path.Combine(
            SourceTreeLocator.RootOrThrow(),
            "tests",
            "HunterBox.Tests",
            "HunterBox.Tests.csproj");
        XDocument document = XDocument.Load(project);
        Assert.NotNull(document.Root);
        XNamespace ns = document.Root!.Name.Namespace;

        XElement verify = FindTarget(document, ns, "VerifyHunterBoxTestCounts");
        Assert.Equal("VSTest", (string?)verify.Attribute("AfterTargets"));
        Assert.Equal("'$(HunterBoxTestGateEnabled)' == 'true'", (string?)verify.Attribute("Condition"));
        string verifyCommand = GetExecCommand(verify, ns);
        foreach (string fragment in new[]
        {
            "$(_HunterBoxVerifyScript)",
            "-Configuration \"$(Configuration)\"",
            "-TestCaseFilter \"$(VSTestTestCaseFilter)\"",
            "-ListTests \"$(VSTestListTests)\"",
            "-Logger \"$(VSTestLogger)\"",
            "-TrxDirectory \"$(_HunterBoxResultsDirectory)\"",
            "-NotBeforeUtc \"$(_HunterBoxTestRunStartedUtc)\"",
            "-KeepLatestTrx $(HunterBoxKeepLatestTrx)",
        })
        {
            Assert.Contains(fragment, verifyCommand, StringComparison.Ordinal);
        }

        XElement record = FindTarget(document, ns, "RecordHunterBoxTestRunStart");
        Assert.Equal("VSTest", (string?)record.Attribute("BeforeTargets"));
        Assert.Contains(
            "HunterBoxTestGateEnabled",
            (string?)record.Attribute("Condition") ?? string.Empty,
            StringComparison.Ordinal);
        // Record 的当前 UTC 来源由项目文件锁定，不再依赖 msbuild 行为用例的时序。
        Assert.Contains(
            "[System.DateTime]::UtcNow.ToString(\"o\")",
            record.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("记录测试开始时间", record.ToString(), StringComparison.Ordinal);
        Assert.Contains("门禁结果目录", record.ToString(), StringComparison.Ordinal);

        // 测试开始前预清理，失败运行也不会累积。
        XElement prune = FindTarget(document, ns, "PruneHunterBoxTestResults");
        Assert.Equal("VSTest", (string?)prune.Attribute("BeforeTargets"));
        Assert.Equal("'$(HunterBoxTestGateEnabled)' == 'true'", (string?)prune.Attribute("Condition"));
        string pruneCommand = GetExecCommand(prune, ns);
        Assert.Contains("-PruneOnly", pruneCommand, StringComparison.Ordinal);
        Assert.Contains("-KeepLatestTrx $(HunterBoxKeepLatestTrx)", pruneCommand, StringComparison.Ordinal);

        // 保留份数只定义一次。
        XElement keepLatest = Assert.Single(document.Descendants(ns + "HunterBoxKeepLatestTrx"));
        Assert.Equal(KeepLatestTrxText, keepLatest.Value.Trim());

        // 默认结果目录只定义一次，避免两处字面量静默漂移。
        XElement defaultResults = Assert.Single(document.Descendants(ns + "HunterBoxDefaultResultsDirectory"));
        Assert.Contains("TestResults", defaultResults.Value, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(File.ReadAllText(project), @"..\..\TestResults"));

        // 构建后与 VSTest 前都自动比较部署副本与源码脚本的哈希。
        XElement scriptHash = FindTarget(document, ns, "VerifyHunterBoxTestGateScript");
        Assert.Contains("Build", (string?)scriptHash.Attribute("AfterTargets") ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("VSTest", (string?)scriptHash.Attribute("BeforeTargets") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, scriptHash.Elements(ns + "GetFileHash").Count());
        Assert.Contains("GetFileHash", scriptHash.ToString(), StringComparison.Ordinal);
        // 缺失源码/部署副本由 Exists 守卫阻断，不再保留恒不成立的空哈希分支。
        Assert.Contains("计数脚本源码不存在", scriptHash.ToString(), StringComparison.Ordinal);
        Assert.Contains("计数脚本部署副本不存在", scriptHash.ToString(), StringComparison.Ordinal);
        XElement gateScriptSource = Assert.Single(document.Descendants(ns + "HunterBoxGateScriptSource"));
        Assert.Contains("verify-test-counts.ps1", gateScriptSource.Value, StringComparison.Ordinal);

        // 跳过提示目标存在且内容明确。
        XElement skipped = FindTarget(document, ns, "ReportHunterBoxTestGateSkipped");
        Assert.Equal("VSTest", (string?)skipped.Attribute("AfterTargets"));
        // Debug/筛选的跳过由条件与提示断言覆盖，不再额外启动一次 msbuild 做行为断言。
        Assert.Equal("'$(HunterBoxTestGateEnabled)' != 'true'", (string?)skipped.Attribute("Condition"));
        string message = (string?)Assert.Single(skipped.Elements(ns + "Message")).Attribute("Text") ?? string.Empty;
        Assert.Contains("跳过计数门禁", message, StringComparison.Ordinal);

        // 门禁使能属性有定义，并被目标条件消费。
        List<XElement> gateProperties = [.. document.Descendants(ns + "HunterBoxTestGateEnabled")];
        Assert.True(gateProperties.Count >= 2, "HunterBoxTestGateEnabled 应包含条件定义与默认值两处");
        Assert.Contains(
            "VSTestTestCaseFilter",
            (string?)gateProperties[0].Attribute("Condition") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains(
            "VSTestListTests",
            (string?)gateProperties[0].Attribute("Condition") ?? string.Empty,
            StringComparison.Ordinal);

        // 脚本部署项与元数据存在。
        XElement copy = Assert.Single(
            document.Descendants(ns + "None")
                .Where(item => ((string?)item.Attribute("Include"))?.EndsWith(
                    "verify-test-counts.ps1",
                    StringComparison.Ordinal) == true));
        Assert.Equal("PreserveNewest", (string?)copy.Attribute("CopyToOutputDirectory"));
        Assert.Contains("verify-test-counts.ps1", (string?)copy.Attribute("Link") ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// 一次 Release 求值覆盖预清理、相对结果目录与校验脚本；合成 TRX 使用过去时间且不传 Record，
    /// 避免 msbuild 启动耗时（含冷启动 8 秒）造成新鲜度误报；Debug 跳过由条件断言和脚本跳过场景覆盖。
    /// </summary>
    [SourceTreeFact]
    public void GateWiring_MsBuildTargetsFollowGateProperty()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string project = Path.Combine(root, "tests", "HunterBox.Tests", "HunterBox.Tests.csproj");
        using TempDir temp = TempDir.Create();
        // 使用相对结果目录并把工作目录固定到临时根，实测按 MSBuild 启动目录解析。
        const string enabledDirectoryName = "enabled";
        string enabledResults = CreateVstestResults(temp.Path, enabledDirectoryName);
        string repoResults = Path.Combine(root, "TestResults");
        int repoTrxBefore = Directory.Exists(repoResults)
            ? Directory.GetFiles(repoResults, "*.trx").Length
            : 0;

        // 不把 Record 并入校验调用（固定 +3 秒会随 msbuild 冷启动漂移）；
        // Record 的 UTC 来源由项目文件断言锁定。
        const string gateTargets =
            "PruneHunterBoxTestResults;VerifyHunterBoxTestCounts;ReportHunterBoxTestGateSkipped";
        string enabled = RunMsBuildTargets(
            project,
            gateTargets,
            "Release",
            enabledDirectoryName,
            temp.Path);

        // A-03：预清理 7→5、启动脚本；仓库已移除 CHANGELOG，脚本只做完整性校验并跳过计数比对；跳过提示不输出。
        // 校验成功时 msbuild 不回显 Exec 命令行，因此只能断言脚本自身的输出。
        Assert.Contains("清理结果目录中的历史 TRX", enabled, StringComparison.Ordinal);
        Assert.Contains("完整性校验通过", enabled, StringComparison.Ordinal);
        Assert.DoesNotContain("跳过计数门禁", enabled, StringComparison.Ordinal);
        Assert.Equal(KeepLatestTrx, Directory.GetFiles(enabledResults, "*.trx").Length);

        // P-07：临时结果目录的预清理不应触碰仓库真实 TestResults。
        int repoTrxAfter = Directory.Exists(repoResults)
            ? Directory.GetFiles(repoResults, "*.trx").Length
            : 0;
        Assert.Equal(repoTrxBefore, repoTrxAfter);
    }

    /// <summary>
    /// 哈希漂移必须让构建失败；
    /// 同一次求值先跑 Record，顺带证明默认结果目录分支可达且指向仓库 TestResults。
    /// 通过属性覆盖把源码/部署副本指向临时文件，避免改动共享的测试输出。
    /// </summary>
    [SourceTreeFact]
    public void GateWiring_ScriptHashDrift_FailsBuildTarget()
    {
        string root = SourceTreeLocator.RootOrThrow();
        string project = Path.Combine(root, "tests", "HunterBox.Tests", "HunterBox.Tests.csproj");
        using TempDir temp = TempDir.Create();
        string source = Path.Combine(temp.Path, "source-verify.ps1");
        File.WriteAllText(source, "Write-Output 'source'");
        string outDir = Path.Combine(temp.Path, "out") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Path.Combine(outDir, "tools"));
        File.WriteAllText(
            Path.Combine(outDir, "tools", "verify-test-counts.ps1"),
            "Write-Output 'drift'");
        string expectedResults = Path.GetFullPath(Path.Combine(root, "TestResults"));

        string output = RunDotnetMsBuildWithExitCode(
            temp.Path,
            out int exitCode,
            project,
            "-nologo",
            "-v:m",
            "-t:RecordHunterBoxTestRunStart;VerifyHunterBoxTestGateScript",
            "-p:Configuration=Release",
            "-p:Platform=x64",
            $"-p:OutDir={outDir}",
            $"-p:HunterBoxGateScriptSource={source}");

        // 已删除不可达分支；这里继续验证默认结果目录仍解析到仓库 TestResults。
        Assert.Contains($"门禁结果目录：{expectedResults}", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("记录测试开始时间：", output, StringComparison.Ordinal);
        Assert.Contains("计数脚本部署副本与源码不一致", output, StringComparison.Ordinal);
        // 必须断言构建失败；把 Error 改成 Warning 会返回 0 并让本用例失败。
        Assert.NotEqual(0, exitCode);
    }

    /// <summary>global.json 明确最低 SDK 与上移策略，避免版本意图含糊。</summary>
    [SourceTreeFact]
    public void GateWiring_GlobalJsonDeclaresSdkIntent()
    {
        string path = Path.Combine(SourceTreeLocator.RootOrThrow(), "global.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement sdk = document.RootElement.GetProperty("sdk");

        Assert.Equal("10.0.100", sdk.GetProperty("version").GetString());
        Assert.Equal("latestMajor", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    /// <summary>输出树中的脚本副本必须与源码树完全一致，漂移即失败。</summary>
    [SourceTreeFact]
    public void GateWiring_DeployedScriptMatchesSourceTree()
    {
        string source = Path.Combine(SourceTreeLocator.RootOrThrow(), "tools", "verify-test-counts.ps1");
        string deployed = DeployedScript();

        Assert.True(File.Exists(deployed), "测试输出中缺少计数脚本副本");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(deployed));
    }

    /// <summary>按名称查找 MSBuild 目标。</summary>
    /// <param name="document">工程文档。</param>
    /// <param name="ns">XML 命名空间。</param>
    /// <param name="name">目标名称。</param>
    /// <returns>目标元素。</returns>
    private static XElement FindTarget(XDocument document, XNamespace ns, string name) =>
        Assert.Single(document.Descendants(ns + "Target").Where(target => (string?)target.Attribute("Name") == name));

    /// <summary>读取目标内唯一 Exec 的命令行。</summary>
    /// <param name="target">目标元素。</param>
    /// <param name="ns">XML 命名空间。</param>
    /// <returns>命令行文本（实体已解码）。</returns>
    private static string GetExecCommand(XElement target, XNamespace ns) =>
        (string?)Assert.Single(target.Elements(ns + "Exec")).Attribute("Command") ?? string.Empty;

    /// <summary>创建含 7 份 VSTest 默认命名 TRX 的临时结果目录（真实下划线时间格式）。</summary>
    /// <param name="parent">临时目录。</param>
    /// <param name="name">目录名。</param>
    /// <returns>结果目录。</returns>
    private static string CreateVstestResults(string parent, string name)
    {
        string directory = Path.Combine(parent, name);
        Directory.CreateDirectory(directory);
        for (int index = 0; index < 7; index++)
        {
            string path = Path.Combine(directory, $"user_machine_2026-01-01_00_00_0{index}_net10.0.trx");
            File.WriteAllText(path, Trx(passed: 3, skipped: 1));
            // 合成 TRX 使用过去时间且校验调用不传 NotBeforeUtc，避免与 msbuild 启动耗时耦合。
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-index - 10));
        }

        return directory;
    }

    /// <summary>执行指定 MSBuild 目标列表并返回合并输出。</summary>
    /// <param name="project">测试工程路径。</param>
    /// <param name="targets">目标名称列表（分号分隔）。</param>
    /// <param name="configuration">构建配置。</param>
    /// <param name="resultsDirectory">测试结果目录（隔离到临时目录）。</param>
    /// <param name="workingDirectory">MSBuild 启动目录（相对结果目录的解析基准）；为 <see langword="null"/> 时继承当前目录。</param>
    /// <returns>标准输出与标准错误合并文本。</returns>
    private static string RunMsBuildTargets(
        string project,
        string targets,
        string configuration,
        string resultsDirectory,
        string? workingDirectory = null) =>
        RunDotnetMsBuild(
            workingDirectory,
            project,
            "-nologo",
            "-v:m",
            $"-t:{targets}",
            $"-p:Configuration={configuration}",
            "-p:Platform=x64",
            $"-p:VSTestResultsDirectory={resultsDirectory}");

    /// <summary>启动 <c>dotnet msbuild</c> 并等待结束。</summary>
    /// <param name="workingDirectory">进程工作目录；为 <see langword="null"/> 时继承当前目录。</param>
    /// <param name="arguments">传给 msbuild 的参数。</param>
    /// <returns>标准输出与标准错误合并文本。</returns>
    private static string RunDotnetMsBuild(string? workingDirectory, params string[] arguments) =>
        RunDotnetMsBuildCore(workingDirectory, out _, arguments);

    /// <summary>启动 <c>dotnet msbuild</c>，返回输出与真实退出码。</summary>
    /// <param name="workingDirectory">进程工作目录；为 <see langword="null"/> 时继承当前目录。</param>
    /// <param name="exitCode">进程退出码。</param>
    /// <param name="arguments">传给 msbuild 的参数。</param>
    /// <returns>标准输出与标准错误合并文本。</returns>
    private static string RunDotnetMsBuildWithExitCode(
        string? workingDirectory,
        out int exitCode,
        params string[] arguments) =>
        RunDotnetMsBuildCore(workingDirectory, out exitCode, arguments);

    /// <summary>启动 <c>dotnet msbuild</c> 的共用实现。</summary>
    /// <param name="workingDirectory">进程工作目录；为 <see langword="null"/> 时继承当前目录。</param>
    /// <param name="exitCode">进程退出码。</param>
    /// <param name="arguments">传给 msbuild 的参数。</param>
    /// <returns>标准输出与标准错误合并文本。</returns>
    private static string RunDotnetMsBuildCore(
        string? workingDirectory,
        out int exitCode,
        string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // dotnet CLI 重定向输出为 UTF-8；默认按系统 ANSI 解码会把中文变成乱码。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        startInfo.ArgumentList.Add("msbuild");
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(120_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.True(exited, "dotnet msbuild 超时");
        Task.WaitAll(output, error);
        exitCode = process.ExitCode;
        return output.Result + error.Result;
    }

    /// <summary>定位随测试输出部署的计数脚本。</summary>
    /// <returns>脚本绝对路径。</returns>
    private static string DeployedScript() =>
        Path.Combine(AppContext.BaseDirectory, "tools", "verify-test-counts.ps1");

    /// <summary>创建带 <c>tools/</c>、<c>CHANGELOG.md</c> 与专用 <c>TestResults</c> 的临时仓库根。</summary>
    /// <param name="parent">临时目录。</param>
    /// <param name="script">计数脚本源文件。</param>
    /// <param name="truncatedNewest">最新一份 TRX 是否故意截断。</param>
    /// <param name="resultsCount">TRX 份数。</param>
    /// <returns>临时仓库根目录。</returns>
    private static string CreateFakeRepository(string parent, string script, bool truncatedNewest, int resultsCount)
    {
        string root = Path.Combine(parent, "fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        File.Copy(script, Path.Combine(root, "tools", "verify-test-counts.ps1"));
        File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), Changelog(passed: 3, skipped: 1), new UTF8Encoding(true));

        string results = Path.Combine(root, "TestResults");
        Directory.CreateDirectory(results);
        for (int index = 0; index < resultsCount; index++)
        {
            string path = Path.Combine(results, $"result-{index}.trx");
            File.WriteAllText(path, truncatedNewest && index == 0 ? "<TestRun><Results>" : Trx(passed: 3, skipped: 1));
            // 序号越小越新：index 0 是本次运行，必须被选中。
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-index));
        }

        return root;
    }

    /// <summary>生成一个按顺序执行全部场景的包装脚本，只启动一次 PowerShell。</summary>
    /// <param name="directory">临时目录。</param>
    /// <param name="scenarios">场景列表。</param>
    /// <returns>各场景退出码与合并后的输出。</returns>
    private static ScriptRun RunScenarios(string directory, IReadOnlyList<ScriptScenario> scenarios)
    {
        string statusPath = Path.Combine(directory, "current-scenario.txt");
        StringBuilder wrapper = new();
        wrapper.AppendLine("$ErrorActionPreference = 'Continue'");
        foreach (ScriptScenario scenario in scenarios)
        {
            // 先写状态文件，超时时可定位到具体场景。
            wrapper.Append("Set-Content -LiteralPath '").Append(Escape(statusPath))
                .Append("' -Value '").Append(scenario.Name).AppendLine("' -Encoding ASCII");
            if (scenario.RefreshTrxPath is not null)
            {
                // 在同一个 PowerShell 进程内紧邻执行前设置未来时间，避免跨进程时钟偏差。
                wrapper.Append("(Get-Item -LiteralPath '").Append(Escape(scenario.RefreshTrxPath))
                    .Append("').LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(")
                    .Append(scenario.RefreshFutureSeconds.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(")");
            }

            // 每个场景前重置退出码，避免上一条命令的残留值污染结果。
            wrapper.AppendLine("$LASTEXITCODE = 0");
            wrapper.Append("& '").Append(Escape(scenario.Script)).Append('\'');
            foreach ((string name, string? value) in scenario.Arguments)
            {
                wrapper.Append(' ').Append(name);
                if (value is not null)
                {
                    wrapper.Append(" '").Append(Escape(value)).Append('\'');
                }
            }

            wrapper.AppendLine();
            wrapper.Append("Write-Output ('CASE:").Append(scenario.Name).Append("=' + $LASTEXITCODE)").AppendLine();
        }

        wrapper.AppendLine("exit 0");
        string wrapperPath = Path.Combine(directory, "run-scenarios.ps1");
        File.WriteAllText(wrapperPath, wrapper.ToString(), new UTF8Encoding(true));

        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(wrapperPath);

        using Process process = Process.Start(startInfo)!;
        // 先异步读取输出，再等待退出；否则超时断言永远不可达。
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(120_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        string lastScenario = File.Exists(statusPath) ? File.ReadAllText(statusPath).Trim() : "未知";
        Assert.True(exited, $"计数校验脚本超时；最后开始的场景：{lastScenario}");
        Task.WaitAll(standardOutput, standardError);

        Dictionary<string, int> exitCodes = [];
        foreach (string line in standardOutput.Result.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("CASE:", StringComparison.Ordinal))
            {
                continue;
            }

            int separator = trimmed.LastIndexOf('=');
            Assert.True(separator > "CASE:".Length, $"无法解析场景输出：{trimmed}");
            exitCodes[trimmed["CASE:".Length..separator]] = int.Parse(
                trimmed[(separator + 1)..],
                CultureInfo.InvariantCulture);
        }

        return new ScriptRun(standardOutput.Result, standardError.Result, exitCodes);
    }

    /// <summary>PowerShell 单引号字符串转义：单引号写成两个。</summary>
    /// <param name="value">原始文本。</param>
    /// <returns>可放入单引号字符串的文本。</returns>
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>统计子串出现次数。</summary>
    /// <param name="text">文本。</param>
    /// <param name="value">子串。</param>
    /// <returns>出现次数。</returns>
    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    /// <summary>构造带中文统计行的临时 CHANGELOG。</summary>
    /// <param name="passed">通过数。</param>
    /// <param name="skipped">跳过数。</param>
    /// <returns>CHANGELOG 内容。</returns>
    private static string Changelog(int passed, int skipped) =>
        $"## v0.0.0 test\r\n\r\n{passed} 通过 / {skipped} 跳过 / 0 失败\r\n";

    /// <summary>构造最小可解析的 TRX 文件内容。</summary>
    /// <param name="passed">通过数。</param>
    /// <param name="skipped">跳过数。</param>
    /// <returns>TRX 内容。</returns>
    private static string Trx(int passed, int skipped)
    {
        StringBuilder results = new();
        int index = 0;
        for (int count = 0; count < passed; count++)
        {
            results.AppendLine($"    <UnitTestResult testName=\"p{index++}\" outcome=\"Passed\" />");
        }

        for (int count = 0; count < skipped; count++)
        {
            results.AppendLine($"    <UnitTestResult testName=\"s{index++}\" outcome=\"NotExecuted\" />");
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
            {results}  </Results>
              <ResultSummary outcome="Completed">
                <Counters total="{passed + skipped}" passed="{passed}" failed="0" />
              </ResultSummary>
            </TestRun>
            """;
    }

    /// <summary>单个脚本场景：参数按 <c>-名称 值</c> 成对存放，开关参数的值部分为 <see langword="null"/>。</summary>
    /// <param name="Name">场景名（CASE 行使用）。</param>
    /// <param name="Script">要执行的脚本路径。</param>
    /// <param name="Arguments">参数名与值（开关参数值为 <see langword="null"/>）。</param>
    /// <param name="RefreshTrxPath">运行前需要刷新修改时间的 TRX；为 <see langword="null"/> 时不刷新。</param>
    /// <param name="RefreshFutureSeconds">刷新为「当前 UTC + 该秒数」。</param>
    private readonly record struct ScriptScenario(
        string Name,
        string Script,
        IReadOnlyList<(string Name, string? Value)> Arguments,
        string? RefreshTrxPath = null,
        int RefreshFutureSeconds = 0);

    /// <summary>包装脚本一次运行的输出与各场景退出码。</summary>
    /// <param name="StandardOutput">标准输出（含各场景 Write-Host 内容）。</param>
    /// <param name="StandardError">标准错误（含各场景 Fail 内容）。</param>
    /// <param name="ExitCodes">场景名到退出码的映射。</param>
    private readonly record struct ScriptRun(
        string StandardOutput,
        string StandardError,
        IReadOnlyDictionary<string, int> ExitCodes)
    {
        /// <summary>读取场景退出码，缺失时让测试失败。</summary>
        /// <param name="name">场景名。</param>
        /// <returns>场景退出码。</returns>
        public int ExitCodeOf(string name)
        {
            Assert.True(ExitCodes.TryGetValue(name, out int code), $"场景 {name} 没有输出 CASE 行");
            return code;
        }
    }
}
