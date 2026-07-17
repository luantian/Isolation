# ============================================================
# 连接字符串解密验证工具
# 功能：验证加密的连接字符串是否可以正常解密
# 用法：.\Decrypt-ConnectionString.ps1 "ENC:AQAAANCMnd8BFmERjAoAwB..."
# ============================================================

param(
    [Parameter(Mandatory = $true, HelpMessage = "要解密的连接字符串（以 ENC: 开头）")]
    [string]$EncryptedConnectionString
)

# 检查是否以 ENC: 开头
if (-not $EncryptedConnectionString.StartsWith("ENC:")) {
    Write-Host ""
    Write-Host " 错误：连接字符串必须以 ENC: 开头" -ForegroundColor Red
    Write-Host ""
    Write-Host "用法：.\Decrypt-ConnectionString.ps1 \"ENC:AQAAANCMnd8BFmERjAoAwB...\""
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  连接字符串解密验证工具" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "加密长度：$($EncryptedConnectionString.Length) 字符"

try {
    # 加载 System.Security 程序集
    Add-Type -AssemblyName System.Security

    # 移除 ENC: 前缀
    $base64 = $EncryptedConnectionString.Substring(4)

    # 从 Base64 转换为字节数组
    $encryptedBytes = [Convert]::FromBase64String($base64)

    # 使用 DPAPI 解密（本地机器范围）
    $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $encryptedBytes,
        $null,
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine
    )

    # 转换为字符串
    $decrypted = [System.Text.Encoding]::UTF8.GetString($plainBytes)

    Write-Host "解密长度：$($decrypted.Length) 字符"
    Write-Host ""
    Write-Host "✅ 解密成功！" -ForegroundColor Green
    Write-Host ""
    Write-Host "解密后的连接字符串：" -ForegroundColor Yellow
    Write-Host $decrypted
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  连接字符串分析" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""

    # 解析连接字符串
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($decrypted)

    Write-Host "服务器：$($builder.DataSource)"
    Write-Host "数据库：$($builder.InitialCatalog)"
    Write-Host "用户名：$($builder.UserID)"

    if ($builder.UserID) {
        Write-Host "密码：$('*' * $builder.Password.Length) (已隐藏)"
    }
    else {
        Write-Host "认证：Windows 身份验证"
    }

    Write-Host ""
    Write-Host "✅ 连接字符串格式正确，可以正常使用" -ForegroundColor Green
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "❌ 解密失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "可能的原因：" -ForegroundColor Yellow
    Write-Host "  1. 此连接字符串是在其他机器上加密的"
    Write-Host "  2. 当前用户没有解密权限"
    Write-Host "  3. 连接字符串已损坏或被篡改"
    Write-Host ""
    Write-Host "详细信息：$($_.Exception.StackTrace)"
    exit 1
}
