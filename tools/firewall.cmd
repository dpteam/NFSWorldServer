@echo off
rem  DPT.Server / NFS World - open the Windows Firewall ports (UAC prompt).
rem  Forward the same ports on the router to this PC's LAN address.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firewall.ps1" %*