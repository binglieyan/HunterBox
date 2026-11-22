# 构建期/提交前行尾校验。
# 声明范围与测试门禁共用 tools/text-file-extensions.txt；剪枝目录与测试共用
# tools/gate-pruned-directories.txt；枚举时跳过真正的链接（符号链接/junction/WSL 链接）；
# 目录重解析点在 LinkType 不可用时回退 fsutil 读取标签并只放行已知非链接标签
# （云占位/WOF/WCI/AppExecLink/StorageSync），
# 未知标签按 fail-closed 跳过并告警；文件重解析点不查标签（避免云占位逐文件进程与下载），无法确认即跳过并告警；
# 默认模式下任何非 CRLF 文件都会让调用方失败；扫描 0 个文件同样失败，避免范围配置错误被静默放过。
# -Fix：一键修复模式。只尝试修复行尾违规文件：
# 只调整行尾并补齐末尾换行（不修改其它正文），重写时按 UTF-8 补 BOM；行尾违规且无法严格 UTF-8 解码
# （GBK/ANSI、含 NUL 的无 BOM UTF-16/32）、带 UTF-16/32 BOM、不可读或写入失败时保持原样并退出 1；
# 行尾合规但缺 BOM 的文件不处理。构建期始终不带该参数。
[CmdletBinding()]
param(
    [string]$Root = '',
    # 测试/调试可显式覆盖扩展名；生产构建始终读取共享清单。
    [string[]]$Extensions = @(),
    # 一键修复行尾违规；只调整行尾并补齐末尾换行，
    # 重写时补 BOM，不修改其它正文；编码不支持、不可读或写入失败时保持原样并退出 1。
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'

# 读取重解析标签，返回小写十六进制字符串。
# 必须用字符串比较：PS 5.1 会把 0xA0000003 解析成 Int32 负数，与无符号标签比较恒假。
# 读取失败返回 $null，由调用方 fail-closed 处理。
function Get-ReparseTag([string]$Path) {
    try {
        $output = & fsutil.exe reparsepoint query $Path 2>$null | Out-String
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        $match = [regex]::Match($output, '0x([0-9a-fA-F]{8})')
        if (-not $match.Success) {
            return $null
        }

        return $match.Groups[1].Value.ToLowerInvariant()
    }
    catch {
        return $null
    }
}

# 链接标签（全部为名称代理标签，必须跳过）：来源 Windows SDK winnt.h。
#   a0000003 = IO_REPARSE_TAG_MOUNT_POINT（junction）
#   a000000c = IO_REPARSE_TAG_SYMLINK
#   a0000019 = IO_REPARSE_TAG_GLOBAL_REPARSE（全局命名空间名称代理，重定向类，按链接处理）
#   a000001d = IO_REPARSE_TAG_LX_SYMLINK（WSL 符号链接）
$script:LinkTags = @('a0000003', 'a000000c', 'a0000019', 'a000001d')
# 已知非链接重解析点（放行继续校验）：
#   ^9000[0-9a-f]01a$ = IO_REPARSE_TAG_CLOUD 家族（OneDrive 等云占位，覆盖 CLOUD..CLOUD_F）
#   80000017 = IO_REPARSE_TAG_WOF（Windows Overlay Filter，压缩）
#   80000018 = IO_REPARSE_TAG_WCI（Windows Container Image，压缩/去重）
#   8000001b = IO_REPARSE_TAG_APPEXECLINK（UWP 执行别名）
#   8000001e = IO_REPARSE_TAG_STORAGE_SYNC（存储同步）
# 白名单之外一律视为不可确认并按 fail-closed 跳过并告警；
# 未核实的云/存储类新标签（例如 0x90000027）不会被正则误放行，需人工核实后再加入。
$script:PlainTagPatterns = @(
    '^9000[0-9a-f]01a$', '^80000017$', '^80000018$', '^8000001b$', '^8000001e$')

# 判断重解析点是否为真正的链接。
# 返回 $true 表示确认是链接，$false 表示确认不是链接，$null 表示无法确认。
# 先看 Get-Item 的 LinkType/Target：PS 5.1 下常为空（仅作快速确认），不可用即按 fail-closed 处理；
# 仅在 -AllowTagLookup（目录）时回退 fsutil 读取标签，避免对云占位文件逐文件启动进程并触发下载。
# 链接判定与 C# 侧 SafeFileTree/IFileSystem.IsReparseLink 的语义同步点见 docs/wiki/09-build-test-run.md。
function Test-IsLink([string]$Path, [switch]$AllowTagLookup) {
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
    catch {
        return $null
    }

    $linkType = $item.PSObject.Properties['LinkType']
    if ($null -ne $linkType -and -not [string]::IsNullOrEmpty([string]$linkType.Value)) {
        return $true
    }

    $target = $item.PSObject.Properties['Target']
    if ($null -ne $target -and -not [string]::IsNullOrEmpty([string]$target.Value)) {
        return $true
    }

    if (-not $AllowTagLookup) {
        return $null
    }

    $tag = Get-ReparseTag $Path
    if ($null -eq $tag) {
        return $null
    }

    if ($script:LinkTags -contains $tag) {
        return $true
    }

    foreach ($pattern in $script:PlainTagPatterns) {
        if ($tag -match $pattern) {
            return $false
        }
    }

    return $null
}

$root = $Root
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $PSScriptRoot
}
$root = [System.IO.Path]::GetFullPath($root)

$extensionList = @($Extensions | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($extensionList.Count -eq 0) {
    $listPath = Join-Path $PSScriptRoot 'text-file-extensions.txt'
    if (-not (Test-Path -LiteralPath $listPath)) {
        [Console]::Error.WriteLine("找不到行尾门禁的扩展名清单：$listPath")
        exit 1
    }

    # 用 .NET API 读取，避免 Windows PowerShell 5 对无 BOM/LF 文件的编码与分行差异。
    $extensionList = @([System.IO.File]::ReadAllLines($listPath) |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
}

if ($extensionList.Count -eq 0) {
    [Console]::Error.WriteLine('行尾门禁的扩展名清单为空，不能静默通过。')
    exit 1
}

# 注意：参数 $Extensions 带 [string[]] 类型约束，不能复用作 HashSet（会被强制转成定长数组）。
$extensionSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($extension in $extensionList) {
    $value = $extension
    if (-not $value.StartsWith('.')) {
        $value = '.' + $value
    }

    [void]$extensionSet.Add($value)
}

# 剪枝边界与测试共用一份清单，脚本不再内联目录名。
$prunePath = Join-Path $PSScriptRoot 'gate-pruned-directories.txt'
if (-not (Test-Path -LiteralPath $prunePath)) {
    [Console]::Error.WriteLine("找不到行尾门禁的剪枝目录清单：$prunePath")
    exit 1
}

$pruneList = @([System.IO.File]::ReadAllLines($prunePath) |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') })
if ($pruneList.Count -eq 0) {
    [Console]::Error.WriteLine('行尾门禁的剪枝目录清单为空，不能静默通过。')
    exit 1
}

$pruned = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $pruneList) {
    [void]$pruned.Add($name)
}

# 把行尾违规文件重写为 CRLF，
# 并按 UTF-8 补 BOM 与末尾换行；只调整行尾，不修改其它正文。失败返回 $false。
# 只处理行尾违规（裸 LF/裸 CR）：行尾合规但缺 BOM 的文件不在此处理，交给 EncodingConsistencyTests。
# 解码使用严格 UTF-8；GBK/ANSI、UTF-16/UTF-32 BOM 以及含 NUL 的字节（疑似无 BOM 的 UTF-16/UTF-32）
# 一律拒绝，绝不静默改写。写入先落到同目录临时文件，再用 File.Replace 原子替换。
function Repair-File([string]$Path) {
    $script:repairError = ''
    $script:repairErrorKind = ''
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        $length = $bytes.Length

        if ($length -ge 4 -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00) -or
            ($bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF))) {
            $script:repairErrorKind = 'Encoding'
            throw '检测到 UTF-32 BOM，声明范围只允许 UTF-8，拒绝重写'
        }

        if ($length -ge 2 -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
            ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))) {
            $script:repairErrorKind = 'Encoding'
            throw '检测到 UTF-16 BOM，声明范围只允许 UTF-8，拒绝重写'
        }

        # 声明范围内的文本文件不可能合法包含 NUL；无 BOM 的 UTF-16/UTF-32 会把 ASCII 编成含 0x00 的字节，
        # 严格 UTF-8 解码不会报错，必须在解码前 fail-closed。
        if ([Array]::IndexOf($bytes, [byte]0) -ge 0) {
            $script:repairErrorKind = 'Encoding'
            throw '包含 NUL 字节（疑似无 BOM 的 UTF-16/UTF-32），声明范围只允许 UTF-8，拒绝重写'
        }

        # 连续剥离所有 UTF-8 BOM，避免第二个 BOM 被解码成 U+FEFF 写入正文。
        $offset = 0
        while ($length - $offset -ge 3 -and
            $bytes[$offset] -eq 0xEF -and $bytes[$offset + 1] -eq 0xBB -and $bytes[$offset + 2] -eq 0xBF) {
            $offset += 3
        }

        $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
        $text = $strictUtf8.GetString($bytes, $offset, $length - $offset)

        # 三处替换都是字面量，用 String.Replace 避免正则匹配的额外耗时与内存。
        # 这里只调整行尾并补齐末尾换行，不修改其它正文。
        $text = $text.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
        if (-not $text.EndsWith("`r`n")) {
            $text += "`r`n"
        }

        # 先写同目录临时文件再原子替换；后缀尽量短以缓解 MAX_PATH。
        # 用 finally 覆盖写入与替换：任何失败路径都删除临时文件，避免残留半截 .hbtmp。
        $tempPath = $Path + '.hbtmp'
        try {
            [System.IO.File]::WriteAllText($tempPath, $text, (New-Object System.Text.UTF8Encoding($true)))
            # PS 5.1 会把 $null 绑成空字符串（非法路径），必须用 [NullString]::Value 传真正的 null 备份参数。
            [System.IO.File]::Replace($tempPath, $Path, [NullString]::Value)
        }
        finally {
            try {
                if ([System.IO.File]::Exists($tempPath)) {
                    [System.IO.File]::Delete($tempPath)
                }
            }
            catch {
                # 清理失败不掩盖原始错误；遗留的 .hbtmp 不在声明范围，不会进入门禁。
            }
        }

        return $true
    }
    catch {
        # 不吞异常文本：调用方把原因写进错误输出，便于定位 GBK/UTF-16 等非 UTF-8 文件。
        # PowerShell 会把 .NET 方法异常包成 MethodInvocationException，必须取基异常判定类型与消息。
        $baseError = $_.Exception
        while ($null -ne $baseError.InnerException) {
            $baseError = $baseError.InnerException
        }

        $script:repairError = $baseError.Message
        if ([string]::IsNullOrEmpty($script:repairErrorKind)) {
            if ($baseError -is [System.Text.DecoderFallbackException]) {
                $script:repairErrorKind = 'Encoding'
            }
            else {
                $script:repairErrorKind = 'Io'
            }
        }

        return $false
    }
}

$loneLineEnding = [regex]::new('(?<!\r)\n|\r(?!\n)', [System.Text.RegularExpressions.RegexOptions]::Compiled)
# 按 64KB 分块读取，内存与文件大小无关；Latin1 保留字节到字符的一一映射，
# 分块边界只可能切断 CRLF，用 $carry 把结尾的 CR 留到下一块判断。
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$buffer = New-Object byte[] 65536
$offenders = [System.Collections.Generic.List[string]]::new()
$uncertain = [System.Collections.Generic.List[string]]::new()
$unreadable = [System.Collections.Generic.List[string]]::new()
# 同一路径可能被文件/目录枚举各失败一次，按路径去重后只记录一条。
$reportedPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$encodingFailures = [System.Collections.Generic.List[string]]::new()
$repairFailures = [System.Collections.Generic.List[string]]::new()
$fixedFiles = [System.Collections.Generic.List[string]]::new()
$checked = 0
$pending = [System.Collections.Generic.Stack[string]]::new()
$pending.Push($root)
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    # 目录枚举是惰性的，ACL/删除等异常不能让整轮扫描终止。
    try {
        $files = @([System.IO.Directory]::EnumerateFiles($directory))
    }
    catch {
        if ($reportedPaths.Add($directory)) {
            [void]$unreadable.Add(
                $directory.Substring($root.Length).TrimStart('\', '/') + '（' + $_.Exception.Message + '）')
        }

        $files = @()
    }

    foreach ($file in $files) {
        # 文件链接直接跳过；文件重解析点不查标签，
        # 避免对 OneDrive 占位逐文件启动 fsutil 并触发整文件下载；无法确认时 skip + 告警。
        # 取舍：WOF/WCI 压缩的声明文件也会落在这里被跳过（有告警），不在门禁中解压校验（见 wiki09）。
        try {
            $fileAttributes = [System.IO.File]::GetAttributes($file)
        }
        catch {
            # 单个文件不可读（占用/权限）时记录后继续，不能中断整轮扫描。
            [void]$unreadable.Add(
                $file.Substring($root.Length).TrimStart('\', '/') + '（' + $_.Exception.Message + '）')
            continue
        }

        if (($fileAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $isLink = Test-IsLink $file
            if ($null -eq $isLink) {
                [void]$uncertain.Add($file)
                continue
            }

            if ($isLink) {
                continue
            }
        }

        if (-not $extensionSet.Contains([System.IO.Path]::GetExtension($file))) {
            continue
        }

        $checked++
        $bad = $false
        $carry = ''
        try {
            $stream = [System.IO.File]::OpenRead($file)
        }
        catch {
            if ($reportedPaths.Add($file)) {
                [void]$unreadable.Add(
                    $file.Substring($root.Length).TrimStart('\', '/') + '（' + $_.Exception.Message + '）')
            }

            continue
        }

        # 网络盘断开等读取期 IOException 也必须记录后继续，不能中止整轮扫描。
        $readFailed = $false
        try {
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $text = $carry + $latin1.GetString($buffer, 0, $read)
                $carry = ''
                if ($text.EndsWith("`r")) {
                    $carry = "`r"
                    $text = $text.Substring(0, $text.Length - 1)
                }

                if ($loneLineEnding.IsMatch($text)) {
                    $bad = $true
                    break
                }
            }
        }
        catch {
            if ($reportedPaths.Add($file)) {
                [void]$unreadable.Add(
                    $file.Substring($root.Length).TrimStart('\', '/') + '（' + $_.Exception.Message + '）')
            }

            $readFailed = $true
        }
        finally {
            $stream.Dispose()
        }

        if ($readFailed) {
            continue
        }

        if (-not $bad -and $carry -eq "`r") {
            $bad = $true
        }

        if ($bad) {
            if ($Fix) {
                if (Repair-File $file) {
                    [void]$fixedFiles.Add($file)
                    continue
                }

                [Console]::Error.WriteLine("修复失败（保持原样）：$file（$script:repairError）")
                $relative = $file.Substring($root.Length).TrimStart('\', '/')
                if ($script:repairErrorKind -eq 'Encoding') {
                    [void]$encodingFailures.Add($relative + '（' + $script:repairError + '）')
                }
                else {
                    [void]$repairFailures.Add($relative + '（' + $script:repairError + '）')
                }

                continue
            }

            $offenders.Add($file)
        }
    }

    # 目录枚举异常同样记录后继续，已完成的目录结果照常输出。
    try {
        $children = @([System.IO.Directory]::EnumerateDirectories($directory))
    }
    catch {
        if ($reportedPaths.Add($directory)) {
            [void]$unreadable.Add(
                $directory.Substring($root.Length).TrimStart('\', '/') + '（' + $_.Exception.Message + '）')
        }

        $children = @()
    }

    foreach ($child in $children) {
        # 目录数量有限，允许 fsutil 回退区分链接与云占位；
        # 未知标签或读取失败按 fail-closed 跳过并告警。
        try {
            $childAttributes = [System.IO.File]::GetAttributes($child)
        }
        catch {
            [void]$unreadable.Add(
                $child.Substring($root.Length).TrimStart('\', '/') + '（' + $_.Exception.Message + '）')
            continue
        }

        if (($childAttributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $isLink = Test-IsLink $child -AllowTagLookup
            if ($null -eq $isLink) {
                [void]$uncertain.Add($child)
                continue
            }

            if ($isLink) {
                continue
            }
        }

        if ($pruned.Contains([System.IO.Path]::GetFileName($child))) {
            continue
        }

        $pending.Push($child)
    }
}

if ($uncertain.Count -gt 0) {
    [Console]::Error.WriteLine("警告：$($uncertain.Count) 个重解析点无法确认类型，已按 fail-closed 跳过（未校验行尾）。")
    [Console]::Error.WriteLine('  常见原因是 OneDrive 云占位或未知重解析标签；请先下载文件/关闭云同步占位，或手动检查：')
    foreach ($item in $uncertain | Select-Object -First 10) {
        $relative = $item.Substring($root.Length).TrimStart('\', '/')
        [Console]::Error.WriteLine('  ' + $relative)
    }

    if ($uncertain.Count -gt 10) {
        [Console]::Error.WriteLine("  ... 其余 $($uncertain.Count - 10) 个")
    }
}

if ($fixedFiles.Count -gt 0) {
    Write-Output "已修复的行尾违规文件："
    foreach ($file in $fixedFiles) {
        Write-Output ('  ' + $file.Substring($root.Length).TrimStart('\', '/'))
    }
}

if ($unreadable.Count -gt 0) {
    [Console]::Error.WriteLine("以下路径无法读取（可能被占用或权限不足），未校验行尾：")
    foreach ($item in $unreadable) {
        [Console]::Error.WriteLine('  ' + $item)
    }

    [Console]::Error.WriteLine("共 $($unreadable.Count) 个路径，已检查 $checked 个声明文件。")
}

if ($encodingFailures.Count -gt 0) {
    [Console]::Error.WriteLine("以下文件行尾违规但编码不受支持（非 UTF-8 或含 NUL），已保持原样：")
    foreach ($item in $encodingFailures) {
        [Console]::Error.WriteLine('  ' + $item)
    }

    [Console]::Error.WriteLine("共 $($encodingFailures.Count) 个文件，已检查 $checked 个声明文件。")
}

if ($repairFailures.Count -gt 0) {
    [Console]::Error.WriteLine("以下文件未能修复（只读、占用或写入失败），已保持原样：")
    foreach ($item in $repairFailures) {
        [Console]::Error.WriteLine('  ' + $item)
    }

    [Console]::Error.WriteLine("共 $($repairFailures.Count) 个文件，已检查 $checked 个声明文件。")
}

if ($offenders.Count -gt 0) {
    [Console]::Error.WriteLine("以下文件不是纯 CRLF（请按 CRLF 保存后重试）：")
    foreach ($file in $offenders) {
        $relative = $file.Substring($root.Length).TrimStart('\', '/')
        [Console]::Error.WriteLine('  ' + $relative)
    }

    [Console]::Error.WriteLine("共 $($offenders.Count) 个文件，已检查 $checked 个声明文件。")
}

# 空扫描判定放在明细之后，避免明细被提前退出吞掉。
if ($checked -eq 0) {
    [Console]::Error.WriteLine('行尾门禁未扫描到任何声明文件，请检查 -Root 与扩展名清单。')
    exit 1
}

if ($unreadable.Count -gt 0 -or $encodingFailures.Count -gt 0 -or
    $repairFailures.Count -gt 0 -or $offenders.Count -gt 0) {
    exit 1
}

if ($fixedFiles.Count -gt 0) {
    Write-Output "行尾修复完成：共重写 $($fixedFiles.Count) 个文件为 CRLF + UTF-8 BOM（已检查 $checked 个声明文件）。"
    exit 0
}

Write-Output "行尾校验通过：$checked 个声明文件均为 CRLF。"
