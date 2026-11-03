@echo off
setlocal
rem  DPT.Server / NFS World - run
cd /d "%~dp0"
if not exist "bin\NFSWorldServer.exe" (
  echo Server is not built yet. Run build.cmd first.
  pause
  exit /b 1
)
bin\NFSWorldServer.exe %*
if errorlevel 1 pause
endlocal