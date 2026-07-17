# ============================================================
# 连接字符串加密工具
# 功能：使用 Windows DPAPI 加密数据库连接字符串
# 用法：.\Encrypt-ConnectionString.ps1 "Server=...;Password=..."
#
# 示例：
#   .\Encrypt-ConnectionString.ps1 "Server=192.168.1.100\SQLEXPRESS;Database=IsolationLeakageDb;User Id=sa;Password=Admin@123;"
# ============================================================

param(
    [Parameter(Mandatory = $true, HelpMessage = "要加密的连接字符串")]
    [string]$ConnectionString
)

# 检查是否在 Windows 环境下运行
if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -lt 6) {
    Write-Host " 错误：此脚本只能在 Windows 环境下运行" -ForegroundColor Red
    Write-Host ""
    Write-Host "原因：使用了 Windows 数据保护 API (DPAPI)" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  连接字符串加密工具" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "明文长度：$($ConnectionString.Length) 字符"

try {
    # 加载 System.Security 程序集
    Add-Type -AssemblyName System.Security

    # 转换为字节数组
    $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($ConnectionString)

    # 使用 DPAPI 加密（本地机器范围）
    $encryptedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
        $plainBytes,
        $null,  # 可选的额外熵（null 表示不使用）
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine
    )

    # 转换为 Base64 字符串
    $base64 = [Convert]::ToBase64String($encryptedBytes)
    $encrypted = "ENC:$base64"

    Write-Host "加密长度：$($encrypted.Length) 字符"
    Write-Host ""
    Write-Host "✅ 加密成功！" -ForegroundColor Green
    Write-Host ""
    Write-Host "加密后的连接字符串：" -ForegroundColor Yellow
    Write-Host $encrypted
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  配置示例" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "请将上面的字符串复制到 appsettings.json 中："
    Write-Host ""
    Write-Host "{"
    Write-Host "  `"ConnectionStrings`": {"
    Write-Host "    `"DefaultConnection`": `"$encrypted`""
    Write-Host "  }"
    Write-Host "}"
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  注意事项" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "⚠️  加密后的字符串只能在本机解密！" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  - 不要在 A 机器加密后复制到 B 机器使用"
    Write-Host "  - 如果需要在多台机器部署，每台机器都需要单独加密"
    Write-Host "  - 备份配置文件时，加密的字符串无法在其他机器恢复"
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "❌ 加密失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "可能的原因：" -ForegroundColor Yellow
    Write-Host "  1. 不在 Windows 环境下运行"
    Write-Host "  2. 没有 DataProtection API 权限"
    Write-Host "  3. 连接字符串格式错误"
    Write-Host ""
    Write-Host "详细信息：$($_.Exception.StackTrace)"
    exit 1
}
