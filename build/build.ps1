# ============================================================
#  PowerSwitch 构建脚本（.NET 8）
# ------------------------------------------------------------
#  依赖 .NET 8 SDK（dotnet 命令）。
#  发布方式：框架依赖 + 单文件 —— 产物是单个 exe，体积约 1 MB，
#  但目标机必须装 .NET 8 Desktop Runtime（WindowsDesktop.App 8.x）。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File build.ps1
#    powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Debug
# ============================================================
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root 'src'
$wdDir  = Join-Path $root 'watchdog'
$outDir = Join-Path $PSScriptRoot 'out'

# ---------- 定位 dotnet ----------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    throw "找不到 dotnet 命令。请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0"
}

$sdkList = & $dotnet --list-sdks 2>$null
if (-not ($sdkList | Where-Object { $_ -match '^8\.' })) {
    throw "未检测到 .NET 8 SDK。当前已装：`n$($sdkList -join "`n")"
}

Write-Host "dotnet : $dotnet" -ForegroundColor Gray
Write-Host "SDK    : $((($sdkList | Where-Object { $_ -match '^8\.' }) -join ', '))" -ForegroundColor Gray
Write-Host "配置   : $Configuration" -ForegroundColor Gray

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Invoke-Publish {
    param(
        [string]$ProjectPath,
        [string]$AssemblyName
    )

    Write-Host ""
    Write-Host ">> 发布 $AssemblyName" -ForegroundColor Cyan
    $log = & $dotnet publish $ProjectPath -c $Configuration -o $outDir 2>&1
    $code = $LASTEXITCODE
    $log | Where-Object { $_ -match 'error|warning' } | ForEach-Object { Write-Host "   $_" -ForegroundColor Yellow }

    $exe = Join-Path $outDir "$AssemblyName.exe"
    if ($code -ne 0 -or -not (Test-Path $exe)) {
        throw "发布 $AssemblyName 失败（exit=$code）"
    }
    $info = Get-Item $exe
    Write-Host ("   完成: {0}  {1:N0} bytes  {2}" -f $info.Name, $info.Length, $info.LastWriteTime) -ForegroundColor Green
}

Invoke-Publish -ProjectPath (Join-Path $srcDir 'PowerSwitch.csproj')         -AssemblyName 'PowerSwitch'
Invoke-Publish -ProjectPath (Join-Path $wdDir  'PowerSwitchWatchdog.csproj') -AssemblyName 'PowerSwitchWatchdog'

Write-Host ""
Write-Host "构建产物目录: $outDir" -ForegroundColor Cyan
Get-ChildItem $outDir -Filter '*.exe' | ForEach-Object {
    Write-Host ("  {0,-32} {1,10:N0} bytes" -f $_.Name, $_.Length)
}
Write-Host ""
Write-Host "提醒：这是框架依赖发布，目标机需装 .NET 8 Desktop Runtime。" -ForegroundColor Yellow
