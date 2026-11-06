<#
  ROSHIQ — сборка, тесты, публикация EXE и установщик.
  Запуск:  powershell -ExecutionPolicy Bypass -File .\build.ps1
  Параметры:
    -SkipTests          не запускать тесты
    -FrameworkDependent собрать EXE, требующий установленного .NET 8 Desktop Runtime (меньше размер)
    -Sign               подпись обязательна: без настроенного сертификата сборка остановится

  Подпись (Authenticode) включается сама, если задана одна из переменных окружения:
    ROSHIQ_SIGN_THUMBPRINT     отпечаток сертификата в хранилище Windows (в т.ч. на USB-токене)
    ROSHIQ_SIGN_PFX            путь к .pfx (+ ROSHIQ_SIGN_PFX_PASSWORD)
    ROSHIQ_SIGNTOOL            (необязательно) полный путь к signtool.exe
    ROSHIQ_TIMESTAMP_URL       (необязательно) сервер метки времени, по умолчанию http://timestamp.digicert.com
  Подробности — RELEASING.md.
#>
param(
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [switch]$Sign
)

$ErrorActionPreference = 'Continue'  # ошибки отслеживаются по $LASTEXITCODE
Set-Location -Path $PSScriptRoot
# Сообщения dotnet на английском — иначе кириллица в build.log превращается в кракозябры
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$log = Join-Path $PSScriptRoot 'build.log'
"=== ROSHIQ build $(Get-Date -Format s) ===" | Out-File $log -Encoding utf8

function Step($name, [scriptblock]$action) {
    Write-Host ""
    Write-Host "==> $name" -ForegroundColor Magenta
    "==> $name" | Out-File $log -Append -Encoding utf8
    & $action 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ОШИБКА на шаге: $name (подробности в build.log)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# 1. Проверка .NET SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host ".NET SDK не найден. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Red
    exit 1
}
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^(8|9|10)\.' })) {
    Write-Host "Нужен .NET SDK 8.0 или новее. Найдено:`n$sdks" -ForegroundColor Red
    exit 1
}
Write-Host "SDK: $(($sdks | Select-Object -Last 1))"

# Версия берётся из Directory.Build.props — менять её нужно только там
$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { Write-Host "Не найдена <Version> в Directory.Build.props" -ForegroundColor Red; exit 1 }
Write-Host "Версия: $version"

# ---------- Подпись ----------
function Find-SignTool {
    if ($env:ROSHIQ_SIGNTOOL -and (Test-Path $env:ROSHIQ_SIGNTOOL)) { return $env:ROSHIQ_SIGNTOOL }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin") | Where-Object { Test-Path $_ }
    foreach ($k in $kits) {
        $found = Get-ChildItem -Path $k -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

$signArgs = $null
if ($env:ROSHIQ_SIGN_THUMBPRINT) {
    $signArgs = @('/sha1', ($env:ROSHIQ_SIGN_THUMBPRINT -replace '\s', ''))
} elseif ($env:ROSHIQ_SIGN_PFX) {
    if (-not (Test-Path $env:ROSHIQ_SIGN_PFX)) { Write-Host "Файл сертификата не найден: $env:ROSHIQ_SIGN_PFX" -ForegroundColor Red; exit 1 }
    $signArgs = @('/f', $env:ROSHIQ_SIGN_PFX)
    if ($env:ROSHIQ_SIGN_PFX_PASSWORD) { $signArgs += @('/p', $env:ROSHIQ_SIGN_PFX_PASSWORD) }
}
$signtool = $null
if ($signArgs) {
    $signtool = Find-SignTool
    if (-not $signtool) {
        Write-Host "signtool.exe не найден. Установите Windows SDK (компонент «Signing Tools»): https://developer.microsoft.com/windows/downloads/windows-sdk/" -ForegroundColor Red
        exit 1
    }
    Write-Host "Подпись: включена ($signtool)"
} elseif ($Sign) {
    Write-Host "Указан -Sign, но сертификат не настроен (ROSHIQ_SIGN_THUMBPRINT или ROSHIQ_SIGN_PFX). См. RELEASING.md" -ForegroundColor Red
    exit 1
} else {
    Write-Host "Подпись: выключена (сертификат не настроен)" -ForegroundColor Yellow
}
$timestamp = if ($env:ROSHIQ_TIMESTAMP_URL) { $env:ROSHIQ_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }

function Sign-File($path) {
    & $signtool sign /fd sha256 /tr $timestamp /td sha256 /d "ROSHIQ" @signArgs $path
    if ($LASTEXITCODE -ne 0) { return }
    & $signtool verify /pa /q $path
}

# Иконки предметов встраиваются в программу; без интернета шаг просто пропускается
Write-Host ""
Write-Host "==> Иконки предметов" -ForegroundColor Magenta
try { & (Join-Path $PSScriptRoot 'tools\fetch-item-icons.ps1') 2>&1 | Tee-Object -FilePath $log -Append }
catch { Write-Host "Иконки не обновлены: $($_.Exception.Message)" -ForegroundColor Yellow }
$global:LASTEXITCODE = 0

Step "Восстановление пакетов" { dotnet restore Roshiq.sln }
Step "Сборка (Release)" { dotnet build Roshiq.sln -c Release --no-restore }

if (-not $SkipTests) {
    Step "Тесты" { dotnet test tests/Roshiq.Tests/Roshiq.Tests.csproj -c Release --no-build --logger "console;verbosity=normal" }
}

$dist = Join-Path $PSScriptRoot 'dist\Roshiq'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

if ($FrameworkDependent) {
    Step "Публикация EXE (нужен .NET 8 Desktop Runtime)" {
        dotnet publish src/Roshiq.App/Roshiq.App.csproj -c Release -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -o $dist
    }
} else {
    Step "Публикация автономного EXE (win-x64)" {
        dotnet publish src/Roshiq.App/Roshiq.App.csproj -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $dist
    }
}
Write-Host "EXE: $dist\Roshiq.exe" -ForegroundColor Green
if ($signArgs) { Step "Подпись Roshiq.exe" { Sign-File (Join-Path $dist 'Roshiq.exe') } }

# Переносная версия (zip без установки)
$distRoot = Join-Path $PSScriptRoot 'dist'
$portable = Join-Path $distRoot "Roshiq-Portable-$version.zip"
if (Test-Path $portable) { Remove-Item $portable -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $portable
Write-Host "Переносная версия: $portable" -ForegroundColor Green

# Установщик (Inno Setup 6, если установлен)
function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @()
    foreach ($v in '6', '7') {
        $candidates += "${env:ProgramFiles(x86)}\Inno Setup $v\ISCC.exe", "$env:ProgramFiles\Inno Setup $v\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup $v\ISCC.exe"
    }
    # Путь из реестра (установка в нестандартную папку)
    $keys = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*'
    foreach ($k in $keys) {
        Get-ItemProperty $k -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.InstallLocation) { $candidates += (Join-Path $_.InstallLocation 'ISCC.exe') }
        }
    }
    # Установка в корень любого диска, например D:\Inno Setup 7
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        foreach ($v in '6', '7') { $candidates += (Join-Path $d.Root "Inno Setup $v\ISCC.exe") }
    }
    return $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
$iscc = Find-Iscc
if ($iscc) { Write-Host "Inno Setup: $iscc" }

$setup = Join-Path $distRoot "Roshiq-Setup-$version.exe"
if ($iscc) {
    $isccArgs = @("/DAppVersion=$version")
    if ($signArgs) {
        # Inno Setup сам подписывает установщик и деинсталлятор этой командой ($f — файл, $q — кавычка)
        $quoted = ($signArgs | ForEach-Object { if ($_ -match '[\s"]') { '$q' + $_ + '$q' } else { $_ } }) -join ' '
        $isccArgs += @('/DSign', "/Sroshiq=`$q$signtool`$q sign /fd sha256 /tr $timestamp /td sha256 /d `$qROSHIQ`$q $quoted `$f")
    }
    if (Test-Path $setup) { Remove-Item $setup -Force }
    Step "Установщик (Inno Setup)" { & $iscc @isccArgs "installer\Roshiq.iss" }
    if ($signArgs) { Step "Проверка подписи установщика" { & $signtool verify /pa /q $setup } }
    Write-Host "Установщик: $setup" -ForegroundColor Green
} else {
    Write-Host "Inno Setup 6 не найден — установщик пропущен. Скачать: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host "Можно запускать dist\Roshiq\Roshiq.exe без установки." -ForegroundColor Yellow
}

# Контрольные суммы: программа сверяет с ними скачанное обновление
$sums = Join-Path $distRoot 'SHA256SUMS.txt'
$files = @($setup, $portable) | Where-Object { Test-Path $_ }
($files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) }) |
    Out-File $sums -Encoding ascii
Write-Host "Контрольные суммы: $sums" -ForegroundColor Green

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
