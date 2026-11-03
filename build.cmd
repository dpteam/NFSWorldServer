@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem  DPT.Server / NFS World - build script (no NuGet, no MSBuild)
rem  Targets .NET Framework 4.0 so it still runs on Windows XP SP3.
rem ============================================================

set "ROOT=%~dp0"
set "SRV=%ROOT%src\Dpt.NfsWorld.Server"
set "LCH=%ROOT%src\Dpt.NfsWorld.Launcher"
set "OUT=%ROOT%bin"

if not exist "%OUT%" mkdir "%OUT%"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe from .NET Framework 4.x was not found.
  echo         Install .NET Framework 4.0 or newer and try again.
  exit /b 1
)

echo [1/6] collecting server sources...
if exist "%OUT%\sources.rsp" del "%OUT%\sources.rsp"
for /r "%SRV%" %%f in (*.cs) do echo "%%f">>"%OUT%\sources.rsp"

echo [2/6] compiling server with %CSC%
"%CSC%" /nologo /target:exe /platform:anycpu /langversion:4 /optimize+ /debug:pdbonly ^
  /warn:3 /define:TRACE ^
  /out:"%OUT%\NFSWorldServer.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll ^
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  @"%OUT%\sources.rsp"

if errorlevel 1 (
  echo.
  echo [ERROR] server build failed.
  exit /b 1
)

echo [3/6] collecting launcher sources...
if exist "%OUT%\launcher.rsp" del "%OUT%\launcher.rsp"
for /r "%LCH%" %%f in (*.cs) do echo "%%f">>"%OUT%\launcher.rsp"

echo [4/6] compiling launcher (MSHTML / WinForms WebBrowser)
"%CSC%" /nologo /target:winexe /platform:anycpu /langversion:4 /optimize+ /debug:pdbonly ^
  /warn:3 /define:TRACE ^
  /out:"%OUT%\NFSWorldLauncher.exe" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll ^
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /resource:"%LCH%\Assets\ui.html",LauncherUi.html ^
  /resource:"%LCH%\Assets\theme.css",LauncherTheme.css ^
  /resource:"%LCH%\Assets\app.js",LauncherApp.js ^
  @"%OUT%\launcher.rsp"

if errorlevel 1 (
  echo.
  echo [ERROR] launcher build failed.
  exit /b 1
)

echo [5/6] copying web assets and data...
if exist "%OUT%\public" rmdir /s /q "%OUT%\public"
xcopy "%ROOT%public" "%OUT%\public\" /E /I /Y /Q >nul
xcopy "%ROOT%config.example.ini" "%OUT%\" /Y /Q >nul

if not exist "%OUT%\data" mkdir "%OUT%\data"
if not exist "%OUT%\data\profiles" mkdir "%OUT%\data\profiles"
if exist "%ROOT%data\profiles" xcopy "%ROOT%data\profiles" "%OUT%\data\profiles\" /E /I /Y /Q >nul
if not exist "%OUT%\logs" mkdir "%OUT%\logs"

if not exist "%OUT%\data\certs" mkdir "%OUT%\data\certs"
if exist "%ROOT%data\certs\xmpp.pfx" xcopy "%ROOT%data\certs\xmpp.pfx" "%OUT%\data\certs\" /D /Y /Q >nul
if not exist "%OUT%\data\certs\xmpp.pfx" (
  echo       no xmpp.pfx in data\certs - run: powershell -File tools\make-cert.ps1
)

echo [6/6] done.
echo.
echo   server  : %OUT%\NFSWorldServer.exe
echo   launcher: %OUT%\NFSWorldLauncher.exe
echo   run     : run.cmd
echo   engine  : http://localhost:44600/Engine.svc
echo   public  : http://localhost:44601/   admin: http://localhost:44602/  (bin\config.ini)
echo.
endlocal