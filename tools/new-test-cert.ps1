<#
  Тестовый (самоподписанный) сертификат подписи — ТОЛЬКО чтобы проверить, что подпись в build.ps1 работает.
  Другим людям такой сертификат ничего не даёт: Windows всё равно покажет «Неизвестный издатель».

  Запуск:  powershell -ExecutionPolicy Bypass -File .\tools\new-test-cert.ps1 [-Trust]
    -Trust  добавить сертификат в доверенные корневые ТЕКУЩЕГО пользователя (Windows спросит подтверждение),
            иначе проверка подписи в build.ps1 не пройдёт. Удалить потом: certmgr.msc →
            «Доверенные корневые центры сертификации» и «Личное» → «ROSHIQ Test Signing».
#>
param([switch]$Trust)

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ROSHIQ Test Signing' `
    -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(1)
Write-Host "Создан сертификат: $($cert.Subject)  отпечаток $($cert.Thumbprint)" -ForegroundColor Green

if ($Trust) {
    $tmp = Join-Path $env:TEMP 'roshiq-test-cert.cer'
    Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
    Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    Remove-Item $tmp
    Write-Host "Сертификат добавлен в доверенные корневые текущего пользователя." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Подписанная сборка в этом окне PowerShell:"
Write-Host "  `$env:ROSHIQ_SIGN_THUMBPRINT = '$($cert.Thumbprint)'"
Write-Host "  .\build.ps1"
