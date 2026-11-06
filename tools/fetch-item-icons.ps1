<#
  Скачивает иконки предметов (публичный CDN Valve) для всех предметов из базы ROSHIQ
  и упаковывает их в src\Roshiq.App\Assets\ItemIcons.zip — он встраивается в программу при сборке,
  так что в самой программе иконки есть сразу, без интернета.

  build.ps1 вызывает этот скрипт сам. Уже скачанные иконки повторно не качаются.
  Запуск вручную:  powershell -ExecutionPolicy Bypass -File .\tools\fetch-item-icons.ps1 [-Force]
    -Force  скачать заново все иконки (например после крупного патча, когда Valve меняет картинки)
  Без интернета скрипт ничего не ломает: сборка пройдёт, иконки подгрузятся позже в программе
  (если в настройках разрешена загрузка из сети).
#>
param([switch]$Force)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$itemsFile = Join-Path $root 'src\Roshiq.Data\Resources\items.txt'
$work = Join-Path $root 'src\Roshiq.App\Assets\ItemIcons'
$zip = Join-Path $root 'src\Roshiq.App\Assets\ItemIcons.zip'
$cdn = 'https://cdn.akamai.steamstatic.com/apps/dota2/images/dota_react/items/'

if (-not (Test-Path $itemsFile)) { Write-Host "Не найден $itemsFile" -ForegroundColor Red; exit 0 }
New-Item -ItemType Directory -Force -Path $work | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$keys = Get-Content $itemsFile -Encoding utf8 |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object { ($_ -split '\|')[0].Trim() } |
    Where-Object { $_ -match '^item_[a-z0-9_]+$' } | Sort-Object -Unique
# Предметы, о которых программа напоминает сама, даже если их нет в сборках
$keys = @($keys) + @('item_tpscroll', 'item_ward_observer', 'item_ward_sentry', 'item_dust', 'item_smoke_of_deceit') | Sort-Object -Unique

$ok = 0; $new = 0; $failed = @()
foreach ($key in $keys) {
    $name = $key.Substring(5)
    $file = Join-Path $work "$name.png"
    if ((Test-Path $file) -and -not $Force) { $ok++; continue }
    try {
        $resp = Invoke-WebRequest -Uri ($cdn + $name + '.png') -UseBasicParsing -TimeoutSec 20
        $bytes = $resp.Content
        if ($bytes -is [byte[]] -and $bytes.Length -gt 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50) {
            [IO.File]::WriteAllBytes($file, $bytes)
            $ok++; $new++
        } else { $failed += $name }
    } catch { $failed += $name }
}

Write-Host "Иконки предметов: $ok из $($keys.Count) (новых: $new)"
if ($failed.Count -gt 0) { Write-Host ("Не скачались: " + ($failed -join ', ')) -ForegroundColor Yellow }

$pngs = Get-ChildItem $work -Filter *.png -ErrorAction SilentlyContinue
if ($pngs.Count -gt 0 -and ($new -gt 0 -or -not (Test-Path $zip))) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $work '*.png') -DestinationPath $zip
    Write-Host "Упаковано в $zip" -ForegroundColor Green
}
exit 0
