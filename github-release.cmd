@echo off
chcp 65001 >nul
rem Выпустить версию ROSHIQ на GitHub: загрузить код и поставить тег v<версия>.
rem По тегу GitHub Actions сам соберёт установщик и опубликует его в Releases (5–10 минут).
rem Версия берётся из Directory.Build.props — перед выпуском новой версии поменяйте её там.
cd /d "%~dp0"
set LOG=%~dp0github-release.log
echo === %date% %time% === > "%LOG%"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version | Select-Object -First 1"`) do set VER=%%v
if "%VER%"=="" (echo Не удалось прочитать версию & echo no version >> "%LOG%" & pause & exit /b 1)
echo Версия: %VER%
echo version %VER% >> "%LOG%"

call "%~dp0github-update.cmd" "ROSHIQ %VER%" < nul
set LOG=%~dp0github-release.log

git rev-parse -q --verify "refs/tags/v%VER%" >nul 2>&1 && (
  echo Тег v%VER% уже есть — эта версия уже выпущена. Поменяйте ^<Version^> в Directory.Build.props.
  echo TAG EXISTS >> "%LOG%"
  pause
  exit /b 1
)
git tag -a "v%VER%" -m "ROSHIQ %VER%" >> "%LOG%" 2>&1
git push origin "v%VER%" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo ОШИБКА: тег не загрузился. Подробности в github-release.log
  echo TAG PUSH FAILED >> "%LOG%"
) else (
  echo Готово. Через 5–10 минут установщик появится здесь:
  echo https://github.com/baramobatare-rgb/ROSHIQ/releases
  echo TAG PUSH OK >> "%LOG%"
)
pause
