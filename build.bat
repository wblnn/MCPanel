@echo off
chcp 65001 >nul 2>&1
echo ============================================
echo   MC Panel 打包工具
echo ============================================
echo.

echo [1/3] 清理旧的发布文件...
if exist "publish\win-x64" rd /s /q "publish\win-x64"

echo [2/3] 正在编译并打包（Windows x64 单文件可执行程序）...
dotnet publish MCPanel.Manager -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "publish\win-x64"

if %ERRORLEVEL% neq 0 (
    echo.
    echo [错误] 打包失败！请检查编译错误。
    pause
    exit /b 1
)

echo.
echo [3/3] 打包完成！
echo.
echo 输出目录：publish\win-x64
echo 可执行文件：publish\win-x64\MCPanel.exe
echo.
echo 运行方式：
echo   1. 双击 MCPanel.exe（自动打开浏览器）
echo   2. 访问 http://localhost:5162
echo.
pause
