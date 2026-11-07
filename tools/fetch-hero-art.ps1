<#
  Скачивает портреты героев для интерфейса (публичный CDN Valve, 256×144 / 128×72)
  и упаковывает их в src\Roshiq.App\Assets\HeroArt.zip — он встраивается в программу при сборке.
  Картинки Valve в репозиторий не кладём: и папка, и zip в .gitignore.

  build.ps1 вызывает этот скрипт сам. Уже скачанные портреты повторно не качаются.
  Запуск вручную:  powershell -ExecutionPolicy Bypass -File .\tools\fetch-hero-art.ps1 [-Force]
  Без интернета сборка не ломается: интерфейс покажет запасные портреты 96×54 из Portraits.zip.
#>
param([switch]$Force)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$heroesFile = Join-Path $root 'src\Roshiq.Data\Resources\heroes.txt'
$work = Join-Path $root 'src\Roshiq.App\Assets\HeroArt'
$zip = Join-Path $root 'src\Roshiq.App\Assets\HeroArt.zip'
$cdn = 'https://cdn.akamai.steamstatic.com/apps/dota2/images/dota_react/heroes/'

if (-not (Test-Path $heroesFile)) { Write-Host "Не найден $heroesFile" -ForegroundColor Red; exit 0 }
New-Item -ItemType Directory -Force -Path $work | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$keys = Get-Content $heroesFile -Encoding utf8 |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object { ($_ -split '\|')[1].Trim() } |
    Where-Object { $_ -match '^[a-z0-9_]+$' } | Sort-Object -Unique

$ok = 0; $new = 0; $failed = @()
foreach ($key in $keys) {
    $file = Join-Path $work "$key.png"
    if ((Test-Path $file) -and -not $Force) { $ok++; continue }
    try {
        $resp = Invoke-WebRequest -Uri ($cdn + $key + '.png') -UseBasicParsing -TimeoutSec 30
        $bytes = $resp.Content
        if ($bytes -is [byte[]] -and $bytes.Length -gt 24 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50) {
            [IO.File]::WriteAllBytes($file, $bytes)
            $ok++; $new++
        } else { $failed += $key }
    } catch { $failed += $key }
}

Write-Host "Портреты героев: $ok из $($keys.Count) (новых: $new)"
if ($failed.Count -gt 0) { Write-Host ("Не скачались: " + ($failed -join ', ')) -ForegroundColor Yellow }

$pngs = Get-ChildItem $work -Filter *.png -ErrorAction SilentlyContinue
if ($pngs.Count -gt 0 -and ($new -gt 0 -or -not (Test-Path $zip))) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $work '*.png') -DestinationPath $zip
    Write-Host "Упаковано в $zip" -ForegroundColor Green
}
exit 0
