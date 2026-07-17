# ============================================================
# 数据库服务器一键部署脚本
# 功能：配置 SQL Server 远程访问、创建数据库、创建登录账户、
#       配置防火墙、设置定时同步任务
# 用法：在数据库服务器上，以管理员身份运行此脚本
# ============================================================

#Requires -RunAsAdministrator

param(
    # SQL Server 实例名（安装时的实例名）
    [string]$SqlInstance = "SQLEXPRESS",

    # 要创建的 SQL 登录用户名
    [string]$AppUser = "appuser",

    # 要创建的 SQL 登录密码（不传则交互式输入）
    [string]$AppPassword = "",

    # 数据库名称
    [string]$DatabaseName = "IsolationLeakageDb",

    # 从库服务器地址（留空表示不配置主从同步）
    [string]$SecondaryServer = "",

    # 同步间隔（小时），默认 4
    [int]$SyncIntervalHours = 4
)

$ErrorActionPreference = "Stop"
$needRestart = $false
$testConnStr = $null  # 会在第 1 步中根据情况设置

# ============================================================
# 工具函数
# ============================================================

function Write-Step {
    param([string]$Step, [string]$Message)
    Write-Host ""
    Write-Host "  [$Step] $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    OK $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "    !! $Message" -ForegroundColor Yellow
}

function Write-Err {
    param([string]$Message)
    Write-Host "    XX $Message" -ForegroundColor Red
}

function Get-ServiceName {
    param([string]$Instance)
    if ($Instance -eq "MSSQLSERVER") {
        return "MSSQLSERVER"
    }
    return "MSSQL`$$Instance"
}

# ============================================================
# 开始
# ============================================================

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  智能安全壳隔离阀泄漏率测量系统 - 数据库服务器部署" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  SQL Server 实例: $SqlInstance"
Write-Host "  数据库名称:      $DatabaseName"
Write-Host "  应用账户:        $AppUser"
Write-Host ""

# ============================================================
# 第 1 步：检查 SQL Server 是否安装，未安装则自动下载安装
# ============================================================

Write-Step "1/8" "检查 SQL Server 安装状态..."

$serviceName = Get-ServiceName $SqlInstance
$sqlService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($null -eq $sqlService) {
    Write-Warn "未找到 SQL Server 实例: $SqlInstance"
    Write-Host ""
    Write-Host "  是否自动下载并安装 SQL Server Express？" -ForegroundColor Yellow
    Write-Host "  （约 400MB，需要联网）" -ForegroundColor Gray
    Write-Host ""
    $install = Read-Host "  输入 Y 自动安装，N 退出后手动安装 (Y/n)"

    if ($install -eq 'n' -or $install -eq 'N') {
        Write-Host ""
        Write-Host "  请手动安装 SQL Server Express：" -ForegroundColor Yellow
        Write-Host "  1. 下载: https://www.microsoft.com/sql-server/sql-server-downloads" -ForegroundColor Yellow
        Write-Host "  2. 安装时选择「基本」安装类型" -ForegroundColor Yellow
        Write-Host "  3. 实例名填写: $SqlInstance" -ForegroundColor Yellow
        Write-Host "  4. 身份验证选「混合模式」，设置 sa 密码" -ForegroundColor Yellow
        Write-Host "  5. 安装完成后重新运行此脚本" -ForegroundColor Yellow
        Write-Host ""
        Read-Host "按回车退出"
        exit 1
    }

    # ===== 自动下载安装 SQL Server Express =====
    Write-Host ""
    Write-Host "  [1/3] 下载 SQL Server Express 安装程序..." -ForegroundColor Cyan

    # SQL Server 2022 Express 安装程序（直接链接，非重定向）
    $downloadUrl = "https://download.microsoft.com/download/3/8/d/38db09da-9331-4726-a64a-c2237f127b28/SQL2022-SSEI-Expr.exe"
    $installerPath = Join-Path $env:TEMP "SQLServerExpressSetup.exe"

    # 如果已经下载过，直接复用
    if ((Test-Path $installerPath) -and (Get-Item $installerPath).Length -gt 1MB) {
        Write-Ok "检测到已有安装程序: $installerPath"
    } else {
        try {
            Write-Host "    正在下载（约 150MB），请稍候..." -ForegroundColor Gray
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

            # 优先使用 Invoke-WebRequest（能正确处理重定向）
            try {
                Invoke-WebRequest -Uri $downloadUrl -OutFile $installerPath -UseBasicParsing -TimeoutSec 600
            } catch {
                # Invoke-WebRequest 失败，回退到 WebClient
                Write-Host "    切换到备用下载方式..." -ForegroundColor Gray
                $webClient = New-Object System.Net.WebClient
                $webClient.DownloadFile($downloadUrl, $installerPath)
            }

            # 验证下载结果
            if (-not (Test-Path $installerPath) -or (Get-Item $installerPath).Length -lt 1MB) {
                throw "下载后文件不存在或文件大小异常"
            }

            $fileSize = [math]::Round((Get-Item $installerPath).Length / 1MB, 1)
            Write-Ok "下载完成: $installerPath ($fileSize MB)"
        }
        catch {
            Write-Err "下载失败: $_"
            Write-Host ""
            Write-Host "  请手动下载安装程序：" -ForegroundColor Yellow
            Write-Host "  https://www.microsoft.com/sql-server/sql-server-downloads" -ForegroundColor Yellow
            Write-Host "  下载后放到: $installerPath" -ForegroundColor Yellow
            Write-Host "  然后重新运行此脚本" -ForegroundColor Yellow
            Read-Host "按回车退出"
            exit 1
        }
    }

    # ===== 运行安装程序 =====
    Write-Host ""
    Write-Host "  [2/3] 正在启动 SQL Server 安装程序..." -ForegroundColor Cyan
    Write-Host "    安装程序窗口已弹出，请按以下步骤操作：" -ForegroundColor Yellow
    Write-Host "    1. 选择「新建 SQL Server 独立安装」" -ForegroundColor Yellow
    Write-Host "    2. 实例名填写: $SqlInstance" -ForegroundColor Yellow
    Write-Host "    3. 身份验证模式选「混合模式」" -ForegroundColor Yellow
    Write-Host "    4. 设置 sa 密码（记下来，后面要用）" -ForegroundColor Yellow
    Write-Host "    5. 其他保持默认，一直下一步直到完成" -ForegroundColor Yellow
    Write-Host ""

    # 直接启动安装程序 GUI，让用户按向导操作
    # 不使用 /ACTION 参数，因为那个需要配置文件才能静默安装
    Start-Process -FilePath $installerPath -Wait

    # 清理安装程序
    Remove-Item $installerPath -Force -ErrorAction SilentlyContinue

    # ===== 等待服务启动 =====
    Write-Host ""
    Write-Host "  [3/3] 等待 SQL Server 服务启动..." -ForegroundColor Cyan

    $maxWait = 60
    $waited = 0
    while ($waited -lt $maxWait) {
        $sqlService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $sqlService -and $sqlService.Status -eq "Running") {
            break
        }
        Start-Sleep -Seconds 2
        $waited += 2
        Write-Host "`r    等待中... ($waited/$maxWait 秒)" -NoNewline -ForegroundColor Gray
    }
    Write-Host ""

    $sqlService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $sqlService -or $sqlService.Status -ne "Running") {
        Write-Err "SQL Server 服务未启动，请手动检查"
        Read-Host "按回车退出"
        exit 1
    }

    Write-Ok "SQL Server ($SqlInstance) 已安装并启动"

    # 安装后需要重新获取 sa 密码（因为安装时用户自己设的）
    Write-Host ""
    Write-Host "  安装时你设置的 sa 密码是什么？" -ForegroundColor Yellow
    Write-Host "  （脚本需要用 sa 密码来创建 appuser 账户）" -ForegroundColor Gray
    $saPassword = Read-Host "  sa 密码" -AsSecureString
    if ($saPassword.Length -eq 0) {
        Write-Err "sa 密码不能为空"
        Read-Host "按回车退出"
        exit 1
    }
    $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($saPassword)
    $script:SaPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)

    # 更新测试连接字符串（安装后可能需要用 sa 账户连接）
    $testConnStr = "Server=.\$SqlInstance;Database=master;User Id=sa;Password=$($script:SaPassword);TrustServerCertificate=True;"

    # 测试 sa 连接
    try {
        $conn = New-Object System.Data.SqlClient.SqlConnection($testConnStr)
        $conn.Open()
        $conn.Close()
        $conn.Dispose()
        Write-Ok "sa 账户连接成功"
    } catch {
        Write-Err "sa 账户连接失败: $_"
        Write-Host "  请确认密码正确，且 SQL Server 已启用混合认证模式" -ForegroundColor Yellow
        Read-Host "按回车退出"
        exit 1
    }
}
else {
    if ($sqlService.Status -ne "Running") {
        Write-Warn "SQL Server 服务未运行，正在启动..."
        Start-Service $serviceName
        Start-Sleep -Seconds 3
    }
    Write-Ok "SQL Server ($SqlInstance) 已安装且正在运行"
}

# ============================================================
# 第 2 步：测试当前连接
# ============================================================

Write-Step "2/8" "测试数据库连接..."

$serverAddress = ".\$SqlInstance"

# 如果第 1 步刚安装了 SQL Server，testConnStr 已经用 sa 设好了；
# 否则用 Windows Auth（本地管理员连本地 SQL Server 一般没问题）
if (-not $testConnStr) {
    $testConnStr = "Server=$serverAddress;Database=master;Integrated Security=True;TrustServerCertificate=True;"
}

try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($testConnStr)
    $conn.Open()
    $conn.Close()
    $conn.Dispose()
    Write-Ok "SQL Server 连接成功"
}
catch {
    Write-Err "无法连接 SQL Server: $_"
    Write-Host "  请确认 SQL Server 服务正在运行" -ForegroundColor Yellow
    Read-Host "按回车退出"
    exit 1
}

# ============================================================
# 第 3 步：启用 TCP/IP 协议并设置固定端口
# ============================================================

Write-Step "3/8" "启用 TCP/IP 协议并设置固定端口 1433..."

try {
    # 尝试通过 SMO WMI 配置
    [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.SqlServer.Smo") | Out-Null
    [System.Reflection.Assembly]::LoadWithPartialName("Microsoft.SqlServer.SqlWmiManagement") | Out-Null

    $mc = New-Object Microsoft.SqlServer.Management.Smo.Wmi.ManagedComputer
    $instance = $mc.ServerInstances[$SqlInstance]
    $tcp = $instance.ServerProtocols["Tcp"]

    if ($tcp) {
        # 启用 TCP/IP
        if (-not $tcp.IsEnabled) {
            $tcp.IsEnabled = $true
            $tcp.Alter()
            Write-Ok "TCP/IP 协议已启用"
        } else {
            Write-Ok "TCP/IP 协议已启用"
        }

        # 设置 IPAll 的端口为 1433（固定端口，不用动态端口）
        $ipAll = $tcp.IPAddresses | Where-Object { $_.Name -eq "IPAll" }
        if ($ipAll) {
            $needChange = $false

            # 清空动态端口（设为空 = 不用动态端口）
            foreach ($prop in $ipAll.IPAddressProperties) {
                if ($prop.Name -eq "TcpDynamicPorts" -and $prop.Value -ne "") {
                    $prop.Value = ""
                    $needChange = $true
                }
                if ($prop.Name -eq "TcpPort" -and $prop.Value -ne "1433") {
                    $prop.Value = "1433"
                    $needChange = $true
                }
            }

            if ($needChange) {
                $ipAll.Alter()
                Write-Ok "TCP 端口已设为固定 1433"
                $needRestart = $true
            } else {
                Write-Ok "TCP 端口已是 1433"
            }
        }
    } else {
        Write-Warn "未找到 TCP 协议，请手动在 SQL Server 配置管理器中设置:"
        Write-Warn "  SQL Server 配置管理器 → TCP/IP → 属性 → IP 地址 → IPAll → TCP 端口 = 1433"
    }
}
catch {
    Write-Warn "自动配置 TCP/IP 失败: $_"
    Write-Warn "请手动在 SQL Server 配置管理器中操作:"
    Write-Warn "  1. 启用 TCP/IP 协议"
    Write-Warn "  2. TCP/IP 属性 → IP 地址 → IPAll → TCP 端口 = 1433"
    Write-Warn "  3. 清空 TCP 动态端口"
}

# ============================================================
# 第 3.5 步：启动 SQL Server Browser 服务
# ============================================================

Write-Step "3.5/8" "配置 SQL Server Browser 服务..."

try {
    $browserService = Get-Service -Name "SQLBrowser" -ErrorAction SilentlyContinue
    if ($null -eq $browserService) {
        Write-Warn "SQL Server Browser 服务未安装"
        Write-Warn "客户端将通过固定端口 1433 连接，Browser 服务非必须"
    } else {
        # 设为自动启动
        if ($browserService.StartType -eq "Disabled" -or $browserService.StartType -eq "Manual") {
            Set-Service -Name "SQLBrowser" -StartupType Automatic
            Write-Ok "SQL Browser 已设为自动启动"
        } else {
            Write-Ok "SQL Browser 已为自动启动"
        }

        # 启动服务
        if ($browserService.Status -ne "Running") {
            Start-Service "SQLBrowser"
            Write-Ok "SQL Browser 服务已启动"
        } else {
            Write-Ok "SQL Browser 服务正在运行"
        }

        # 防火墙放行 SQL Browser（UDP 1434）
        $browserRuleName = "SQL Server Browser - UDP 1434"
        $browserRule = Get-NetFirewallRule -DisplayName $browserRuleName -ErrorAction SilentlyContinue
        if ($null -eq $browserRule) {
            New-NetFirewallRule -DisplayName $browserRuleName `
                -Direction Inbound -Protocol UDP -LocalPort 1434 `
                -Action Allow -Profile Any -Enabled True | Out-Null
            Write-Ok "防火墙已放行 SQL Browser (UDP 1434)"
        } else {
            Write-Ok "SQL Browser 防火墙规则已存在"
        }
    }
}
catch {
    Write-Warn "配置 SQL Browser 时出错: $_"
}

# 如果需要，重启 SQL Server 使端口配置生效
if ($needRestart) {
    Write-Warn "端口配置已更改，需要重启 SQL Server 服务..."
    Restart-Service $serviceName -Force
    Start-Sleep -Seconds 5
    Write-Ok "SQL Server 服务已重启"
}

# ============================================================
# 第 4 步：启用混合认证模式
# ============================================================

Write-Step "4/8" "检查身份验证模式..."

try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($testConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT SERVERPROPERTY('IsIntegratedSecurityOnly')"
    $result = $cmd.ExecuteScalar()
    $conn.Close()
    $conn.Dispose()

    if ($result -eq 1) {
        Write-Warn "当前为 Windows 身份验证模式，需要切换到混合模式"
        Write-Host ""
        Write-Host "  请手动操作：" -ForegroundColor Yellow
        Write-Host "  1. 打开 SSMS，用 Windows 认证连接" -ForegroundColor Yellow
        Write-Host "  2. 右键服务器 → 属性 → 安全性" -ForegroundColor Yellow
        Write-Host "  3. 选择「SQL Server 和 Windows 身份验证模式」" -ForegroundColor Yellow
        Write-Host "  4. 确定后重启 SQL Server 服务" -ForegroundColor Yellow
        Write-Host "  5. 重新运行此脚本" -ForegroundColor Yellow
        Write-Host ""
        $switchNow = Read-Host "  是否现在通过 SQL 命令切换？(y/N)"
        if ($switchNow -eq 'y' -or $switchNow -eq 'Y') {
            $conn2 = New-Object System.Data.SqlClient.SqlConnection($testConnStr)
            $conn2.Open()
            $cmd2 = $conn2.CreateCommand()
            $cmd2.CommandText = "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; EXEC sp_configure 'mixed mode', 1; RECONFIGURE;"
            $cmd2.ExecuteNonQuery() | Out-Null
            $conn2.Close()
            $conn2.Dispose()
            Write-Ok "混合模式已启用，需要重启 SQL Server 服务"
            Restart-Service $serviceName -Force
            Start-Sleep -Seconds 5
            Write-Ok "SQL Server 服务已重启"
        }
    } else {
        Write-Ok "已为混合认证模式"
    }
}
catch {
    Write-Warn "检查认证模式时出错: $_"
}

# ============================================================
# 第 5 步：创建数据库和登录账户
# ============================================================

Write-Step "5/8" "创建数据库和登录账户..."

# 获取密码
if ([string]::IsNullOrWhiteSpace($AppPassword)) {
    $securePwd = Read-Host "请输入 $AppUser 的密码（至少 8 位）" -AsSecureString
    if ($securePwd.Length -lt 8) {
        Write-Err "密码长度不能少于 8 位"
        Read-Host "按回车退出"
        exit 1
    }
    $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePwd)
    $AppPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)
}

try {
    $conn = New-Object System.Data.SqlClient.SqlConnection($testConnStr)
    $conn.Open()

    # 创建数据库（如果不存在）
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "IF DB_ID('$DatabaseName') IS NULL CREATE DATABASE [$DatabaseName]"
    $cmd.ExecuteNonQuery() | Out-Null
    Write-Ok "数据库 [$DatabaseName] 已就绪"

    # 创建登录账户（如果不存在）
    $cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = '$AppUser')
BEGIN
    CREATE LOGIN [$AppUser] WITH PASSWORD = N'$AppPassword',
        CHECK_POLICY = OFF, DEFAULT_DATABASE = [$DatabaseName];
END
ELSE
BEGIN
    ALTER LOGIN [$AppUser] WITH PASSWORD = N'$AppPassword', CHECK_POLICY = OFF;
END
"@
    $cmd.ExecuteNonQuery() | Out-Null
    Write-Ok "登录账户 [$AppUser] 已创建/更新"

    # 在数据库中创建用户并授权
    $cmd.CommandText = "USE [$DatabaseName]"
    $cmd.ExecuteNonQuery() | Out-Null

    $cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = '$AppUser')
BEGIN
    CREATE USER [$AppUser] FOR LOGIN [$AppUser];
END
"@
    $cmd.ExecuteNonQuery() | Out-Null

    $cmd.CommandText = "ALTER ROLE db_owner ADD MEMBER [$AppUser]"
    $cmd.ExecuteNonQuery() | Out-Null
    Write-Ok "用户 [$AppUser] 已获得数据库读写权限"

    $conn.Close()
    $conn.Dispose()
}
catch {
    Write-Err "创建数据库/账户失败: $_"
    Read-Host "按回车退出"
    exit 1
}

# ============================================================
# 第 6 步：配置 Windows 防火墙
# ============================================================

Write-Step "6/8" "配置 Windows 防火墙..."

try {
    $ruleName = "SQL Server ($SqlInstance) - TCP 1433"
    $existingRule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue

    if ($null -eq $existingRule) {
        New-NetFirewallRule -DisplayName $ruleName `
            -Direction Inbound -Protocol TCP -LocalPort 1433 `
            -Action Allow -Profile Any -Enabled True | Out-Null
        Write-Ok "防火墙规则已添加: 允许 TCP 1433 端口入站"
    } else {
        if ($existingRule.Enabled -ne "True") {
            Set-NetFirewallRule -DisplayName $ruleName -Enabled True
            Write-Ok "防火墙规则已启用"
        } else {
            Write-Ok "防火墙规则已存在且已启用"
        }
    }
    # 注意：SQL Browser (UDP 1434) 的防火墙规则已在步骤 3.5 中配置
}
catch {
    Write-Warn "配置防火墙时出错: $_"
    Write-Warn "请手动在 Windows 防火墙中放行 TCP 1433 端口"
}

# ============================================================
# 第 7 步：配置主从同步（可选）
# ============================================================

Write-Step "7/8" "配置主从同步..."

if (-not [string]::IsNullOrWhiteSpace($SecondaryServer)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $syncScript = Join-Path $scriptDir "SyncDatabase.ps1"

    if (Test-Path $syncScript) {
        $taskName = "IsolationLeakage-DbSync"
        $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

        if ($null -ne $existingTask) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        }

        $action = New-ScheduledTaskAction -Execute "PowerShell.exe" `
            -Argument "-ExecutionPolicy Bypass -File `"$syncScript`" -PrimaryServer `".\$SqlInstance`" -SecondaryServer `"$SecondaryServer`" -DatabaseName `"$DatabaseName`""

        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours $SyncIntervalHours)

        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Description "数据库主从同步（每 $SyncIntervalHours 小时）" -RunLevel Highest | Out-Null

        Write-Ok "定时同步任务已创建: 每 $SyncIntervalHours 小时同步到 $SecondaryServer"
    } else {
        Write-Warn "同步脚本不存在: $syncScript"
    }
} else {
    Write-Host "    跳过（未指定从库地址）" -ForegroundColor Gray
    Write-Host "    如需配置主从同步，加参数: -SecondaryServer `"192.168.1.101\SQLEXPRESS`"" -ForegroundColor Gray
}

# ============================================================
# 第 8 步：获取本机 IP 并显示连接信息
# ============================================================

Write-Step "8/8" "部署完成！"

$ipAddresses = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.InterfaceAlias -notmatch "Loopback" -and $_.IPAddress -ne "127.0.0.1" } |
    Select-Object -ExpandProperty IPAddress

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  数据库服务器部署完成！" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  客户端连接信息：" -ForegroundColor Cyan
Write-Host ""
Write-Host "  服务器地址: 以下任一 IP 均可" -ForegroundColor White

foreach ($ip in $ipAddresses) {
    Write-Host "    - $ip\$SqlInstance" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  数据库名称:  $DatabaseName" -ForegroundColor White
Write-Host "  用户名:      $AppUser" -ForegroundColor White
Write-Host "  密码:        （你刚才设置的密码）" -ForegroundColor White
Write-Host ""
Write-Host "  完整连接字符串示例：" -ForegroundColor Cyan
if ($ipAddresses.Count -gt 0) {
    Write-Host "    Server=$($ipAddresses[0])\$SqlInstance;Database=$DatabaseName;User Id=$AppUser;Password=你的密码;TrustServerCertificate=True;" -ForegroundColor Yellow
}
Write-Host ""

Write-Host "  接下来请在每台客户端上：" -ForegroundColor Cyan
Write-Host "  1. 运行「部署-客户端.bat」配置远程连接" -ForegroundColor White
Write-Host "  2. 或手动修改 appsettings.json 中的连接字符串" -ForegroundColor White
Write-Host ""

Read-Host "按回车退出"
