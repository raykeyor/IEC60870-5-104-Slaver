#!/usr/bin/env pwsh
<#
.SYNOPSIS
    生成三种测试用证书：
      1. expired.pfx        — 已过期（30 天前到期）
      2. expiring-soon.pfx  — 24 小时后到期
      3. invalid.pfx        — 损坏/非法的证书文件
#>

$certsDir = Join-Path $PSScriptRoot "IEC104Simulator\bin\Debug\net8.0\certs"
New-Item -ItemType Directory -Path $certsDir -Force | Out-Null

$password  = ConvertTo-SecureString "test1234" -AsPlainText -Force
$storeBase = "Cert:\CurrentUser\My"

function Remove-TempCert($cert) {
    Remove-Item -Path "$storeBase\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
}

# ─── 1. 已过期证书 ──────────────────────────────────────────────────────────
Write-Host "`n[1/3] 生成已过期证书 (expired.pfx) ..." -ForegroundColor Cyan

$expiredCert = New-SelfSignedCertificate `
    -Subject          "CN=IEC104 Test - Expired" `
    -FriendlyName     "IEC104 Test Expired Cert" `
    -NotBefore        (Get-Date).AddDays(-90) `
    -NotAfter         (Get-Date).AddDays(-30) `
    -KeyAlgorithm     RSA `
    -KeyLength        2048 `
    -HashAlgorithm    SHA256 `
    -KeyUsage         DigitalSignature, KeyEncipherment `
    -CertStoreLocation $storeBase

$outPath = Join-Path $certsDir "expired.pfx"
Export-PfxCertificate -Cert $expiredCert -FilePath $outPath -Password $password | Out-Null
Remove-TempCert $expiredCert
Write-Host "  -> $outPath" -ForegroundColor Green
Write-Host "     有效期: $($expiredCert.NotBefore.ToString('yyyy-MM-dd')) ~ $($expiredCert.NotAfter.ToString('yyyy-MM-dd'))" -ForegroundColor Gray

# ─── 2. 24 小时内过期证书 ────────────────────────────────────────────────────
Write-Host "`n[2/3] 生成 24 小时内过期证书 (expiring-soon.pfx) ..." -ForegroundColor Cyan

$expiringSoonCert = New-SelfSignedCertificate `
    -Subject          "CN=IEC104 Test - Expiring Soon" `
    -FriendlyName     "IEC104 Test Expiring Soon Cert" `
    -NotBefore        (Get-Date).AddDays(-1) `
    -NotAfter         (Get-Date).AddHours(23) `
    -KeyAlgorithm     RSA `
    -KeyLength        2048 `
    -HashAlgorithm    SHA256 `
    -KeyUsage         DigitalSignature, KeyEncipherment `
    -CertStoreLocation $storeBase

$outPath = Join-Path $certsDir "expiring-soon.pfx"
Export-PfxCertificate -Cert $expiringSoonCert -FilePath $outPath -Password $password | Out-Null
Remove-TempCert $expiringSoonCert
Write-Host "  -> $outPath" -ForegroundColor Green
Write-Host "     有效期: $($expiringSoonCert.NotBefore.ToString('yyyy-MM-dd HH:mm')) ~ $($expiringSoonCert.NotAfter.ToString('yyyy-MM-dd HH:mm'))" -ForegroundColor Gray

# ─── 3. 非法/损坏证书 ───────────────────────────────────────────────────────
Write-Host "`n[3/3] 生成非法损坏证书 (invalid.pfx) ..." -ForegroundColor Cyan

# 写入伪造的 PFX 头 + 随机垃圾字节，使任何解析器都无法加载
$fakeBytes = [byte[]]::new(256)
$fakeBytes[0] = 0x30   # ASN.1 SEQUENCE 标志，让它"像"一个证书但内容无效
$fakeBytes[1] = 0x82
$fakeBytes[2] = 0x00
$fakeBytes[3] = 0xF8
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($fakeBytes, 4, 252)

$outPath = Join-Path $certsDir "invalid.pfx"
[System.IO.File]::WriteAllBytes($outPath, $fakeBytes)
Write-Host "  -> $outPath  ($($fakeBytes.Length) 字节随机垃圾数据)" -ForegroundColor Green

# ─── 汇总 ────────────────────────────────────────────────────────────────────
Write-Host "`n===== 完成 =====" -ForegroundColor Yellow
Write-Host "证书目录: $certsDir" -ForegroundColor Yellow
Write-Host "PFX 密码: test1234" -ForegroundColor Yellow
Get-ChildItem $certsDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
