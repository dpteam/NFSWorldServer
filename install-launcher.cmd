@echo off
rem ============================================================
rem  DPT.Server / NFS World - install the dptnfsw:// handler
rem  Run once. No admin needed (HKCU only).
rem ============================================================
setlocal
cd /d "%~dp0"
if not exist "bin\NFSWorldLauncher.exe" (
  echo Server is not built yet. Run build.cmd first.
  pause
  exit /b 1
)
bin\NFSWorldLauncher.exe --install
echo.
echo  Handler installed: dptnfsw://play?server=...
echo  Open the launcher page: http://localhost:44601/launcher/
echo.
pause
endlocal