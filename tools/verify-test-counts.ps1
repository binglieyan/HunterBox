#requires -Version 5.1
<#
.SYNOPSIS
    校验最近一次全量测试的 TRX 是否完整（仓库已移除 CHANGELOG，默认不比对运行计数）。

.DESCRIPTION
    Release 全量测试后由 HunterBox.Tests.csproj 的 AfterTargets="VSTest" 自动调用，也可手动运行。
    - TRX 缺失、无法解析、结果数不完整时明确失败；显式提供 -ChangelogPath 时，计数漂移也会失败；
    - 未显式指定 TRX 时选择 TrxDirectory 下最新的一份（按修改时间），不会因文件名顺序误取旧文件；
    - 筛选/截断运行按「Counters.total 与结果数不符」拒绝；
    - 仓库已不再维护 CHANGELOG：默认路径不存在时只做完整性校验并打印计数，不比对文档；
      显式传入 -ChangelogPath 但文件缺失仍视为调用方错误；
    - 成功或失败都会把 TRX 收敛到最近 N 份：默认 TestResults 与显式传入的结果目录
      都按目录清理全部 *.trx（显式目录即本门禁的输出目录）；显式 -TrxPath 只有位于
      该结果目录内才清理，避免删除共享目录中他人的文件；
    - TRX 修改时间晚于当前时间 30 秒以上视为不可信并直接失败；
      先解析校验、后清理，失败退出时由 finally 清理；
    - PruneOnly 只清理 TRX，不要求 TRX 存在，供测试开始前调用，
      保证测试失败的运行也不会无限累积。

.PARAMETER TrxPath
    可选：显式指定全量测试 TRX；相对路径按仓库根解析。未指定时从 TrxDirectory 选择最新的一份。

.PARAMETER TrxDirectory
    TRX 目录；相对路径按仓库根解析（默认 TestResults，本仓库专用）。
    相对路径的解释基准固定为脚本上一级目录，与调用方当前目录无关。

.PARAMETER ChangelogPath
    CHANGELOG 路径（可选）。仓库已不再维护变更记录：不传且默认路径不存在时跳过计数比对；
    显式传入时仍按传入文件比对，测试用它注入自造条目。

.PARAMETER NotBeforeUtc
    本次测试开始时间（UTC，ISO 8601）。提供时要求 TRX 修改时间不早于该时间，
    防止编译失败后沿用上一次同计数的旧 TRX。

.PARAMETER Configuration
    当前构建配置；非 Release 时输出「跳过计数门禁」并成功退出。

.PARAMETER TestCaseFilter
    当前测试筛选条件；非空时输出跳过原因并成功退出。

.PARAMETER ListTests
    是否为 list-tests 运行；为 true 时输出跳过原因并成功退出。

.PARAMETER Logger
    当前测试记录器列表；只要其中有 trx（可带 LogFileName 参数）就继续校验，
    全部不是 trx 时输出跳过原因并成功退出。

.PARAMETER KeepLatestTrx
    成功或失败后保留的最近 TRX 份数；0 表示不清理。

.PARAMETER PruneOnly
    只执行 TRX 清理后退出，不校验计数。

.EXAMPLE
    dotnet test HunterBox.slnx -c Release -p:Platform=x64
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify-test-counts.ps1 -KeepLatestTrx 5
#>
[CmdletBinding()]
param(
    [string]$TrxPath = "",
    [string]$TrxDirectory = "TestResults",
    [string]$ChangelogPath = "",
    [string]$NotBeforeUtc = "",
    [int]$KeepLatestTrx = 0,
    [string]$Configuration = "Release",
    [string]$TestCaseFilter = "",
    [string]$ListTests = "",
    [string]$Logger = "",
    [switch]$PruneOnly
)

$ErrorActionPreference = "Stop"

function Fail([string]$message) {
    [Console]::Error.WriteLine($message)
    exit 1
}

$root = Split-Path -Parent $PSScriptRoot

# 所有相对路径统一按仓库根解析，不受调用方当前目录影响。
function Resolve-RepositoryPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) {
        return $path
    }

    return Join-Path $root $path
}

# 按目录清理 TRX。显式传入的结果目录就是本门禁自己的输出目录，
# 不再用文件名正则筛选；比较默认目录前去掉尾分隔符，避免误判。
function Remove-OldTrx([string]$directory) {
    if ($KeepLatestTrx -le 0) {
        return
    }

    $resolved = [System.IO.Path]::GetFullPath($directory)
    $resolvedKey = $resolved.TrimEnd('\', '/')
    $defaultKey = [System.IO.Path]::GetFullPath((Join-Path $root "TestResults")).TrimEnd('\', '/')
    if (-not [string]::Equals($resolvedKey, $defaultKey, [System.StringComparison]::OrdinalIgnoreCase)) {
        # 用 Write-Host 保证默认 minimal 输出下也可见。
        Write-Host "清理结果目录中的历史 TRX（保留最近 $KeepLatestTrx 份）：$resolved"
    }

    try {
        $all = @(Get-ChildItem -LiteralPath $resolved -Filter *.trx -File |
            Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $false })
        if ($all.Count -gt $KeepLatestTrx) {
            $all | Select-Object -Skip $KeepLatestTrx | Remove-Item -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Host "清理历史 TRX 失败（不影响计数校验）：$($_.Exception.Message)"
    }
}

# 测试开始前只清理 TRX；此时还没有本次结果，也不要求 CHANGELOG 存在。
if ($PruneOnly) {
    $pruneDirectory = Resolve-RepositoryPath $TrxDirectory
    if (Test-Path -LiteralPath $pruneDirectory) {
        Remove-OldTrx $pruneDirectory
    }

    exit 0
}

# 门禁是否执行的唯一判定点；跳过原因始终可见。
if ($Configuration -ne "Release") {
    Write-Host "跳过计数门禁：当前配置为 $Configuration（仅 Release 全量运行校验）。"
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($TestCaseFilter)) {
    Write-Host "跳过计数门禁：筛选运行（$TestCaseFilter）。"
    exit 0
}

if ($ListTests -eq "true") {
    Write-Host "跳过计数门禁：list-tests 运行不产生测试结果。"
    exit 0
}

# 解析全部记录器名称；trx;LogFileName=... 与多记录器中的 trx 都视为已启用 TRX。
$loggerNames = @()
$hasTrxLogger = $false
if (-not [string]::IsNullOrWhiteSpace($Logger)) {
    $loggerNames = @($Logger -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
    foreach ($name in $loggerNames) {
        if ($name -eq "trx") {
            $hasTrxLogger = $true
            break
        }
    }
}

if ($loggerNames.Count -gt 0 -and -not $hasTrxLogger) {
    Write-Host "跳过计数门禁：自定义测试记录器（$Logger）；如需校验请附加 --logger trx。"
    exit 0
}

$changelogExplicit = -not [string]::IsNullOrWhiteSpace($ChangelogPath)
if (-not $changelogExplicit) {
    $changelogPath = Join-Path $root "CHANGELOG.md"
}
else {
    $changelogPath = Resolve-RepositoryPath $ChangelogPath
}

# 仓库已移除 CHANGELOG：默认路径不存在时只做完整性校验；显式传入却缺失仍算调用方错误。
$changelogAvailable = Test-Path -LiteralPath $changelogPath
if ($changelogExplicit -and -not $changelogAvailable) {
    Fail "找不到 CHANGELOG：$changelogPath。"
}

if ([string]::IsNullOrWhiteSpace($TrxPath)) {
    $directory = Resolve-RepositoryPath $TrxDirectory
    if (-not (Test-Path -LiteralPath $directory)) {
        Fail "找不到 TRX 目录：$directory。请先运行 Release 全量测试。"
    }

    $latest = Get-ChildItem -LiteralPath $directory -Filter *.trx -File |
        Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $false } |
        Select-Object -First 1
    if ($null -eq $latest) {
        Fail "TRX 目录中没有测试结果：$directory。请先运行 Release 全量测试。"
    }

    $trxFullPath = $latest.FullName
}
else {
    $trxFullPath = Resolve-RepositoryPath $TrxPath
    if (-not (Test-Path -LiteralPath $trxFullPath)) {
        Fail "找不到全量测试 TRX：$trxFullPath。"
    }
}

# 显式 -TrxPath 可能含 ".."；统一归一后再与结果目录比较，避免误判为目录外。
$trxFullPath = [System.IO.Path]::GetFullPath($trxFullPath)

# 只清理本门禁自己的结果目录；显式 -TrxPath 只有位于结果目录内才清理，
# 避免删除共享目录中他人的 TRX。
$pruneDirectory = ""
if ([string]::IsNullOrWhiteSpace($TrxPath)) {
    $pruneDirectory = $directory
}
else {
    $trxParent = Split-Path -Parent $trxFullPath
    $configuredDirectory = [System.IO.Path]::GetFullPath((Resolve-RepositoryPath $TrxDirectory))
    if ([string]::Equals(
        $trxParent.TrimEnd('\', '/'),
        $configuredDirectory.TrimEnd('\', '/'),
        [System.StringComparison]::OrdinalIgnoreCase)) {
        $pruneDirectory = $trxParent
    }
    else {
        Write-Host "跳过清理：显式 TRX 不在结果目录（$configuredDirectory），不删除其它文件：$trxFullPath"
    }
}

# 先解析/校验、后在 finally 中清理；即使多份 TRX 的时间戳并列，也不会在读取前删掉正在校验的文件。
# Fail 的 exit 仍会执行 finally，测试失败运行同样有界。
try {
    $trxWritten = (Get-Item -LiteralPath $trxFullPath).LastWriteTimeUtc
    $nowUtc = [DateTime]::UtcNow
    # 30 秒容差覆盖 FAT 2 秒时间戳粒度、网络盘/CI 时钟漂移
    # 与文件服务器缓存；超过 30 秒的未来时间才会让新鲜度校验恒通过，直接拒绝。
    if ($trxWritten -gt $nowUtc.AddSeconds(30)) {
        Fail "TRX 修改时间晚于当前时间（TRX $($trxWritten.ToString('o'))，当前 $($nowUtc.ToString('o'))），无法确认是本次运行的结果。"
    }

    $notBefore = [DateTime]::MinValue
    if (-not [string]::IsNullOrWhiteSpace($NotBeforeUtc)) {
        try {
            $notBefore = [DateTime]::Parse(
                $NotBeforeUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        }
        catch {
            Fail "NotBeforeUtc 不是合法的 ISO 8601 时间：$NotBeforeUtc"
        }
    }

    if ($notBefore -ne [DateTime]::MinValue) {
        if ($trxWritten -lt $notBefore.AddSeconds(-2)) {
            Fail "TRX 早于本次测试开始时间（TRX $($trxWritten.ToString('o'))，本次 $($notBefore.ToString('o'))），可能是上一次运行的旧结果；请重新运行全量测试。"
        }
    }

    try {
        [xml]$trx = Get-Content -LiteralPath $trxFullPath -Raw
    }
    catch {
        Fail "TRX 无法解析（可能被截断）：$($_.Exception.Message)"
    }

    $summaries = @($trx.SelectNodes('//*[local-name()="ResultSummary"]'))
    $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    if ($summaries.Count -ne 1 -or $results.Count -eq 0) {
        Fail "TRX 缺少 ResultSummary 或测试结果，可能来自筛选运行或被截断：$trxFullPath"
    }

    $counters = @($trx.SelectNodes('//*[local-name()="Counters"]'))
    if ($counters.Count -eq 0) {
        Fail "TRX 缺少 Counters，无法确认是否为完整运行：$trxFullPath"
    }

    $declaredTotal = [int]$counters[0].GetAttribute("total")
    if ($declaredTotal -ne $results.Count) {
        Fail "TRX 结果数 $($results.Count) 与 Counters.total $declaredTotal 不一致，文件不完整。"
    }

    $passed = 0
    $skipped = 0
    $failed = 0
    foreach ($result in $results) {
        switch ($result.GetAttribute("outcome")) {
            "Passed" { $passed++ }
            "NotExecuted" { $skipped++ }
            default { $failed++ }
        }
    }

    # 仓库不再维护 CHANGELOG：没有可比的文档时只保留完整性判据（失败数必须为 0）。
    if (-not $changelogAvailable) {
        if ($failed -ne 0) {
            Fail "本次全量运行有 $failed 个失败，不能作为发布基线。"
        }

        Write-Host "完整性校验通过：$passed 通过 / $skipped 跳过 / $failed 失败（共 $($results.Count) 个用例）；未发现 CHANGELOG，跳过计数比对。"
        exit 0
    }

    $changelog = Get-Content -LiteralPath $changelogPath -Raw
    # 只匹配最新版本段落；用例总数与文档不符时给出独立判据。
    $latestSection = [regex]::Match($changelog, '(?ms)^## v[^\n]*\n(.*?)(?=^## v|\z)')
    if (-not $latestSection.Success) {
        Fail "CHANGELOG 缺少版本条目。"
    }

    $match = [regex]::Match($latestSection.Groups[1].Value, '(\d+) 通过 / (\d+) 跳过 / (\d+) 失败')
    if (-not $match.Success) {
        Fail "CHANGELOG 最新版本条目缺少「通过 / 跳过 / 失败」统计。"
    }

    $documentedPassed = [int]$match.Groups[1].Value
    $documentedSkipped = [int]$match.Groups[2].Value
    $documentedFailed = [int]$match.Groups[3].Value
    $documentedTotal = $documentedPassed + $documentedSkipped + $documentedFailed

    # 筛选运行/截断文件的独立判据——用例总数必须与文档一致。
    if ($results.Count -ne $documentedTotal) {
        Fail "TRX 用例总数 $($results.Count) 与 CHANGELOG 记录 $documentedTotal 不一致（可能是筛选运行或文件不完整）。"
    }

    if ($failed -ne 0) {
        Fail "本次全量运行有 $failed 个失败，不能作为发布基线。"
    }

    if ($passed -ne $documentedPassed -or $skipped -ne $documentedSkipped -or $documentedFailed -ne 0) {
        Fail "测试计数漂移：实际 $passed 通过 / $skipped 跳过 / $failed 失败，CHANGELOG 记录 $documentedPassed 通过 / $documentedSkipped 跳过 / $documentedFailed 失败。"
    }

    Write-Host "测试计数与 CHANGELOG 一致：$passed 通过 / $skipped 跳过 / 0 失败（共 $($results.Count) 个用例）。"
    exit 0
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($pruneDirectory)) {
        Remove-OldTrx $pruneDirectory
    }
}
