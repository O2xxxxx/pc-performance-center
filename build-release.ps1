# PC Performance Center —— 一键打包脚本
# 用法（在项目根目录）：  powershell -ExecutionPolicy Bypass -File .\build-release.ps1
param(
    [string]$Configuration = "Release",
    # 目标架构：x64（默认）或 arm64（Surface 等 ARM 笔记本）
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [string]$Runtime = "",
    [switch]$FrameworkDependent
)

if ([string]::IsNullOrWhiteSpace($Runtime)) { $Runtime = "win-$Architecture" }

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$out = Join-Path $root "artifacts"

Write-Host "==> 清理旧产物" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$publishArgs = @(
    "publish", (Join-Path $root "src\PPC.App\PPC.App.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", (Join-Path $out "PCPerformanceCenter"),
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:SatelliteResourceLanguages=zh-Hans",
    "--nologo"
)

if (-not $FrameworkDependent) { $publishArgs += "--self-contained", "true" }
else { $publishArgs += "--self-contained", "false" }

Write-Host "==> dotnet $($publishArgs -join ' ')" -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $out "PCPerformanceCenter\PCPerformanceCenter.exe"
if (-not (Test-Path $exe)) { throw "未找到生成的 exe：$exe" }

$zip = Join-Path $out "PCPerformanceCenter-$Runtime.zip"
Write-Host "==> 压缩为 $zip" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $out "PCPerformanceCenter\*") -DestinationPath $zip -Force

Write-Host ""
Write-Host "完成：" -ForegroundColor Green
Write-Host "  EXE : $exe"
Write-Host "  ZIP : $zip"
Write-Host ""
Write-Host "提示：实时监控与硬件控制建议右键「以管理员身份运行」。" -ForegroundColor Yellow
