@echo off
REM Double-click this to build the overlay on your own machine.
REM
REM Nothing to install first - Windows already ships the C# compiler this uses.
REM Takes a few seconds, and the result appears in the dist folder.
REM
REM Why build instead of downloading: a file you compile yourself never came
REM from the internet, so it carries no "downloaded from the web" mark and no
REM reputation problem. Antivirus leaves it alone. The prebuilt download is
REM sometimes flagged as a false positive; this route sidesteps that entirely.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
echo   Done. The program is dist\NowPlayingOverlay.exe next to this file.
echo   Move it somewhere permanent, then run it - it lives in the system tray.
echo.
pause
