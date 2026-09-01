# ==============================================================================
# DesktopIconLock 一键编译脚本
# 作用：自动定位系统 .NET 编译工具，将主程序与测试套件编译到 dist 目录
# ==============================================================================

[CmdletBinding()]
param(
    [switch]$RunAfterBuild,
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Get-Location
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  DesktopIconLock 一键构建开始" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. 查找 csc.exe 编译器
$CscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$CscPath = $null
foreach ($candidate in $CscCandidates) {
    if (Test-Path -LiteralPath $candidate) {
        $CscPath = $candidate
        break
    }
}

if (-not $CscPath) {
    Write-Error "未找到 .NET Framework csc.exe 编译器，请确认已安装 .NET Framework 4.5+。"
    exit 1
}

Write-Host "[1/4] 编译器定位成功: $CscPath" -ForegroundColor Green

# 2. 创建 dist 输出目录并停止正在运行的旧实例
$DistDir = Join-Path $ProjectRoot "dist"
if (-not (Test-Path -LiteralPath $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

$Running = Get-Process -Name "DesktopIconLock" -ErrorAction SilentlyContinue
if ($Running) {
    Write-Host "[2/4] 正在关闭后台运行中的 DesktopIconLock 进程..." -ForegroundColor Yellow
    $Running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

# 3. 编译主程序 DesktopIconLock.exe -> dist/
$MainOutput = Join-Path $DistDir "DesktopIconLock.exe"
$MainSources = Get-ChildItem -Path (Join-Path $ProjectRoot "DesktopIconLock") -Filter "*.cs" -Recurse | Select-Object -ExpandProperty FullName

Write-Host "[3/4] 正在编译主程序 -> $MainOutput" -ForegroundColor Cyan
& $CscPath /nologo /target:winexe /optimize+ /out:"$MainOutput" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:CustomMarshalers.dll $MainSources

if ($LASTEXITCODE -ne 0) {
    Write-Error "主程序 DesktopIconLock.exe 编译失败。"
    exit $LASTEXITCODE
}

# 4. 编译测试套件 TestRunner.exe -> dist/
$TestOutput = Join-Path $DistDir "TestRunner.exe"
$TestSources = @(
    (Join-Path $ProjectRoot "DesktopIconLock\Common\AuditLogger.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Native\User32.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Native\ShellInterop.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Native\DisplayInfo.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\IconAccessor.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\DesktopLayoutChangeDetector.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\DesktopLocationEventClassifier.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\LayoutStore.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\AdaptiveMapper.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\StartupManager.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\ScreenCaptureService.cs"),
    (Join-Path $ProjectRoot "DesktopIconLock\Core\HistoryStore.cs"),
    (Join-Path $ProjectRoot "Tests\TestRunner.cs")
)

Write-Host "[4/4] 正在编译测试程序 -> $TestOutput" -ForegroundColor Cyan
& $CscPath /nologo /target:exe /optimize+ /out:"$TestOutput" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:CustomMarshalers.dll $TestSources

if ($LASTEXITCODE -ne 0) {
    Write-Error "测试程序 TestRunner.exe 编译失败。"
    exit $LASTEXITCODE
}

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "  编译成功！成品输出目录: $DistDir" -ForegroundColor Green
Write-Host "  - $MainOutput" -ForegroundColor Green
Write-Host "  - $TestOutput" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green

# 自动启动或执行测试
if ($RunTests) {
    Write-Host "`n正在执行自动化全套测试..." -ForegroundColor Cyan
    & $TestOutput
}

if ($RunAfterBuild) {
    Write-Host "`n正在启动后台托盘服务..." -ForegroundColor Cyan
    Start-Process -FilePath $MainOutput -WindowStyle Hidden
}
