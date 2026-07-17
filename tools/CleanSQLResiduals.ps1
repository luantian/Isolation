# Clean residual SQL Server registry entries
# Run this BEFORE reinstalling SQL Server

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  清理 SQL Server 残留注册表" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# 1. 删除 SQL Server 160 相关注册表项
$pathsToRemove = @(
    "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\160",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server\160",
    "HKLM:\SYSTEM\CurrentControlSet\Services\SQLWriter",
    "HKLM:\SYSTEM\CurrentControlSet\Services\SQLBrowser",
    "HKLM:\SYSTEM\CurrentControlSet\Services\SQLTELEMETRY",
    "HKLM:\SYSTEM\CurrentControlSet\Services\SQLSERVERAGENT"
)

# 也删除 MSSQL16 开头的服务残留
$mssqlPaths = Get-ChildItem "HKLM:\SYSTEM\CurrentControlSet\Services" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "*MSSQL16*" -or $_.Name -like "*MSSQL`$*" }
foreach ($p in $mssqlPaths) {
    $pathsToRemove += $p.PSPath
}

foreach ($p in $pathsToRemove) {
    if (Test-Path $p) {
        Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed: $p" -ForegroundColor Green
    }
}

# 2. 清理 Microsoft SQL Server 主键
$mainKey = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server"
if (Test-Path $mainKey) {
    Write-Host ""
    Write-Host "主键下的子项:"
    Get-ChildItem $mainKey -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  $($_.PSChildName)" -ForegroundColor Yellow
    }

    # 删除 Setup 子项（安装程序缓存）
    $setupKey = Join-Path $mainKey "Setup"
    if (Test-Path $setupKey) {
        Remove-Item $setupKey -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed: Setup" -ForegroundColor Green
    }

    # 删除 Instance Names
    $instNamesKey = Join-Path $mainKey "Instance Names"
    if (Test-Path $instNamesKey) {
        Remove-Item $instNamesKey -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Removed: Instance Names" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "  注册表清理完成！" -ForegroundColor Green
Write-Host "  现在可以重新运行 deploy.bat 选 1 安装 SQL Server" -ForegroundColor Cyan
Write-Host ""

Read-Host "按回车退出"
