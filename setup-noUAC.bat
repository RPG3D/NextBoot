@echo off
rem One-time setup: register NextBoot as an elevated Scheduled Task and
rem repoint the desktop shortcut to "schtasks /run" so future launches skip UAC.
rem Double-click this file and accept the single UAC prompt.
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator privileges (one-time only)...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-noUAC.ps1"
echo.
pause
