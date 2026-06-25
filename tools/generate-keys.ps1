<#
.SYNOPSIS
    Generate ECDsa P-256 key pair for TLAH Studio update signing.
    Run ONCE. Save private key securely. Embed public key in the app.

.OUTPUTS
    public_key.txt  — embed in UpdateCrypto.cs as PublicKeyBase64
    private_key.txt — KEEP SECRET, use to sign latest.json
#>

Add-Type -AssemblyName System.Security

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
$publicKey = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
$privateKey = [Convert]::ToBase64String($ecdsa.ExportPkcs8PrivateKey())

$publicKey | Out-File -FilePath "public_key.txt" -Encoding ascii -NoNewline
$privateKey | Out-File -FilePath "private_key.txt" -Encoding ascii -NoNewline

Write-Host "=== Keys generated ===" -ForegroundColor Green
Write-Host ""
Write-Host "Public key saved to:  public_key.txt"
Write-Host "Private key saved to: private_key.txt"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Copy public_key.txt content into UpdateCrypto.cs PublicKeyBase64 constant"
Write-Host "2. Rebuild and re-publish the application"
Write-Host "3. Store private_key.txt securely on your server"
Write-Host "4. Use sign-latest.ps1 to sign each latest.json before uploading"
