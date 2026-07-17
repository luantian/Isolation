@echo off
chcp 65001 >nul 2>&1
title Isolation Leakage App - Deploy
color 0B

:MENU
cls
echo.
echo ============================================================
echo   Isolation Leakage App - One-Click Deploy
echo ============================================================
echo.
echo   [1] Database Server Setup (run once)
echo       Install SQL Server, configure remote access,
echo       create database and user, setup firewall
echo.
echo   [2] Client Setup (run on each client)
echo       Configure app to connect to remote database
echo.
echo   [3] Uninstall SQL Server (cleanup)
echo       Remove SQL Server and all data
echo.
echo   [4] Exit
echo.
echo ============================================================
echo.
set /p "choice=  Enter 1/2/3/4: "

if "%choice%"=="1" goto SERVER
if "%choice%"=="2" goto CLIENT
if "%choice%"=="3" goto UNINSTALL
if "%choice%"=="4" exit
echo.
echo   Invalid input, please try again
timeout /t 2 >nul
goto MENU

:SERVER
echo.
echo ============================================================
echo   Database Server Setup
echo ============================================================
echo.
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo   Requesting admin privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\" 1' -Verb RunAs"
    exit /b
)
if "%1"=="1" (
    powershell -ExecutionPolicy Bypass -File "%~dp0Setup-DatabaseServer.ps1"
) else (
    powershell -ExecutionPolicy Bypass -File "%~dp0Setup-DatabaseServer.ps1"
)
echo.
pause
goto MENU

:CLIENT
echo.
echo ============================================================
echo   Client Setup
echo ============================================================
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0Setup-Client.ps1"
echo.
pause
goto MENU

:UNINSTALL
echo.
echo ============================================================
echo   Uninstall SQL Server
echo ============================================================
echo.
echo   WARNING: This will remove SQL Server and ALL data!
echo.
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo   Requesting admin privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\" 3' -Verb RunAs"
    exit /b
)
if "%1"=="3" (
    powershell -ExecutionPolicy Bypass -File "%~dp0Uninstall-SQLServer.ps1"
) else (
    powershell -ExecutionPolicy Bypass -File "%~dp0Uninstall-SQLServer.ps1"
)
echo.
pause
goto MENU
