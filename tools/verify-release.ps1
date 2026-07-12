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

function Get-StartupLogFragment {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Offset
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return ""
    }

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Position = [Math]::Min($Offset, $stream.Length)
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

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
    # A file-presence check cannot catch CLR/WinUI startup regressions. Launch
    # from outside {app} so self-contained assembly resolution is exercised in
    # the same way as a desktop shortcut or shell invocation.
    $startupLog = Join-Path $env:LOCALAPPDATA "TLAH Studio\logs\startup.log"
    $startupOffset = if (Test-Path -LiteralPath $startupLog) { (Get-Item -LiteralPath $startupLog).Length } else { 0 }
    $appPath = Join-Path $installDir "TLAHStudio.App.exe"
    $appProcess = Start-Process -FilePath $appPath -WorkingDirectory $smokeRoot -PassThru
    $deadline = (Get-Date).AddSeconds(18)
    $startupText = ""
    $activated = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $startupText = Get-StartupLogFragment -Path $startupLog -Offset $startupOffset
        if ($startupText -match "FATAL:|UNHANDLED XAML:") {
            if (-not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force }
            throw "Installed application failed its startup smoke test: $startupText"
        }
        if ($startupText -match "Window activated\.") {
            $activated = $true
            break
        }
        if ($appProcess.HasExited) {
            throw "Installed application exited before activating. Startup log: $startupText"
        }
    }
    if (-not $activated) {
        if (-not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force }
        throw "Installed application did not activate within 18 seconds. Startup log: $startupText"
    }
    if (-not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
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
    $expectedSignatureUrl = "https://download.matrixlabs.cn/tlah/windows/latest-$Version.json.sig"
    if ($latest.signatureUrl -ne $expectedSignatureUrl) {
        throw "latest.json signatureUrl must point to the immutable versioned signature: $expectedSignatureUrl"
    }
    if ($latest.platform -ne "windows" -or $latest.arch -ne "x64") {
        throw "latest.json platform and arch must be windows/x64."
    }
    if ($latest.minSupportedVersion -notmatch '^\d+\.\d+\.\d+$' -or
        [version]$latest.minSupportedVersion -gt [version]$Version) {
        throw "latest.json minSupportedVersion must be a semantic version no newer than the release."
    }

    $actualHash = (Get-FileHash -LiteralPath $installer.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($latest.sha256 -ne $actualHash) {
        throw "Installer SHA256 mismatch. latest.json=$($latest.sha256), actual=$actualHash"
    }
    $actualSize = (Get-Item -LiteralPath $installer.Path).Length
    if (-not $latest.PSObject.Properties.Name.Contains("installerSizeBytes")) {
        throw "latest.json must include installerSizeBytes."
    }
    if ([long]$latest.installerSizeBytes -ne $actualSize) {
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
    if (-not $authenticode.SignerCertificate) {
        throw "Installer is not Authenticode signed."
    }
    if ($authenticode.Status -ne "Valid") {
        if (-not $AllowUntrustedAuthenticode) {
            throw "Installer Authenticode status is $($authenticode.Status)."
        }
        Write-Warning "Installer is signed but not trusted by this machine: $($authenticode.Status)."
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
