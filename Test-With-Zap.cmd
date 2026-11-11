@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Test-With-Zap.ps1" %*
exit /b %ERRORLEVEL%
