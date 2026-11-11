@echo off
chcp 65001 >nul
rem Обновить репозиторий ROSHIQ на GitHub текущим состоянием этой папки.
rem Лишнее (bin, obj, dist, иконки предметов, build.log) не загружается — см. .gitignore.
cd /d "%~dp0"
set REPO=https://github.com/baramobatare-rgb/ROSHIQ.git
set LOG=%~dp0github-update.log
echo === %date% %time% === > "%LOG%"

where git >nul 2>&1 || (echo Git не найден. Установите Git: https://git-scm.com & echo git not found >> "%LOG%" & pause & exit /b 1)

if not exist ".git" (
  git init >> "%LOG%" 2>&1
  git symbolic-ref HEAD refs/heads/main >> "%LOG%" 2>&1
)
git remote get-url origin >nul 2>&1 && git remote set-url origin %REPO% || git remote add origin %REPO%

rem Имя и почта для коммитов только в этом репозитории (если не заданы глобально)
git config user.name >nul 2>&1 || git config user.name "baramobatare-rgb"
git config user.email >nul 2>&1 || git config user.email "baramobatare-rgb@users.noreply.github.com"

rem Сначала сохраняем свои изменения, потом сливаем с GitHub: файлы, которые здесь не менялись (например, сборки,
rem которые раз в неделю обновляет GitHub), берутся с GitHub, а не затираются старой копией из этой папки.
git add -A >> "%LOG%" 2>&1
set MSG=%~1
if "%MSG%"=="" set MSG=ROSHIQ update %date%
git diff --cached --quiet || git commit -m "%MSG%" >> "%LOG%" 2>&1

echo Получаю текущее состояние GitHub...
git fetch origin main >> "%LOG%" 2>&1
git rev-parse --verify -q origin/main >nul 2>&1 || goto push
git merge --no-edit origin/main >> "%LOG%" 2>&1
if errorlevel 1 (
  git merge --abort >> "%LOG%" 2>&1
  echo ОШИБКА: изменения здесь и на GitHub затрагивают одни и те же строки. Подробности в github-update.log
  echo MERGE CONFLICT >> "%LOG%"
  pause
  exit /b 1
)

:push

echo Загружаю на GitHub (если откроется окно входа — войдите)...
git push -u origin main >> "%LOG%" 2>&1
if errorlevel 1 (
  echo ОШИБКА: не удалось загрузить. Подробности в github-update.log
  echo PUSH FAILED >> "%LOG%"
) else (
  echo Готово: https://github.com/baramobatare-rgb/ROSHIQ
  echo PUSH OK >> "%LOG%"
)
pause
