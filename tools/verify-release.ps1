param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$InstallerPath,

    [string]$LatestJsonPath = ".\TLAHStudio.Installer\latest.json",

    [string]$LatestSignaturePath,

    [string]$PublicKeyBase64,

    [switch]$AllowUntrustedAuthenticode,

    [switch]$SkipSmokeInstall,

    [switch]$ForceSmokeInstall
)

$ErrorActionPreference = "Stop"

function Invoke-SmokeInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallerPath,

        [Parameter(Mandatory = $true)]
        [string]$Version,

        [switch]$Force
    )

    $existingInstall = Join-Path $env:LOCALAPPDATA "Programs\TLAH Studio"
    if ((Test-Path -LiteralPath $existingInstall) -and -not $Force) {
        Write-Warning "Smoke install skipped because an existing user install was found at '$existingInstall'. Run verify-release.ps1 with -ForceSmokeInstall only on a disposable machine."
        return
    }

    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("TLAHStudioSmoke-" + [Guid]::NewGuid().ToString("N"))
    $installDir = Join-Path $smokeRoot "app"
    $logPath = Join-Path $smokeRoot "install.log"
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null

    $arguments = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/CLOSEAPPLICATIONS",
        "/NOLAUNCH",
        "/DIR=`"$installDir`"",
        "/LOG=`"$logPath`""
    )

    $process = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Smoke install failed with exit code $($process.ExitCode). Log: $logPath"
    }

    foreach ($requiredFile in @("TLAHStudio.App.exe", "TLAHStudio.Updater.exe", "version.json")) {
        $path = Join-Path $installDir $requiredFile
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Smoke install missing required file: $path"
        }
    }

    $installedVersion = (Get-Content -LiteralPath (Join-Path $installDir "version.json") -Raw | ConvertFrom-Json).version
    if ($installedVersion -ne $Version) {
        throw "Smoke install version mismatch. version.json=$installedVersion, expected=$Version"
    }

    $uninstaller = Join-Path $installDir "unins000.exe"
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstallProc = Start-Process -FilePath $uninstaller -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -Wait -PassThru
        if ($uninstallProc.ExitCode -ne 0) {
            Write-Warning "Smoke uninstall exited with code $($uninstallProc.ExitCode)."
        }
    }

    if (Test-Path -LiteralPath $smokeRoot) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repo
try {
    if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
        $InstallerPath = ".\TLAHStudio.Installer\output\TLAHStudioSetup-$Version.exe"
    }
    if ([string]::IsNullOrWhiteSpace($LatestSignaturePath)) {
        $LatestSignaturePath = "$LatestJsonPath.sig"
    }

    $installer = Resolve-Path -LiteralPath $InstallerPath
    $latestPath = Resolve-Path -LiteralPath $LatestJsonPath
    $latestSigPath = Resolve-Path -LiteralPath $LatestSignaturePath

    $latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    if ($latest.version -ne $Version) {
        throw "latest.json version '$($latest.version)' does not match release version '$Version'."
    }
    if ($latest.installerUrl -notmatch [regex]::Escape("TLAHStudioSetup-$Version.exe")) {
        throw "latest.json installerUrl does not point at TLAHStudioSetup-$Version.exe."
    }
    if ($latest.forceUpdate -ne $true) {
        throw "latest.json forceUpdate must be true for required desktop updates."
    }
    if ($latest.minSupportedVersion -ne $Version) {
        throw "latest.json minSupportedVersion must match the release version."
    }

    $actualHash = (Get-FileHash -LiteralPath $installer.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($latest.sha256 -ne $actualHash) {
        throw "Installer SHA256 mismatch. latest.json=$($latest.sha256), actual=$actualHash"
    }
    $actualSize = (Get-Item -LiteralPath $installer.Path).Length
    if ($latest.installerSizeBytes -and [long]$latest.installerSizeBytes -ne $actualSize) {
        throw "Installer size mismatch. latest.json=$($latest.installerSizeBytes), actual=$actualSize"
    }

    if ([string]::IsNullOrWhiteSpace($PublicKeyBase64)) {
        $cryptoSource = Get-Content -LiteralPath ".\TLAHStudio.Core\Services\UpdateCrypto.cs" -Raw
        if ($cryptoSource -notmatch 'PublicKeyBase64\s*=\s*"([^"]+)"') {
            throw "Could not read embedded update public key."
        }
        $PublicKeyBase64 = $Matches[1]
    }

    $jsonText = Get-Content -LiteralPath $latestPath -Raw
    $sigText = (Get-Content -LiteralPath $latestSigPath -Raw).Trim()
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $ecdsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKeyBase64), [ref]$bytesRead)
        $validManifestSignature = $ecdsa.VerifyData(
            [System.Text.Encoding]::UTF8.GetBytes($jsonText),
            [Convert]::FromBase64String($sigText),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        if (-not $validManifestSignature) {
            throw "latest.json signature verification failed."
        }
    }
    finally {
        $ecdsa.Dispose()
    }

    $authenticode = Get-AuthenticodeSignature -FilePath $installer.Path
    if ($authenticode.Status -ne "Valid") {
        if (-not $AllowUntrustedAuthenticode) {
            throw "Installer Authenticode status is $($authenticode.Status)."
        }
        if ($authenticode.SignerCertificate) {
            Write-Warning "Installer is signed but not trusted by this machine: $($authenticode.Status)."
        }
        else {
            Write-Warning "Installer is not Authenticode signed; continuing because -AllowUntrustedAuthenticode was provided."
        }
    }

    if (-not $SkipSmokeInstall) {
        Invoke-SmokeInstall -InstallerPath $installer.Path -Version $Version -Force:$ForceSmokeInstall
    }

    Write-Host "Release verification passed."
    Write-Host "Version: $Version"
    Write-Host "SHA256: $actualHash"
}
finally {
    Pop-Location
}
