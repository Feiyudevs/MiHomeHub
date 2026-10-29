@echo off
setlocal
cd /d "%~dp0"
powershell -noProfile -ExecutionPolicy Bypass -File "%~dp0scripts\release.ps1" %*
if errorlevel 1 pause
endlocal
