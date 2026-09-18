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
Write-Host "  KooDesk 一键构建开始" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

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

$DistDir = Join-Path $ProjectRoot "dist"
if (-not (Test-Path -LiteralPath $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
}

$Running = Get-Process -Name "kooDESK", "KooDesk", "DesktopIconLock" -ErrorAction SilentlyContinue
if ($Running) {
    Write-Host "[2/4] 正在关闭后台运行中的 KooDesk 进程..." -ForegroundColor Yellow
    $Running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

$MainOutput = Join-Path $DistDir "kooDESK.exe"
$MainSources = Get-ChildItem -Path (Join-Path $ProjectRoot "KooDesk") -Filter "*.cs" -Recurse | Select-Object -ExpandProperty FullName
$IconDir = Join-Path $ProjectRoot "KooDesk\Resources"
$FaviconPath = Join-Path $IconDir "favicon.ico"

Write-Host "[3/4] 正在编译主程序 -> $MainOutput" -ForegroundColor Cyan
$CscArgs = @('/nologo','/target:winexe','/optimize+',"/out:$MainOutput",'/r:System.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:CustomMarshalers.dll')
if (Test-Path -LiteralPath $FaviconPath) {
    $CscArgs += "/win32icon:$FaviconPath"
}
$RedIcoPath = Join-Path $IconDir "red.ico"
$GreenIcoPath = Join-Path $IconDir "green.ico"
if (Test-Path -LiteralPath $RedIcoPath) {
    $CscArgs += "/resource:$RedIcoPath,red.ico"
}
if (Test-Path -LiteralPath $GreenIcoPath) {
    $CscArgs += "/resource:$GreenIcoPath,green.ico"
}
$CscArgs += $MainSources
& $CscPath @CscArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "主程序 kooDESK.exe 编译失败。"
    exit $LASTEXITCODE
}

$TestOutput = Join-Path $DistDir "TestRunner.exe"
if ($RunTests) {
    $TestSources = @(
        (Join-Path $ProjectRoot "KooDesk\Native\User32.cs"),
        (Join-Path $ProjectRoot "KooDesk\Native\ShellInterop.cs"),
        (Join-Path $ProjectRoot "KooDesk\Native\DisplayInfo.cs"),
        (Join-Path $ProjectRoot "KooDesk\Core\IconAccessor.cs"),
        (Join-Path $ProjectRoot "KooDesk\Core\DesktopLayoutChangeDetector.cs"),
        (Join-Path $ProjectRoot "KooDesk\Core\DesktopLocationEventClassifier.cs"),
        (Join-Path $ProjectRoot "KooDesk\Core\LayoutStore.cs"),
        (Join-Path $ProjectRoot "KooDesk\Core\AdaptiveMapper.cs"),
        (Join-Path $ProjectRoot "KooDesk\Core\StartupManager.cs"),
        (Join-Path $ProjectRoot "Tests\TestRunner.cs")
    )

    Write-Host "[4/4] 正在编译测试程序 -> $TestOutput" -ForegroundColor Cyan
    & $CscPath /nologo /target:exe /optimize+ /out:"$TestOutput" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:CustomMarshalers.dll $TestSources

    if ($LASTEXITCODE -ne 0) {
        Write-Error "测试程序 TestRunner.exe 编译失败。"
        exit $LASTEXITCODE
    }
}

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "  编译成功！成品输出目录: $DistDir" -ForegroundColor Green
Write-Host "  - $MainOutput" -ForegroundColor Green
if ($RunTests) {
    Write-Host "  - $TestOutput" -ForegroundColor Green
}
Write-Host "==================================================" -ForegroundColor Green

if ($RunTests) {
    Write-Host "`n正在执行自动化全套测试..." -ForegroundColor Cyan
    & $TestOutput
    $testExit = $LASTEXITCODE

    if ($testExit -ne 0) {
        Write-Host "==================================================" -ForegroundColor Red
        Write-Host "  自动化测试未通过，退出码 $testExit" -ForegroundColor Red
        Write-Host "==================================================" -ForegroundColor Red
        exit $testExit
    }

    Write-Host "  自动化测试全部通过" -ForegroundColor Green
}

if ($RunAfterBuild) {
    Write-Host "`n正在启动后台托盘服务..." -ForegroundColor Cyan
    Start-Process -FilePath $MainOutput -WindowStyle Hidden
}
