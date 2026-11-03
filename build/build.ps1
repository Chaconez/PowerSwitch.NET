# ============================================================
#  PowerSwitch 构建脚本
# ------------------------------------------------------------
#  直接调用 .NET Framework 自带的 C# 编译器，不依赖 MSBuild/Roslyn。
#  这样即使机器上只有 Windows 自带的 .NET Framework，也能构建。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File build.ps1
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Release
# ============================================================
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # 仓库根
if (-not (Test-Path (Join-Path $root 'src'))) { $root = Split-Path -Parent $PSScriptRoot }
$srcDir = Join-Path $root 'src'
$wdDir  = Join-Path $root 'watchdog'
$outDir = Join-Path $PSScriptRoot 'out'

# ---------- 定位编译器 ----------
$fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $fwDir)) {
    $fwDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
if (-not (Test-Path $fwDir)) {
    throw "找不到 .NET Framework 4.x 目录：$fwDir"
}

$compiler = Get-ChildItem $fwDir -Filter '*.exe' |
            Where-Object { $_.Name -like 'c?c.exe' } |
            Select-Object -First 1
if (-not $compiler) { throw "在 $fwDir 中找不到 C# 编译器" }

Write-Host "编译器 : $($compiler.FullName)"
Write-Host "版本   : $($compiler.VersionInfo.FileVersion)"
Write-Host "配置   : $Configuration"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Invoke-Build {
    param(
        [string]$ProjectDir,
        [string]$AssemblyName,
        [string]$OutputType,          # winexe / exe
        [string[]]$ExtraReferences,
        [string[]]$ExtraResources,
        [string]$IconPath,
        [string]$ManifestPath
    )

    $sources = @(Get-ChildItem $ProjectDir -Filter '*.cs' -Recurse |
                 Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
                 ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) { throw "$ProjectDir 下没有 .cs 源文件" }

    $refs = @('System.dll', 'System.Core.dll') + $ExtraReferences
    $args = @(
        "/target:$OutputType"
        "/out:$(Join-Path $outDir $AssemblyName).exe"
        "/nologo"
        "/optimize+"
        "/platform:anycpu"
        "/warn:4"
    )
    if ($Configuration -eq 'Debug') {
        $args = $args | Where-Object { $_ -ne '/optimize+' }
        $args += '/debug+'
        $args += '/define:DEBUG'
    } else {
        $args += '/define:TRACE'
    }
    if ($IconPath -and (Test-Path $IconPath)) { $args += "/win32icon:$IconPath" }
    # 自定义 manifest：声明 DPI 感知（否则 200% 缩放下整个窗口被位图放大，文字发虚）
    if ($ManifestPath -and (Test-Path $ManifestPath)) { $args += "/win32manifest:$ManifestPath" }

    foreach ($r in $refs) {
        $p = Join-Path $fwDir $r
        if (Test-Path $p) { $args += "/r:$p" }
    }
    if ($ExtraResources) {
        foreach ($res in $ExtraResources) { $args += "/resource:$res" }
    }
    $args += $sources

    Write-Host ""
    Write-Host ">> 构建 $AssemblyName.exe ($($sources.Count) 个源文件)" -ForegroundColor Cyan
    $output = & $compiler.FullName $args 2>&1
    $code = $LASTEXITCODE
    if ($output) { $output | ForEach-Object { Write-Host "   $_" } }

    $exe = Join-Path $outDir "$AssemblyName.exe"
    if ($code -ne 0 -or -not (Test-Path $exe)) {
        throw "构建 $AssemblyName 失败（exit=$code）"
    }
    $info = Get-Item $exe
    Write-Host ("   完成: {0}  {1:N0} bytes  {2}" -f $info.Name, $info.Length, $info.LastWriteTime) -ForegroundColor Green
}

# ---------- 主程序 ----------
$icons = @(Get-ChildItem (Join-Path $srcDir 'icons') -Filter '*.ico' -ErrorAction SilentlyContinue |
           ForEach-Object { "$($_.FullName),PowerSwitch.icons.$($_.Name)" })

Invoke-Build -ProjectDir $srcDir `
             -AssemblyName 'PowerSwitch' `
             -OutputType 'winexe' `
             -ExtraReferences @('System.Windows.Forms.dll', 'System.Drawing.dll') `
             -ExtraResources $icons `
             -IconPath (Join-Path $srcDir 'icons\icon-balanced.ico') `
             -ManifestPath (Join-Path $srcDir 'app.manifest')

# ---------- 看门狗 ----------
if (Test-Path $wdDir) {
    Invoke-Build -ProjectDir $wdDir `
                 -AssemblyName 'PowerSwitchWatchdog' `
                 -OutputType 'exe' `
                 -ExtraReferences @('System.Management.dll', 'System.ServiceProcess.dll') `
                 -ExtraResources @() `
                 -IconPath $null
}

Write-Host ""
Write-Host "构建产物目录: $outDir" -ForegroundColor Cyan
Get-ChildItem $outDir -Filter '*.exe' | ForEach-Object {
    Write-Host ("  {0,-32} {1,10:N0} bytes" -f $_.Name, $_.Length)
}
