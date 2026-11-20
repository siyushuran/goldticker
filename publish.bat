@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo 正在打包单文件 exe ...
dotnet publish "GoldTicker\GoldTicker.csproj" -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

if errorlevel 1 (
    echo.
    echo 打包失败。
    pause
    exit /b 1
)

echo.
echo 完成：%~dp0publish\GoldTicker.exe
echo 目标机器需安装 .NET 9 桌面运行时。
pause
