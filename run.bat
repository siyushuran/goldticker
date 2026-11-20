@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "EXE=GoldTicker\bin\Release\net9.0-windows\GoldTicker.exe"

if not exist "%EXE%" (
    echo 首次运行，正在编译...
    dotnet build "GoldTicker\GoldTicker.csproj" -c Release
    if errorlevel 1 (
        echo.
        echo 编译失败，请确认已安装 .NET 9 SDK。
        pause
        exit /b 1
    )
)

start "" "%EXE%"
