@echo off
REM Double-click to start the Now Playing overlay server.
REM Keep this window open while streaming. Close it (or Ctrl+C) to stop.
title Now Playing Overlay
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0server.ps1" -Port 8787
echo.
echo Server stopped. Press any key to close.
pause >nul
