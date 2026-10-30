#Requires -Version 7
<#
.SYNOPSIS
    Creates the RynthCore update-signing keys. Run it once, yourself.

.DESCRIPTION
    Launchers install an update only if its feed is signed by a key they trust. This makes
    two ECDSA P-256 keys:

      primary  Signs every release. Saved to <KeyDir>\primary.key, encrypted with Windows
               DPAPI for your user account: Publish-Update.ps1 signs without a prompt, and the
               file is useless on any other account or PC.
      backup   Replaces the primary if it is ever lost (new PC, reinstall). Saved to
               <KeyDir>\backup.key.p8, encrypted with a password you choose here. Move it
               OFFLINE (a USB stick in a drawer) and delete the copy on this PC.

    Both public halves are written into src\RynthCore.App.Avalonia\UpdateTrust.cs, so
    launchers built afterwards trust either key. Commit that file, then build and release.

    The private keys never leave this PC. Never copy them to the server: the point of signing
    is that a compromised server still can't push code to players.

.PARAMETER Force
    Replace existing keys. Launchers that only trust the old keys will refuse every release
    signed with the new ones, so this strands them until they reinstall by hand.
#>
param(
    [string]$KeyDir = (Join-Path $env:USERPROFILE ".rynthcore-update-signing"),
    [string]$TrustFile = (Join-Path $PSScriptRoot "..\src\RynthCore.App.Avalonia\UpdateTrust.cs"),
    # For scripted tests only; normally you are prompted.
    [SecureString]$BackupPassword,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security
$crypto = [System.Security.Cryptography.ProtectedData].Assembly   # DPAPI present (fails loudly if not)

$primaryPath = Join-Path $KeyDir "primary.key"
$backupPath  = Join-Path $KeyDir "backup.key.p8"
if ((Test-Path $primaryPath) -and -not $Force) {
    throw "Keys already exist in $KeyDir. Replacing them strands launchers that trust only the old ones; pass -Force only if you mean it."
}
if (-not (Test-Path $TrustFile)) { throw "UpdateTrust.cs not found at $TrustFile" }

if (-not $BackupPassword) {
    $BackupPassword = Read-Host "Password for the BACKUP key (needed only if the primary is ever lost)" -AsSecureString
    $again = Read-Host "Repeat it" -AsSecureString
    if ((ConvertFrom-SecureString $BackupPassword -AsPlainText) -ne (ConvertFrom-SecureString $again -AsPlainText)) {
        throw "The passwords don't match."
    }
}
$plain = ConvertFrom-SecureString $BackupPassword -AsPlainText
if ($plain.Length -lt 12) { throw "Use at least 12 characters for the backup key password." }

New-Item -ItemType Directory -Force $KeyDir | Out-Null
$curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
$primary = [System.Security.Cryptography.ECDsa]::Create($curve)
$backup  = [System.Security.Cryptography.ECDsa]::Create($curve)

# Primary: DPAPI (CurrentUser), with fixed entropy so a stray DPAPI blob can't pose as it.
$entropy = [Text.Encoding]::UTF8.GetBytes("RynthCore update signing v1")
$pkcs8 = $primary.ExportPkcs8PrivateKey()
try {
    $blob = [System.Security.Cryptography.ProtectedData]::Protect($pkcs8, $entropy, 'CurrentUser')
    [IO.File]::WriteAllBytes($primaryPath, $blob)
} finally { [Array]::Clear($pkcs8) }

# Backup: password-encrypted PKCS#8 (AES-256, 600k PBKDF2 rounds).
$pbe = [System.Security.Cryptography.PbeParameters]::new(
    [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
[IO.File]::WriteAllBytes($backupPath, $backup.ExportEncryptedPkcs8PrivateKey($plain, $pbe))
$plain = $null

function KeyId([byte[]]$spki) {
    $h = [System.Security.Cryptography.SHA256]::HashData($spki)
    return ([Convert]::ToHexString($h, 0, 8)).ToLowerInvariant()
}
$keys = foreach ($k in @(@{ Name = "primary"; Key = $primary }, @{ Name = "backup"; Key = $backup })) {
    $spki = $k.Key.ExportSubjectPublicKeyInfo()
    [pscustomobject]@{ Name = $k.Name; Id = KeyId $spki; Base64 = [Convert]::ToBase64String($spki) }
}

# Write the public keys between the markers in UpdateTrust.cs.
$src = Get-Content $TrustFile -Raw
$begin = "// BEGIN KEYS (New-UpdateSigningKey.ps1)"
$end = "// END KEYS"
$i = $src.IndexOf($begin); $j = $src.IndexOf($end)
if ($i -lt 0 -or $j -lt $i) { throw "Key markers not found in $TrustFile" }
$indent = "        "
$lines = $keys | ForEach-Object { "$indent`"$($_.Base64)`", // $($_.Name) $($_.Id)" }
$new = $src.Substring(0, $i + $begin.Length) + "`r`n" + ($lines -join "`r`n") + "`r`n$indent" + $src.Substring($j)
Set-Content -Path $TrustFile -Value $new -NoNewline -Encoding utf8

Write-Host ""
Write-Host "Keys created in $KeyDir" -ForegroundColor Green
$keys | ForEach-Object { Write-Host ("  {0,-8} {1}" -f $_.Name, $_.Id) }
Write-Host ""
Write-Host "Public keys written to $TrustFile" -ForegroundColor Green
Write-Host "Next:"
Write-Host "  1. Move $backupPath OFFLINE (USB stick), then delete it here. Keep its password somewhere safe."
Write-Host "  2. Commit UpdateTrust.cs, then publish with installer\Publish-Update.ps1 -Version <yyyy.m.d.n>."
