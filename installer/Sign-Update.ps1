#Requires -Version 7
<#
.SYNOPSIS
    Signs an update manifest into the envelope the launcher verifies (RynthUpdater.VerifyEnvelope).

.DESCRIPTION
    Output: {"payload": base64(manifest bytes), "signatures": [{"key": <id>, "sig": base64}]},
    ECDSA P-256 / SHA-256 over the exact payload bytes, IEEE P1363 format. Signs with the DPAPI
    primary key from New-UpdateSigningKey.ps1 — no prompt — or, with -UseBackupKey, the offline
    backup key (asks for its password).
#>
param(
    [Parameter(Mandatory)][string]$PayloadPath,
    [Parameter(Mandatory)][string]$OutPath,
    [string]$KeyDir = (Join-Path $env:USERPROFILE ".rynthcore-update-signing"),
    [switch]$UseBackupKey,
    [string]$BackupKeyPath,
    # For scripted tests only; normally you are prompted.
    [SecureString]$BackupPassword
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security

$ec = [System.Security.Cryptography.ECDsa]::Create()
if ($UseBackupKey) {
    if (-not $BackupKeyPath) { $BackupKeyPath = Join-Path $KeyDir "backup.key.p8" }
    if (-not (Test-Path $BackupKeyPath)) { throw "Backup key not found at $BackupKeyPath" }
    if (-not $BackupPassword) { $BackupPassword = Read-Host "Backup key password" -AsSecureString }
    $pw = ConvertFrom-SecureString $BackupPassword -AsPlainText
    $read = 0
    $ec.ImportEncryptedPkcs8PrivateKey($pw, [IO.File]::ReadAllBytes($BackupKeyPath), [ref]$read)
    $pw = $null
} else {
    $primaryPath = Join-Path $KeyDir "primary.key"
    if (-not (Test-Path $primaryPath)) {
        throw "No signing key at $primaryPath. Run installer\New-UpdateSigningKey.ps1 first."
    }
    $entropy = [Text.Encoding]::UTF8.GetBytes("RynthCore update signing v1")
    $pkcs8 = [System.Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($primaryPath), $entropy, 'CurrentUser')
    try {
        $read = 0
        $ec.ImportPkcs8PrivateKey($pkcs8, [ref]$read)
    } finally { [Array]::Clear($pkcs8) }
}

$payload = [IO.File]::ReadAllBytes($PayloadPath)
$sig = $ec.SignData($payload, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                    [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
$spki = $ec.ExportSubjectPublicKeyInfo()
$id = ([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki), 0, 8)).ToLowerInvariant()

$envelope = [ordered]@{
    payload    = [Convert]::ToBase64String($payload)
    signatures = @([ordered]@{ key = $id; sig = [Convert]::ToBase64String($sig) })
}
Set-Content -Path $OutPath -Value ($envelope | ConvertTo-Json -Depth 4) -Encoding utf8NoBOM
Write-Host "Signed $PayloadPath with key $id -> $OutPath"
