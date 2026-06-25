<#
.SYNOPSIS
    One-command deploy: sign latest.json and upload everything to the server.

.PARAMETER Server
    SSH destination, e.g. user@download.matrixlabs.cn

.PARAMETER RemotePath
    Server directory, e.g. /var/www/download/tlah/windows/

.PARAMETER PrivateKeyFile
    Path to private_key.txt for signing

.EXAMPLE
    .\deploy.ps1 -Server user@download.matrixlabs.cn -PrivateKeyFile .\private_key.txt
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$Server,

    [string]$RemotePath = "/var/www/download/tlah/windows/",

    [Parameter(Mandatory=$true)]
    [string]$PrivateKeyFile
)

$installerDir = "$PSScriptRoot\..\TLAHStudio.Installer"
$latestJson = "$installerDir\latest.json"
$outputDir = "$installerDir\output"

Write-Host "=== TLAH Studio Deploy ===" -ForegroundColor Cyan
Write-Host "Server: $Server"
Write-Host "Remote: $RemotePath"
Write-Host ""

# 1. Find the latest installer
$installer = Get-ChildItem -Path $outputDir -Filter "TLAHStudioSetup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $installer) {
    Write-Error "No installer found in $outputDir. Build it first: dotnet publish + Inno Setup."
    exit 1
}

# 2. Compute SHA256
$hash = (Get-FileHash -Path $installer.FullName -Algorithm SHA256).Hash.ToLower()
Write-Host "Installer: $($installer.Name)"
Write-Host "SHA256:    $hash"

# 3. Update latest.json with the real SHA256
$json = Get-Content -Path $latestJson -Raw | ConvertFrom-Json
$json.sha256 = $hash
$json.installerUrl = "https://download.matrixlabs.cn/tlah/windows/$($installer.Name)"
$json.version = ($installer.Name -replace 'TLAHStudioSetup-', '' -replace '\.exe$', '')
$json | ConvertTo-Json -Depth 10 | Set-Content -Path $latestJson -Encoding UTF8
Write-Host "Updated latest.json with version $($json.version) and SHA256"

# 4. Sign latest.json
& "$PSScriptRoot\sign-latest.ps1" -LatestJsonPath $latestJson -PrivateKeyFile $PrivateKeyFile

# 5. Upload via SCP
Write-Host ""
Write-Host "Uploading to server..." -ForegroundColor Yellow
scp $latestJson "$($Server):$RemotePath"
scp "$latestJson.sig" "$($Server):$RemotePath"
scp $installer.FullName "$($Server):$RemotePath"

Write-Host ""
Write-Host "=== Deploy Complete ===" -ForegroundColor Green
Write-Host "Files uploaded to $RemotePath :"
Write-Host "  $($installer.Name)"
Write-Host "  latest.json"
Write-Host "  latest.json.sig"
