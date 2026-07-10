<#
.SYNOPSIS
    Verify and atomically upload an already-built release without mutating it.

.PARAMETER Server
    SSH destination, for example user@download.matrixlabs.cn.

.PARAMETER PrivateKeyFile
    Optional ECDSA private key. Used only when latest.json.sig is missing.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Server,

    [string]$RemotePath = "/var/www/download/tlah/windows/",

    [string]$PrivateKeyFile
)

$ErrorActionPreference = "Stop"

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments = @()
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

if ($RemotePath -notmatch '^/[A-Za-z0-9._/-]+/?$') {
    throw "RemotePath contains unsupported shell characters: $RemotePath"
}

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repo
try {
    $installerDir = ".\TLAHStudio.Installer"
    $latestJson = Join-Path $installerDir "latest.json"
    $manifest = Get-Content -LiteralPath $latestJson -Raw | ConvertFrom-Json
    $version = [string]$manifest.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "latest.json has an invalid version: $version"
    }

    $installer = Resolve-Path -LiteralPath (Join-Path $installerDir "output\TLAHStudioSetup-$version.exe")
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash([System.IO.File]::ReadAllBytes($installer.Path))
    }
    finally {
        $sha.Dispose()
    }
    $hash = ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()

    $expectedUrl = "https://download.matrixlabs.cn/tlah/windows/TLAHStudioSetup-$version.exe"
    $installerSize = (Get-Item -LiteralPath $installer.Path).Length
    if ($manifest.sha256 -ne $hash) {
        throw "latest.json SHA256 does not match the installer. Run build-release.ps1 first."
    }
    if ($manifest.installerUrl -ne $expectedUrl) {
        throw "latest.json installerUrl does not match $expectedUrl."
    }
    if ([long]$manifest.installerSizeBytes -ne $installerSize) {
        throw "latest.json installerSizeBytes does not match the installer."
    }
    $expectedSignatureUrl = "https://download.matrixlabs.cn/tlah/windows/latest-$version.json.sig"
    if ($manifest.signatureUrl -ne $expectedSignatureUrl) {
        throw "latest.json signatureUrl does not match $expectedSignatureUrl."
    }

    $signaturePath = "$latestJson.sig"
    if (-not (Test-Path -LiteralPath $signaturePath)) {
        if ([string]::IsNullOrWhiteSpace($PrivateKeyFile)) {
            throw "latest.json.sig is missing and no PrivateKeyFile was provided."
        }
        & .\tools\sign-latest.ps1 -LatestJsonPath $latestJson -PrivateKeyFile $PrivateKeyFile
    }
    & .\tools\verify-release.ps1 `
        -Version $version `
        -InstallerPath $installer.Path `
        -LatestJsonPath $latestJson `
        -AllowUntrustedAuthenticode `
        -SkipSmokeInstall

    $remoteBase = $RemotePath.TrimEnd('/')
    $remoteInstaller = "$remoteBase/TLAHStudioSetup-$version.exe"
    $versionedSignaturePath = ".\TLAHStudio.Installer\latest-$version.json.sig"
    if (-not (Test-Path -LiteralPath $versionedSignaturePath)) {
        Copy-Item -LiteralPath $signaturePath -Destination $versionedSignaturePath -Force
    }
    $remoteVersionedSignature = "$remoteBase/latest-$version.json.sig"
    $remoteLegacySignature = "$remoteBase/latest.json.sig"
    $remoteManifest = "$remoteBase/latest.json"
    Invoke-Native scp @($installer.Path, "${Server}:$remoteInstaller.uploading")
    Invoke-Native scp @($versionedSignaturePath, "${Server}:$remoteVersionedSignature.uploading")
    Invoke-Native scp @($signaturePath, "${Server}:$remoteLegacySignature.uploading")
    Invoke-Native scp @($latestJson, "${Server}:$remoteManifest.uploading")

    $promote = "mv -- $remoteInstaller.uploading $remoteInstaller && " +
               "mv -- $remoteVersionedSignature.uploading $remoteVersionedSignature && " +
               "mv -- $remoteLegacySignature.uploading $remoteLegacySignature && " +
               "mv -- $remoteManifest.uploading $remoteManifest"
    Invoke-Native ssh @($Server, $promote)

    Write-Host "Release $version uploaded."
    Write-Host "Installer: $($installer.Path)"
    Write-Host "SHA256: $hash"
}
finally {
    Pop-Location
}
