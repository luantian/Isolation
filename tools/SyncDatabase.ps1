# ============================================================
# 数据库主从同步脚本
# 功能：将主数据库备份并还原到从数据库，保持数据同步
# 用法：通过 Windows 任务计划程序定期执行
#
# 重要：
#   - 如果主库和从库在不同机器上，BackupDir 必须是两台机器
#     都能访问的共享路径（UNC 路径，如 \\server\share\DbSync）
#   - SQL Server 服务账户对 BackupDir 需要有读写权限
#   - 建议使用 Windows 身份验证，两台机器需在同一域或信任域
#
# 示例：
#   # 同机器两个实例：
#   .\SyncDatabase.ps1 -PrimaryServer ".\SQLEXPRESS" -SecondaryServer ".\SQLEXPRESS2"
#
#   # 跨机器（用共享路径）：
#   .\SyncDatabase.ps1 -PrimaryServer "192.168.1.100\SQLEXPRESS" `
#                       -SecondaryServer "192.168.1.101\SQLEXPRESS" `
#                       -BackupDir "\\192.168.1.100\DbShare\Sync"
# ============================================================

param(
    [string]$PrimaryServer = ".\SQLEXPRESS",
    [string]$SecondaryServer = "",
    [string]$DatabaseName = "IsolationLeakageDb",
    [string]$BackupDir = "",
    [int]$RetentionDays = 7,
    [string]$LogDir = ""
)

# ============================================================
# 初始化
# ============================================================

$script:StartTime = Get-Date
$script:LogFile = $null

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $Message"
    Write-Host $line -ForegroundColor $Color
    if ($script:LogFile) {
        Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
    }
}

# 默认备份目录：脚本所在目录下的 DbSync 子目录
if ([string]::IsNullOrWhiteSpace($BackupDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $BackupDir = Join-Path $scriptDir "DbSync"
}

# 默认日志目录：脚本所在目录下的 logs 子目录
if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $LogDir = Join-Path $scriptDir "logs"
}

# 确保日志目录存在
if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

$logDate = Get-Date -Format "yyyyMMdd"
$script:LogFile = Join-Path $LogDir "sync-database-$logDate.log"

# ============================================================
# 参数校验
# ============================================================

if ([string]::IsNullOrWhiteSpace($SecondaryServer)) {
    Write-Log "错误：必须指定从库服务器地址（-SecondaryServer）" "Red"
    Write-Log "用法示例：" "Yellow"
    Write-Log '  .\SyncDatabase.ps1 -PrimaryServer ".\SQLEXPRESS" -SecondaryServer ".\SQLEXPRESS2"' "Yellow"
    exit 1
}

if ($RetentionDays -lt 1) {
    Write-Log "错误：RetentionDays 必须 >= 1" "Red"
    exit 1
}

# ============================================================
# 环境检查
# ============================================================

Write-Log "============================================" "Cyan"
Write-Log " 数据库主从同步脚本" "Cyan"
Write-Log "============================================" "Cyan"
Write-Log ""
Write-Log "主库: $PrimaryServer"
Write-Log "从库: $SecondaryServer"
Write-Log "数据库: $DatabaseName"
Write-Log "备份目录: $BackupDir"
Write-Log "日志文件: $($script:LogFile)"
Write-Log ""

# 检查备份目录是否存在且可访问
if (-not (Test-Path $BackupDir)) {
    Write-Log "备份目录不存在，正在创建: $BackupDir" "Yellow"
    try {
        New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    }
    catch {
        Write-Log "错误：无法创建备份目录 $BackupDir" "Red"
        Write-Log "  如果是跨机器同步，BackupDir 必须是共享路径（如 \\\\server\\share\\DbSync）" "Yellow"
        Write-Log "  详细信息: $_" "Red"
        exit 1
    }
}

# 测试备份目录的读写权限
$testFile = Join-Path $BackupDir "_permission_test.tmp"
try {
    [System.IO.File]::WriteAllText($testFile, "test")
    Remove-Item $testFile -Force
}
catch {
    Write-Log "错误：备份目录 $BackupDir 无写入权限" "Red"
    Write-Log "  请确保 SQL Server 服务账户对此目录有读写权限" "Yellow"
    Write-Log "  详细信息: $_" "Red"
    exit 1
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupFile = Join-Path $BackupDir "$DatabaseName`_$timestamp.bak"

# 对 SQL 中的文件路径进行转义（防止路径中有单引号）
$backupFileEscaped = $backupFile -replace "'", "''"

# ============================================================
# 执行同步
# ============================================================

try {
    # 第一步：备份主库
    Write-Log "[1/3] 正在备份主库..." "Yellow"
    $backupSql = @"
BACKUP DATABASE [$DatabaseName]
TO DISK = N'$backupFileEscaped'
WITH FORMAT, INIT, NAME = N'$DatabaseName-Full Backup', SKIP, NOREWIND, NOUNLOAD, STATS = 10
"@

    $primaryConnStr = "Server=$PrimaryServer;Database=master;Integrated Security=True;TrustServerCertificate=True;"
    $primaryConn = New-Object System.Data.SqlClient.SqlConnection($primaryConnStr)
    $primaryConn.Open()
    $cmd = $primaryConn.CreateCommand()
    $cmd.CommandText = $backupSql
    $cmd.CommandTimeout = 300
    $cmd.ExecuteNonQuery() | Out-Null
    $primaryConn.Close()
    $primaryConn.Dispose()

    # 验证备份文件确实生成了
    if (-not (Test-Path $backupFile)) {
        throw "备份命令执行成功但备份文件不存在: $backupFile`n  可能原因：SQL Server 服务账户对 $BackupDir 无写入权限"
    }
    $backupSize = [math]::Round((Get-Item $backupFile).Length / 1MB, 2)
    Write-Log "  OK 主库备份成功: $backupFile ($backupSize MB)" "Green"

    # 第二步：在从库上还原
    Write-Log "[2/3] 正在还原到从库..." "Yellow"
    $restoreSql = @"
-- 如果从库数据库存在，先设置为单用户模式踢掉其他连接，然后还原
IF DB_ID('$DatabaseName') IS NOT NULL
BEGIN
    ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    RESTORE DATABASE [$DatabaseName]
    FROM DISK = N'$backupFileEscaped'
    WITH REPLACE, RECOVERY, STATS = 10;
    ALTER DATABASE [$DatabaseName] SET MULTI_USER;
END
ELSE
BEGIN
    RESTORE DATABASE [$DatabaseName]
    FROM DISK = N'$backupFileEscaped'
    WITH RECOVERY, STATS = 10;
END
"@

    $secondaryConnStr = "Server=$SecondaryServer;Database=master;Integrated Security=True;TrustServerCertificate=True;"
    $secondaryConn = New-Object System.Data.SqlClient.SqlConnection($secondaryConnStr)
    $secondaryConn.Open()
    $cmd2 = $secondaryConn.CreateCommand()
    $cmd2.CommandText = $restoreSql
    $cmd2.CommandTimeout = 600
    $cmd2.ExecuteNonQuery() | Out-Null
    $secondaryConn.Close()
    $secondaryConn.Dispose()
    Write-Log "  OK 从库还原成功" "Green"

    # 第三步：清理旧备份文件
    Write-Log "[3/3] 清理 $RetentionDays 天前的旧备份..." "Yellow"
    $oldFiles = Get-ChildItem -Path $BackupDir -Filter "$DatabaseName`_*.bak" -ErrorAction SilentlyContinue |
                Where-Object { $_.CreationTime -lt (Get-Date).AddDays(-$RetentionDays) }
    if ($oldFiles) {
        $oldCount = $oldFiles.Count
        $oldFiles | Remove-Item -Force
        Write-Log "  OK 清理完成（删除 $oldCount 个旧文件）" "Green"
    } else {
        Write-Log "  OK 无需清理" "Green"
    }

    $elapsed = (Get-Date) - $script:StartTime
    Write-Log ""
    Write-Log "============================================" "Cyan"
    Write-Log " 同步完成！耗时 $([math]::Round($elapsed.TotalSeconds, 1)) 秒" "Green"
    Write-Log "============================================" "Cyan"
}
catch {
    Write-Log ""
    Write-Log "============================================" "Red"
    Write-Log " 同步失败！" "Red"
    Write-Log "============================================" "Red"
    Write-Log "错误: $_" "Red"
    Write-Log ""
    Write-Log "常见原因：" "Yellow"
    Write-Log "  1. 从库正在被应用使用（故障切换期间）-> 等应用切回主库后再同步" "Yellow"
    Write-Log "  2. 备份目录权限不足 -> SQL Server 服务账户需要对 $BackupDir 有读写权限" "Yellow"
    Write-Log "  3. SQL Server 服务未启动 -> 检查主库和从库的 SQL Server 服务" "Yellow"
    Write-Log "  4. 跨机器同步 -> BackupDir 必须是两台机器都能访问的共享路径" "Yellow"
    Write-Log "  5. 网络不通 -> 检查主库和从库之间的网络连接和防火墙" "Yellow"
    exit 1
}
