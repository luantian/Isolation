# ============================================================
# SQL Server 完全卸载脚本
# 卸载所有 SQL Server 实例和组件
# 注意：这是不可逆操作！所有数据库将丢失！
# ============================================================

#Requires -RunAsAdministrator

$ErrorActionPreference = "Continue"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Red
Write-Host "  SQL Server 完全卸载脚本" -ForegroundColor Red
Write-Host "============================================================" -ForegroundColor Red
Write-Host ""
Write-Host "  警告：此操作将卸载所有 SQL Server 组件，所有数据库将丢失！" -ForegroundColor Red
Write-Host ""

$confirm = Read-Host "  确认要卸载？输入 YES 继续"
if ($confirm -ne "YES") {
    Write-Host "  已取消" -ForegroundColor Yellow
    exit 0
}

Write-Host ""
Write-Host "  [1/4] 停止所有 SQL Server 服务..." -ForegroundColor Cyan

$sqlServices = Get-Service -Name "MSSQL*", "SQLBrowser", "SQLWriter", "SQLAgent*" -ErrorAction SilentlyContinue
foreach ($svc in $sqlServices) {
    if ($svc.Status -eq "Running") {
        Write-Host "    停止: $($svc.DisplayName)"
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 3

Write-Host ""
Write-Host "  [2/4] 卸载 SQL Server 组件..." -ForegroundColor Cyan

# 查找所有 SQL Server 相关程序
$uninstallKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
)

$sqlPrograms = @()
foreach ($key in $uninstallKeys) {
    $items = Get-ItemProperty $key -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.DisplayName -like "*SQL Server*" -or $_.DisplayName -like "*Microsoft SQL*") `
            -and $_.DisplayName -notlike "*MySQL*" `
            -and $_.DisplayName -notlike "*PostgreSQL*" `
            -and $_.UninstallString
        }
    $sqlPrograms += $items
}

# 去重
$sqlPrograms = $sqlPrograms | Sort-Object DisplayName -Unique

if ($sqlPrograms.Count -eq 0) {
    Write-Host "    未找到已安装的 SQL Server 程序" -ForegroundColor Yellow
} else {
    Write-Host "    找到 $($sqlPrograms.Count) 个 SQL Server 组件：" -ForegroundColor Yellow
    foreach ($prog in $sqlPrograms) {
        Write-Host "      - $($prog.DisplayName)"
    }
    Write-Host ""

    # 尝试静默卸载
    foreach ($prog in $sqlPrograms) {
        $uninstallString = $prog.UninstallString
        Write-Host "    卸载: $($prog.DisplayName)" -ForegroundColor Gray

        try {
            # SQL Server 卸载通常需要通过 setup.exe
            if ($uninstallString -match "setup\.exe") {
                $args = "/ACTION=Uninstall /FEATURES=ALL /QUIET /IACCEPTSQLSERVERLICENSETERMS"
                $exePath = $uninstallString -replace '"', ''
                if (Test-Path $exePath) {
                    Start-Process -FilePath $exePath -ArgumentList $args -Wait -ErrorAction SilentlyContinue
                }
            } else {
                # 使用 msiexec 或标准卸载命令
                if ($uninstallString -like "MsiExec*") {
                    $msiArgs = $uninstallString -replace "MsiExec.exe\s*/I", "/X"
                    $msiArgs = "$msiArgs /QUIET /NORESTART"
                    Start-Process -FilePath "msiexec.exe" -ArgumentList ($msiArgs -replace "msiexec.exe\s*", "") -Wait -ErrorAction SilentlyContinue
                } else {
                    # 通用卸载
                    $exePath = ($uninstallString -split '"')[1]
                    if ($exePath -and (Test-Path $exePath)) {
                        Start-Process -FilePath $exePath -ArgumentList "/S" -Wait -ErrorAction SilentlyContinue
                    } else {
                        Start-Process -FilePath "cmd.exe" -ArgumentList "/c `"$uninstallString`" /S" -Wait -ErrorAction SilentlyContinue
                    }
                }
            }
        }
        catch {
            Write-Host "      !! 自动卸载失败: $_" -ForegroundColor Yellow
            Write-Host "      请手动在「控制面板 → 程序和功能」中卸载" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "  [3/4] 清理残留服务和注册表..." -ForegroundColor Cyan

# 删除残留服务
$remainingServices = Get-Service -Name "MSSQL*", "SQLBrowser", "SQLWriter", "SQLAgent*" -ErrorAction SilentlyContinue
foreach ($svc in $remainingServices) {
    Write-Host "    删除服务: $($svc.Name)"
    sc.exe delete $svc.Name | Out-Null
}

# 清理注册表
$regPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server",
    "HKLM:\SOFTWARE\Microsoft\MSSQLServer",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server"
)
foreach ($path in $regPaths) {
    if (Test-Path $path) {
        Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "    清理注册表: $path"
    }
}

Write-Host ""
Write-Host "  [4/4] 清理残留文件..." -ForegroundColor Cyan

$folders = @(
    "$env:ProgramFiles\Microsoft SQL Server",
    "${env:ProgramFiles(x86)}\Microsoft SQL Server",
    "$env:ProgramData\Microsoft\SQL Server",
    "$env:LOCALAPPDATA\Microsoft\Microsoft SQL Server",
    "C:\Program Files\Microsoft SQL Server",
    "C:\Program Files (x86)\Microsoft SQL Server"
)

foreach ($folder in $folders) {
    if (Test-Path $folder) {
        Remove-Item -Path $folder -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "    删除: $folder"
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  SQL Server 卸载完成！" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  建议重启电脑后再运行「一键部署.bat」" -ForegroundColor Cyan
Write-Host ""

$restart = Read-Host "  是否现在重启电脑？(y/N)"
if ($restart -eq 'y' -or $restart -eq 'Y') {
    Restart-Computer -Force
} else {
    Write-Host "  请手动重启电脑" -ForegroundColor Yellow
    Read-Host "按回车退出"
}
