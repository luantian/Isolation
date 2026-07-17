# ============================================================
# 客户端一键配置脚本
# 功能：配置软件连接到远程数据库服务器
# 用法：在每台客户端机器上，双击运行此脚本
# ============================================================

param(
    # 数据库服务器地址（IP 或主机名）
    [string]$ServerAddress = "",

    # SQL Server 实例名
    [string]$SqlInstance = "SQLEXPRESS",

    # 数据库名称
    [string]$DatabaseName = "IsolationLeakageDb",

    # SQL 登录用户名
    [string]$AppUser = "appuser",

    # SQL 登录密码（不传则交互式输入）
    [string]$AppPassword = ""
)

$ErrorActionPreference = "Stop"

# ============================================================
# 开始
# ============================================================

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  智能安全壳隔离阀泄漏率测量系统 - 客户端配置" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# 第 1 步：获取配置信息
# ============================================================

if ([string]::IsNullOrWhiteSpace($ServerAddress)) {
    Write-Host "  请输入数据库服务器地址" -ForegroundColor Yellow
    Write-Host "  示例: 192.168.1.100 或 DBSERVER" -ForegroundColor Gray
    Write-Host ""
    $ServerAddress = Read-Host "  服务器 IP 地址"
}

if ([string]::IsNullOrWhiteSpace($ServerAddress)) {
    Write-Host "  错误: 必须输入服务器地址" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($AppPassword)) {
    $securePwd = Read-Host "  请输入 $AppUser 的密码" -AsSecureString
    $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd)
    $AppPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)
}

$fullServer = "$ServerAddress\$SqlInstance"
$connStr = "Server=$fullServer;Database=$DatabaseName;User Id=$AppUser;Password=$AppPassword;TrustServerCertificate=True;"

Write-Host ""
Write-Host "  配置信息:" -ForegroundColor Cyan
Write-Host "    服务器:   $fullServer"
Write-Host "    数据库:   $DatabaseName"
Write-Host "    用户:     $AppUser"
Write-Host ""

# ============================================================
# 第 2 步：测试连接
# ============================================================

Write-Host "  [1/3] 测试数据库连接..." -ForegroundColor Cyan

try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()

    # 测试能否查询
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT 1"
    $cmd.ExecuteScalar() | Out-Null

    $conn.Close()
    $conn.Dispose()

    Write-Host "    OK 连接成功!" -ForegroundColor Green
}
catch {
    Write-Host "    XX 连接失败: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "  请检查:" -ForegroundColor Yellow
    Write-Host "    1. 服务器 IP 是否正确" -ForegroundColor Yellow
    Write-Host "    2. SQL Server 是否已启动" -ForegroundColor Yellow
    Write-Host "    3. TCP/IP 协议是否已启用" -ForegroundColor Yellow
    Write-Host "    4. 防火墙是否已放行 1433 端口" -ForegroundColor Yellow
    Write-Host "    5. 用户名和密码是否正确" -ForegroundColor Yellow
    Write-Host ""
    $retry = Read-Host "  是否跳过测试继续配置？(y/N)"
    if ($retry -ne 'y' -and $retry -ne 'Y') {
        Read-Host "按回车退出"
        exit 1
    }
}

# ============================================================
# 第 3 步：查找软件安装位置
# ============================================================

Write-Host "  [2/3] 查找软件安装目录..." -ForegroundColor Cyan

# 先检查软件是否在运行
$runningProcess = Get-Process -Name "IsolationLeakage.App" -ErrorAction SilentlyContinue
if ($null -ne $runningProcess) {
    Write-Host "    !! 软件正在运行中" -ForegroundColor Yellow
    $stop = Read-Host "    是否关闭软件继续配置？(Y/n)"
    if ($stop -ne 'n' -and $stop -ne 'N') {
        $runningProcess | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "    OK 软件已关闭" -ForegroundColor Green
    } else {
        Write-Host "    请手动关闭软件后重新运行此脚本" -ForegroundColor Yellow
        Read-Host "按回车退出"
        exit 0
    }
}

$appSettingsPath = $null

# 常见安装路径
$searchPaths = @(
    "C:\Program Files\IsolationLeakageApp",
    "C:\Program Files (x86)\IsolationLeakageApp",
    "$PSScriptRoot\..\src\IsolationLeakage.App",
    "$PSScriptRoot\..\publish",
    $PSScriptRoot
)

foreach ($path in $searchPaths) {
    $candidate = Join-Path $path "appsettings.json"
    if (Test-Path $candidate) {
        $appSettingsPath = $candidate
        break
    }
}

# 如果没找到，让用户手动选择
if ($null -eq $appSettingsPath) {
    Write-Host "    未自动找到 appsettings.json" -ForegroundColor Yellow
    Write-Host "    请手动输入软件安装目录:" -ForegroundColor Yellow
    $manualPath = Read-Host "    路径"
    if (Test-Path $manualPath) {
        $candidate = Join-Path $manualPath "appsettings.json"
        if (Test-Path $candidate) {
            $appSettingsPath = $candidate
        }
    }
}

if ($null -eq $appSettingsPath) {
    Write-Host "    XX 找不到 appsettings.json，请确认软件已安装" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

Write-Host "    OK 找到: $appSettingsPath" -ForegroundColor Green

# ============================================================
# 第 4 步：写入配置
# ============================================================

Write-Host "  [3/3] 写入配置..." -ForegroundColor Cyan

try {
    # 读取现有配置
    $json = Get-Content $appSettingsPath -Raw -Encoding UTF8
    $config = $json | ConvertFrom-Json

    # 更新连接字符串
    if ($null -eq $config.ConnectionStrings) {
        $config | Add-Member -NotePropertyName "ConnectionStrings" -NotePropertyValue ([PSCustomObject]@{})
    }

    $config.ConnectionStrings | Add-Member -NotePropertyName "DefaultConnection" -NotePropertyValue $connStr -Force

    # 清空从库连接（客户端不需要配从库）
    if ($config.ConnectionStrings.PSObject.Properties["SecondaryConnection"]) {
        $config.ConnectionStrings.SecondaryConnection = ""
    }

    # 禁用故障切换（客户端不需要，由数据库服务器决定）
    if ($null -eq $config.Failover) {
        $config | Add-Member -NotePropertyName "Failover" -NotePropertyValue ([PSCustomObject]@{
            Enabled = $false
        })
    }

    # 写回文件
    $newJson = $config | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($appSettingsPath, $newJson, [System.Text.Encoding]::UTF8)

    Write-Host "    OK 配置已保存" -ForegroundColor Green
}
catch {
    Write-Host "    XX 写入配置失败: $_" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

# ============================================================
# 完成
# ============================================================

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  客户端配置完成!" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  配置文件: $appSettingsPath" -ForegroundColor White
Write-Host "  服务器:   $fullServer" -ForegroundColor White
Write-Host "  数据库:   $DatabaseName" -ForegroundColor White
Write-Host ""
Write-Host "  现在可以启动软件了。" -ForegroundColor Cyan
Write-Host "  如果连接失败，请检查:" -ForegroundColor Yellow
Write-Host "    - 本机能 ping 通 $ServerAddress" -ForegroundColor Yellow
Write-Host "    - 数据库服务器防火墙已放行 1433 端口" -ForegroundColor Yellow
Write-Host "    - 用户名密码正确" -ForegroundColor Yellow
Write-Host ""

Read-Host "按回车退出"
