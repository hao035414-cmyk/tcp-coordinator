@echo off
setlocal
set "ROOT=%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [x] csc.exe not found. Install .NET Framework 4.x first.
    exit /b 1
)

if not exist "%ROOT%dist" mkdir "%ROOT%dist"

echo [1/2] building desktop app ...
"%CSC%" /nologo /target:winexe /main:TcpCoordinatorApp.Program /platform:anycpu ^
    "/win32icon:%ROOT%assets\app.ico" "/out:%ROOT%dist\TcpCoordinatorApp.exe" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    "%ROOT%src\CoordinatorApp.cs" "%ROOT%src\TcpCoordinator.cs"
if errorlevel 1 goto :failed

echo [2/2] building console tool ...
"%CSC%" /nologo /target:exe /platform:anycpu ^
    "/out:%ROOT%dist\TcpCoordinator.exe" ^
    "%ROOT%src\TcpCoordinator.cs"
if errorlevel 1 goto :failed

echo.
echo Done.
echo   dist\TcpCoordinatorApp.exe  - desktop app, double-click to run
echo   dist\TcpCoordinator.exe     - console server / client
exit /b 0

:failed
echo.
echo [x] build failed.
exit /b 1
