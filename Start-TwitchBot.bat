@echo off
REM Double-click to start the Twitch !song bot.
REM Start-Overlay.bat should already be running (the bot reads its data).
title Twitch !song bot
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0twitch-bot.ps1"
echo.
echo Bot stopped. Press any key to close.
pause >nul
