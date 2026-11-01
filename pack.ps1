<#
.SYNOPSIS
    VT9Status 打包脚本：编译 → 校验版本 → 自检 → 可选截图 / 归档 / 压缩 / 装机。

.DESCRIPTION
    编译复用 build.ps1，产物默认落在 dist\VT9Status-<版本>\。

    三个环境细节（都是这一轮踩过的坑，脚本里已经处理）：
    1. VT9Status.exe 是 winexe（GUI）程序，用调用运算符启动时 PowerShell 不会等它结束，
       所以自检和截图统一走「启动 + 轮询产物文件」；Start-Process 可用时顺便拿 PID，
       超时就把还挂着的进程杀掉。受限环境（DSH 沙箱）里 Start-Process 会被拒，自动退回调用运算符。
    2. data\self-test.json 写在 exe 同级的 data 目录，Program.SaveJson 用的是 File.Replace；
       目标文件已存在时在受限 ACL 环境下会失败，所以跑自检前先删掉它和它的 .tmp。
    3. 五页预览里的 settings / dump 是浮层（Popup.StaysOpen=false），桌面上任何一处焦点变化
       都会把它关掉，所以截图偶尔截不到浮层——重跑一次即可。

.PARAMETER Version
    形如 1.16.0。给了就先把 src\AssemblyInfo.cs 的版本改成它再编译；不给就用文件里的现值。

.PARAMETER OutputDirectory
    产物目录，默认 dist\VT9Status-<版本>。只有位于仓库 dist\ 下时才会自动清空重建。

.PARAMETER Preview
    额外截五页预览图（overview / dpi / params / settings / dump），放到 <产物>\预览\，约 40 秒。

.PARAMETER Zip
    额外产出 dist\VT9Status-<版本>.zip：exe + config + 使用说明 + 包信息 +（有的话）预览图。

.PARAMETER Archive
    额外快照到 backup\<版本>-<时间戳>\：build.ps1 + src 全部 .cs + 新 exe + config + 当前版本文档。

.PARAMETER Install
    打包后装到仓库根目录：先等旧实例退出（默认最多 30 分钟），再把当前源码编译到根目录。

.PARAMETER InstallTimeoutMinutes
    -Install 等待旧实例退出的上限，默认 30 分钟；给 0 表示只试一次不等。

.PARAMETER SkipSelfTest
    跳过自检（不推荐）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\pack.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\pack.ps1 -Version 1.16.0 -Preview -Zip -Archive -Install
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [switch]$Preview,
    [switch]$Zip,
    [switch]$Archive,
    [switch]$Install,
    [int]$InstallTimeoutMinutes = 30,
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildScript = Join-Path $root 'build.ps1'
$assemblyInfo = Join-Path $root 'src\AssemblyInfo.cs'
$rootExe = Join-Path $root 'VT9Status.exe'
$rootConfig = Join-Path $root 'VT9Status.exe.config'
$readme = Join-Path $root '使用说明.md'

function Write-Step([string]$Text) { Write-Output ''; Write-Output ('== ' + $Text) }
function Write-Note([string]$Text) { Write-Output ('   ' + $Text) }

# ── 版本：读 / 写 AssemblyInfo.cs（保留原有 BOM 状态）──

function Get-SourceVersion {
    $text = [System.IO.File]::ReadAllText($assemblyInfo, [System.Text.Encoding]::UTF8)
    if ($text -notmatch 'AssemblyVersion\("(\d+\.\d+\.\d+)') { throw 'src\AssemblyInfo.cs 里找不到 AssemblyVersion("x.y.z")' }
    return $Matches[1]
}

function Set-SourceVersion([string]$NewVersion) {
    $bytes = [System.IO.File]::ReadAllBytes($assemblyInfo)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.IO.File]::ReadAllText($assemblyInfo, [System.Text.Encoding]::UTF8)
    $text = $text -replace 'AssemblyVersion\("\d+\.\d+\.\d+(\.\d+)?"\)', ('AssemblyVersion("' + $NewVersion + '.0")')
    $text = $text -replace 'AssemblyFileVersion\("\d+\.\d+\.\d+(\.\d+)?"\)', ('AssemblyFileVersion("' + $NewVersion + '.0")')
    [System.IO.File]::WriteAllText($assemblyInfo, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
}

# ── 启动 GUI 程序并等它产出文件 ──

function Invoke-App {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$WaitFile,
        [int]$TimeoutSeconds = 60,
        [string]$What = '运行'
    )
    # 先删掉旧产物：自检的 SaveJson 用 File.Replace，目标已存在时在受限环境下会失败。
    if (Test-Path -LiteralPath $WaitFile) { Remove-Item -LiteralPath $WaitFile -Force -ErrorAction SilentlyContinue }

    $proc = $null
    $where = '调用运算符'
    try {
        $proc = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -ErrorAction Stop
        $where = 'Start-Process'
    } catch {
        & $Exe @Arguments
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $produced = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $WaitFile) { $produced = $true; break }
        Start-Sleep -Milliseconds 400
    }

    if ($proc -ne $null) {
        try { if (-not $proc.HasExited) { [void]$proc.WaitForExit(15000) } } catch { }
        try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
    }

    if (-not $produced) {
        Write-Note ($What + '超时（' + $TimeoutSeconds + ' 秒内没有产出 ' + (Split-Path $WaitFile -Leaf) + '，启动方式：' + $where + '）')
    }
    return $produced
}

# ── 等一个被占用的文件放开 ──

function Wait-FileReleased {
    param([string]$Path, [int]$TimeoutMinutes, [string]$What = '文件')
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastNote = Get-Date
    while ($true) {
        try {
            $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Close()
            return $true
        } catch { }
        if ((Get-Date) -ge $deadline) { return $false }
        if (((Get-Date) - $lastNote).TotalSeconds -ge 30) {
            $lastNote = Get-Date
            $left = [int]($deadline - (Get-Date)).TotalMinutes
            Write-Note ('仍在等待' + $What + '放开…剩余约 ' + $left + ' 分钟（在托盘图标上右键 → 退出即可）')
        }
        Start-Sleep -Seconds 5
    }
}

# ── 主流程 ──

Write-Step 'VT9Status 打包'
if (-not (Test-Path -LiteralPath $buildScript)) { throw ('找不到 ' + $buildScript) }

$source = Get-SourceVersion
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '-Version 需要 x.y.z 形式，例如 1.16.0' }
    if ($Version -ne $source) {
        Set-SourceVersion $Version
        $check = Get-SourceVersion
        if ($check -ne $Version) { throw ('AssemblyInfo.cs 版本写入失败：期望 ' + $Version + '，实际 ' + $check) }
        Write-Note ('版本：' + $source + ' → ' + $Version + '（已写入 src\AssemblyInfo.cs）')
    } else {
        Write-Note ('版本：' + $Version + '（与源码一致）')
    }
} else {
    $Version = $source
    Write-Note ('版本：' + $Version + '（取自 src\AssemblyInfo.cs）')
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('dist\VT9Status-' + $Version) }
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist'))

if (Test-Path -LiteralPath $output) {
    if ($output.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Note ('清空旧产物：' + $output)
        try { Remove-Item -LiteralPath $output -Recurse -Force -ErrorAction Stop }
        catch { throw ('清不掉旧产物目录（可能有程序正在里面运行）：' + $output + ' —— ' + $_.Exception.Message) }
    } else {
        throw ('产物目录已存在且不在 dist\ 下，拒绝自动清空：' + $output)
    }
}

Write-Step '编译'
& $buildScript -OutputDirectory $output
$exe = Join-Path $output 'VT9Status.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw ('编译没有产出 ' + $exe) }
$fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($fileVersion -notlike ($Version + '*')) { throw ('产物版本 ' + $fileVersion + ' 与 ' + $Version + ' 不一致') }
$size = (Get-Item -LiteralPath $exe).Length
Write-Note ('产物：' + $exe + '（' + $size + ' 字节，FileVersion ' + $fileVersion + '）')

$selfTestText = '未执行'
if (-not $SkipSelfTest) {
    Write-Step '自检'
    $dataDir = Join-Path $output 'data'
    $json = Join-Path $dataDir 'self-test.json'
    Remove-Item -LiteralPath $json, ($json + '.tmp') -Force -ErrorAction SilentlyContinue
    if (Invoke-App -Exe $exe -Arguments @('--self-test') -WaitFile $json -TimeoutSeconds 60 -What '自检') {
        $result = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
        if ($result.result -ne 'PASS') { throw ('自检未通过：' + (Get-Content -LiteralPath $json -Raw)) }
        $selfTestText = ([string]$result.passed) + ' 项 PASS'
        Write-Note ('自检：' + $selfTestText)
    } else {
        $log = Join-Path $dataDir 'app.log'
        if (Test-Path -LiteralPath $log) { Write-Note ('app.log 末尾：' + ((Get-Content -LiteralPath $log -Tail 3) -join ' | ')) }
        throw '自检没有产出结果文件（可能弹了错误对话框，需要手动关掉 VT9Status.exe）'
    }
}

$previewFiles = @()
if ($Preview) {
    Write-Step '截图'
    $pages = @('overview', 'dpi', 'params', 'settings', 'dump')
    $previewDir = Join-Path $output '预览'
    New-Item -ItemType Directory -Path $previewDir -Force | Out-Null
    foreach ($page in $pages) {
        $png = Join-Path $output ('data\ui-preview-bg-' + $page + '.png')
        if (Invoke-App -Exe $exe -Arguments @('--ui-preview-bg', ('--page=' + $page)) -WaitFile $png -TimeoutSeconds 45 -What ('截图 ' + $page)) {
            Move-Item -LiteralPath $png -Destination $previewDir -Force
            $previewFiles += (Join-Path $previewDir ('ui-preview-bg-' + $page + '.png'))
            Write-Note ($page + '：已截')
        }
    }
    if ($page -eq 'settings' -or $page -eq 'dump') { }
    Write-Note '提示：settings / dump 是浮层页，偶尔会因桌面焦点变化截不到浮层，重跑一次即可。'
}

Write-Step '包信息'
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$sourceCount = (Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs').Count
$infoPath = Join-Path $output '包信息.txt'
$infoLines = @(
    'VT9Status 发布包'
    '版本            ' + $Version
    '打包时间        ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    '主程序          VT9Status.exe  ' + $size + ' 字节'
    'FileVersion     ' + $fileVersion
    'SHA256          ' + $hash
    '自检            ' + $selfTestText
    '源码            ' + $sourceCount + ' 个 .cs（AssemblyInfo ' + (Get-SourceVersion) + '）'
    '运行            VT9Status.exe（带界面） / VT9Status.exe --tray（只留托盘）'
    '诊断开关        --self-test / --ui-preview-bg --page=overview|dpi|params|settings|dump / --ui-motion-test'
    '配置与数据      exe 同级的 data\ 目录（settings.json / app.log / 截图）'
)
[System.IO.File]::WriteAllText($infoPath, (($infoLines -join [Environment]::NewLine) + [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($true)))
$infoLines | ForEach-Object { Write-Note $_ }

if ($Archive) {
    Write-Step '归档'
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmm')
    $archiveDir = Join-Path $root ('backup\' + $Version + '-' + $stamp)
    New-Item -ItemType Directory -Path (Join-Path $archiveDir 'src') -Force | Out-Null
    Copy-Item -LiteralPath $buildScript -Destination $archiveDir
    if (Test-Path -LiteralPath $rootConfig) { Copy-Item -LiteralPath $rootConfig -Destination $archiveDir }
    Copy-Item -LiteralPath $exe -Destination $archiveDir
    Copy-Item -Path (Join-Path $root 'src\*.cs') -Destination (Join-Path $archiveDir 'src')
    if (Test-Path -LiteralPath $readme) { Copy-Item -LiteralPath $readme -Destination $archiveDir }
    # 文档名里的版本只写到次版本号（验证记录-1.15-…），所以按 major.minor 匹配。
    $docTag = $Version
    $parts = $Version.Split('.')
    if ($parts.Length -ge 2) { $docTag = $parts[0] + '.' + $parts[1] }
    Get-ChildItem -LiteralPath $root -Filter '*.md' | Where-Object { $_.Name -like ('*' + $docTag + '*') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $archiveDir
        Write-Note ('随档文档：' + $_.Name)
    }
    $count = (Get-ChildItem -LiteralPath $archiveDir -Recurse -File).Count
    Write-Note ('已归档 ' + $count + ' 个文件到 ' + $archiveDir)
}

if ($Zip) {
    Write-Step '压缩'
    $zipPath = Join-Path $distRoot ('VT9Status-' + $Version + '.zip')
    $items = @($exe, $infoPath)
    $config = Join-Path $output 'VT9Status.exe.config'
    if (Test-Path -LiteralPath $config) { $items += $config }
    if (Test-Path -LiteralPath $readme) { $items += $readme }
    if ($previewFiles.Count -gt 0) { $items += (Join-Path $output '预览') }
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -LiteralPath $items -DestinationPath $zipPath -Force
    $zipSize = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB, 1)
    Write-Note ('已压缩：' + $zipPath + '（' + $zipSize + ' KB）')
}

$installed = '未安装'
if ($Install) {
    Write-Step '装到仓库根目录'
    Write-Note ('目标：' + $rootExe)
    if (Wait-FileReleased -Path $rootExe -TimeoutMinutes $InstallTimeoutMinutes -What '根目录 exe（旧实例）') {
        & $buildScript
        if ($LASTEXITCODE -ne 0) { throw '装到根目录的编译失败' }
        $installed = (Get-Item -LiteralPath $rootExe).VersionInfo.FileVersion
        Write-Note ('已安装：ver ' + $installed + '（' + (Get-Item -LiteralPath $rootExe).LastWriteTime.ToString('HH:mm:ss') + '）')
        if ($installed -notlike ($Version + '*')) { Write-Note ('注意：根目录产物版本与本次打包的 ' + $Version + ' 不一致') }
    } else {
        $installed = '跳过（旧实例一直占着，超时 ' + $InstallTimeoutMinutes + ' 分钟）'
        Write-Note ('跳过装机：' + $installed)
        Write-Note '在托盘图标上右键 → 退出（或打开面板点底部「退出」）后重跑本脚本即可。'
    }
}

Write-Step '完成'
Write-Note ('产物目录：' + $output)
Write-Note ('版本：' + $Version + '　自检：' + $selfTestText + '　安装：' + $installed)
