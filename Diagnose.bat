@echo off
REM Double-click this on the computer where the overlay is not working.
REM It writes overlay-diagnostic.txt to your Desktop.
title Now Playing overlay - diagnostics
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnose.ps1"
echo.
echo Press any key to close.
pause >nul
