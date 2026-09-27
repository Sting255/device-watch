# ============================================================================
#  build.ps1 —— 一键构建
#
#  需要 .NET SDK（只编译时用；产物是 .NET Framework 4.8 的单 exe，
#  用户那边 Win10 1803+ / Win11 都自带运行时，不用装任何东西）。
#
#    .\build.ps1              构建并输出到项目根目录
#    .\build.ps1 -Run         构建完顺便启动
#    .\build.ps1 -Test        构建完跑一遍接口自检
# ============================================================================
param(
    [switch]$Run,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src'

Write-Host '正在构建…' -ForegroundColor Cyan
Push-Location $proj
try {
    # --no-incremental 很重要：增量构建会复用 obj 缓存，
    # 有时报"0 错误"其实编译器根本没重新跑，会骗人。
    & dotnet build -c Release --no-incremental -v:q --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
}
finally { Pop-Location }

$exe = Join-Path $proj 'bin\Release\net48\设备连接监控.exe'
if (-not (Test-Path $exe)) { throw "找不到构建产物：$exe" }
Copy-Item $exe $root -Force
Copy-Item ($exe + '.config') $root -Force -ErrorAction SilentlyContinue

$size = [Math]::Round((Get-Item (Join-Path $root '设备连接监控.exe')).Length / 1KB)
Write-Host ("构建完成：设备连接监控.exe（{0} KB）" -f $size) -ForegroundColor Green

if ($Run) {
    Write-Host '启动中…' -ForegroundColor Cyan
    Start-Process (Join-Path $root '设备连接监控.exe')
}
if ($Test) {
    Write-Host '等待面板就绪…' -ForegroundColor Cyan
    $ok = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        try { $s = Invoke-RestMethod 'http://127.0.0.1:8787/api/state' -TimeoutSec 3; $ok = $true; break } catch { }
    }
    if (-not $ok) { Write-Host '面板没有响应' -ForegroundColor Red; exit 1 }
    Write-Host ("自检通过：设备 {0} 台，CPU {1}C，内存 {2}%" -f $s.devices, $s.hw.CpuTempC, $s.hw.MemPct) -ForegroundColor Green
}