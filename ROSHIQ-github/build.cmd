@echo off
rem Двойной клик: сборка, тесты, EXE и (если есть Inno Setup) установщик.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
