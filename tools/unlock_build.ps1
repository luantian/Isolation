# 解除构建文件锁定脚本
# 使用方法：以管理员身份运行此脚本

param(
    [string]$ProjectPath = "F:\workspace\cechuang\projects\Isolation",
    [switch]$Force
)

$ErrorActionPreference = "Continue"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  解除构建文件锁定工具" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host

# 检查 obj 目录是否存在
$objPath = Join-Path $ProjectPath "src\IsolationLeakage.App\obj"
if (-not (Test-Path $objPath)) {
    Write-Host "obj 目录不存在，无需解除锁定" -ForegroundColor Green
    exit 0
}

Write-Host "步骤 1: 关闭 dotnet build server..." -ForegroundColor Yellow
dotnet build-server shutdown 2>$null
Start-Sleep -Seconds 2

Write-Host "步骤 2: 查找可能锁定文件的进程..." -ForegroundColor Yellow

# 查找可能锁定文件的进程
$suspectProcesses = @("dotnet", "msbuild", "vbcscompiler", "IsolationLeakage.App")
$foundProcesses = @()

foreach ($processName in $suspectProcesses) {
    $procs = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($procs) {
        $foundProcesses += $procs
        Write-Host "  找到: $($procs.Name) (PID: $($procs.Id -join ', '))" -ForegroundColor Gray
    }
}

if ($foundProcesses.Count -gt 0) {
    Write-Host
    
    if ($Force) {
        Write-Host "步骤 3: 强制终止进程..." -ForegroundColor Yellow
        foreach ($proc in $foundProcesses) {
            try {
                Stop-Process -Id $proc.Id -Force
                Write-Host "  已终止: $($proc.Name) (PID: $($proc.Id))" -ForegroundColor Green
            }
            catch {
                Write-Host "  无法终止: $($proc.Name) (PID: $($proc.Id)) - $($_.Exception.Message)" -ForegroundColor Red
            }
        }
        Start-Sleep -Seconds 2
    }
    else {
        Write-Host
        Write-Host "发现以上进程可能锁定构建文件。" -ForegroundColor Yellow
        Write-Host "使用 -Force 参数可自动终止这些进程。" -ForegroundColor Gray
        Write-Host "或手动关闭 Visual Studio 和相关进程。" -ForegroundColor Gray
    }
}
else {
    Write-Host "  未找到明显的锁定进程" -ForegroundColor Gray
}

Write-Host
Write-Host "步骤 4: 清理 obj 和 bin 目录..." -ForegroundColor Yellow

try {
    $objPath = Join-Path $ProjectPath "src\IsolationLeakage.App\obj"
    $binPath = Join-Path $ProjectPath "src\IsolationLeakage.App\bin"
    
    if (Test-Path $objPath) {
        Remove-Item -Path $objPath -Recurse -Force -ErrorAction Stop
        Write-Host "  已清理 obj 目录" -ForegroundColor Green
    }
    
    if (Test-Path $binPath) {
        Remove-Item -Path $binPath -Recurse -Force -ErrorAction Stop
        Write-Host "  已清理 bin 目录" -ForegroundColor Green
    }
    
    Write-Host
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  锁定已解除！可以重新构建了" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host
    Write-Host "下一步操作：" -ForegroundColor Cyan
    Write-Host "  1. 关闭 Visual Studio（如果正在运行）" -ForegroundColor Gray
    Write-Host "  2. 重新运行: dotnet build" -ForegroundColor Gray
}
catch {
    Write-Host "  清理失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host
    Write-Host "建议操作：" -ForegroundColor Yellow
    Write-Host "  1. 关闭 Visual Studio" -ForegroundColor Gray
    Write-Host "  2. 关闭所有文件资源管理器窗口" -ForegroundColor Gray
    Write-Host "  3. 重启电脑后重试" -ForegroundColor Gray
    exit 1
}

exit 0
