@echo off
chcp 65001 >nul
title 数据库连接字符串加密工具

echo.
echo ============================================================
echo   数据库连接字符串加密工具
echo ============================================================
echo.
echo 本工具用于加密数据库连接字符串，确保密码安全存储。
echo.
echo 用法：
echo   1. 双击运行此脚本
echo   2. 输入你的数据库连接字符串
echo   3. 复制加密后的字符串到 appsettings.json
echo.
echo ============================================================
echo.

:input
echo 请输入数据库连接字符串：
echo （例如：Server=192.168.1.100\SQLEXPRESS;Database=IsolationLeakageDb;User Id=sa;Password=Admin@123;）
echo.
set /p connectionString=连接字符串：

if "%connectionString%"=="" (
    echo.
    echo [错误] 连接字符串不能为空！
    echo.
    goto input
)

echo.
echo 正在加密...
echo.

powershell -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.Security; $plainBytes = [System.Text.Encoding]::UTF8.GetBytes('%connectionString%'); $encryptedBytes = [System.Security.Cryptography.ProtectedData]::Protect($plainBytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine); $base64 = [Convert]::ToBase64String($encryptedBytes); Write-Host 'ENC:'$base64"

echo.
echo ============================================================
echo   加密完成！
echo ============================================================
echo.
echo 请将上面输出的加密字符串（以 ENC: 开头）复制到 appsettings.json 中：
echo.
echo {
echo   "ConnectionStrings": {
echo     "DefaultConnection": "ENC:上面输出的字符串"
echo   }
echo }
echo.
echo ============================================================
echo.
echo 注意事项：
echo   - 加密后的字符串只能在本机解密
echo   - 不要在 A 机器加密后复制到 B 机器使用
echo   - 如果需要在多台机器部署，每台机器都需要单独加密
echo.

pause
