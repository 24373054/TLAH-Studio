param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseNotes,

    [string]$CertificatePath,

    [securestring]$CertificatePassword,

    [string]$CertificateThumbprint,

    [switch]$LocalMachineCertificateStore,

    [switch]$AllowUntrustedCertificate,

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [string]$PrivateKeyFile = ".\tools\private_key.txt",

    [switch]$Upload,

    [string]$Server = "ubuntu@140.143.183.163",

    [string]$RemotePath = "/var/www/download/tlah/windows/",

    [switch]$SkipQualityGate,

    [switch]$SkipSmokeTest,

    [switch]$ForceSmokeTest
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

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repo
try {
    $appProject = ".\TLAHStudio.App\TLAHStudio.App.csproj"
    $appManifest = ".\TLAHStudio.App\app.manifest"
    $appSettings = ".\TLAHStudio.App\appsettings.json"
    $updaterProject = ".\TLAHStudio.Updater\TLAHStudio.Updater.csproj"
    $updaterManifest = ".\TLAHStudio.Updater\app.manifest"
    $coreProject = ".\TLAHStudio.Core\TLAHStudio.Core.csproj"
    $dataProject = ".\TLAHStudio.Data\TLAHStudio.Data.csproj"
    $setupScript = ".\TLAHStudio.Installer\setup.iss"
    $versionJson = ".\TLAHStudio.Installer\version.json"
    $latestJson = ".\TLAHStudio.Installer\latest.json"
    $inno = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

    if (-not (Test-Path -LiteralPath $inno)) {
        throw "Inno Setup compiler not found: $inno"
    }

    function Set-XmlVersion {
        param([string]$Path, [string]$Version)

        [xml]$xml = Get-Content -LiteralPath $Path
        $group = $xml.Project.PropertyGroup | Select-Object -First 1
        foreach ($name in @("Version", "FileVersion", "AssemblyVersion", "InformationalVersion")) {
            $value = if ($name -in @("FileVersion", "AssemblyVersion")) { "$Version.0" } else { $Version }
            $node = $group.SelectSingleNode($name)
            if (-not $node) {
                $node = $xml.CreateElement($name)
                [void]$group.AppendChild($node)
            }
            $node.InnerText = $value
        }
        $xml.Save((Resolve-Path -LiteralPath $Path).Path)
    }

    function Set-ManifestIdentityVersion {
        param([string]$Path, [string]$Version)

        $manifest = Get-Content -LiteralPath $Path -Raw
        $manifest = $manifest -replace '(<assemblyIdentity\b[^>]*\bversion=")[^"]+(")', "`${1}$Version.0`${2}"
        Set-Content -LiteralPath $Path -Value ($manifest.TrimEnd() + [Environment]::NewLine) -Encoding UTF8 -NoNewline
    }

    Set-XmlVersion $appProject $Version
    Set-XmlVersion $updaterProject $Version
    Set-XmlVersion $coreProject $Version
    Set-XmlVersion $dataProject $Version
    Set-ManifestIdentityVersion $appManifest $Version
    Set-ManifestIdentityVersion $updaterManifest $Version

    $appSettingsInfo = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
    if ($appSettingsInfo.App) {
        $appSettingsInfo.App.Version = $Version
        $appSettingsInfo | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $appSettings -Encoding UTF8
    }

    $setup = Get-Content -LiteralPath $setupScript -Raw
    $setup = $setup -replace '#define MyAppVersion ".+?"', "#define MyAppVersion `"$Version`""
    Set-Content -LiteralPath $setupScript -Value ($setup.TrimEnd() + [Environment]::NewLine) -Encoding UTF8 -NoNewline

    $versionInfo = Get-Content -LiteralPath $versionJson -Raw | ConvertFrom-Json
    $versionInfo.version = $Version
    $versionInfo | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $versionJson -Encoding UTF8

    $latest = Get-Content -LiteralPath $latestJson -Raw | ConvertFrom-Json
    $latest.version = $Version
    $latest.installerUrl = "https://download.matrixlabs.cn/tlah/windows/TLAHStudioSetup-$Version.exe"
    $latest.sha256 = ""
    $latest.forceUpdate = $true
    $latest.minSupportedVersion = $Version
    if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        $ReleaseNotes = "Trust hardening release.`n`nFixes:`n- Release builds no longer ship PDB debug symbols`n- App and updater carry company/product version metadata`n- Authenticode signing is supported for app binaries, updater, and installer when a code signing certificate is provided"
    }
    $latest.releaseNotes = $ReleaseNotes
    $latest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $latestJson -Encoding UTF8

    if (-not $SkipQualityGate) {
        & .\tools\ci.ps1 -Configuration Release -Platform x64
    }

    # WinUI's XAML compiler can leave reusable MSBuild nodes holding generated
    # files after the quality gate. Shut them down before cleaning/publishing.
    & dotnet build-server shutdown

    $publishRoots = @(
        ".\TLAHStudio.App\obj\x64\Release",
        ".\TLAHStudio.App\bin\x64\Release",
        ".\TLAHStudio.Updater\obj\x64\Release",
        ".\TLAHStudio.Updater\bin\x64\Release",
        ".\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish",
        ".\TLAHStudio.Updater\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"
    )
    foreach ($publishRoot in $publishRoots) {
        if (Test-Path -LiteralPath $publishRoot) {
            $resolved = (Resolve-Path -LiteralPath $publishRoot).Path
            if (-not $resolved.StartsWith($repo.Path, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to delete outside repo: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }

    function Clear-ProjectArtifacts {
        param([Parameter(Mandatory = $true)][string]$ProjectDir)

        foreach ($name in @("obj", "bin")) {
            $path = Join-Path $ProjectDir $name
            if (-not (Test-Path -LiteralPath $path)) {
                continue
            }
            $resolved = (Resolve-Path -LiteralPath $path).Path
            if (-not $resolved.StartsWith($repo.Path, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to delete outside repo: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }

    function Invoke-DotnetWithRetry {
        param(
            [Parameter(Mandatory = $true)]
            [string[]]$Arguments,

            [Parameter(Mandatory = $true)]
            [string]$ProjectDir
        )

        try {
            Invoke-Native dotnet $Arguments
        }
        catch {
            Write-Warning "dotnet $($Arguments -join ' ') failed once; cleaning $ProjectDir artifacts and retrying."
            & dotnet build-server shutdown
            Clear-ProjectArtifacts -ProjectDir $ProjectDir
            Start-Sleep -Seconds 2
            Invoke-Native dotnet $Arguments
        }
    }

    function Test-AppPublishXamlResources {
        param([Parameter(Mandatory = $true)][string]$PublishDir)

        foreach ($requiredFile in @(
            "App.xbf",
            "MainWindow.xbf",
            "TLAHStudio.App.pri",
            "Views\SidebarPage.xbf",
            "Views\ChatPage.xbf",
            "Views\MessageInputControl.xbf"
        )) {
            $path = Join-Path $PublishDir $requiredFile
            if (-not (Test-Path -LiteralPath $path)) {
                throw "Published app is missing required WinUI XAML resource: $path"
            }
        }
    }

    function Invoke-AppPublishStartupSmoke {
        param([Parameter(Mandatory = $true)][string]$PublishDir)

        $exe = Join-Path $PublishDir "TLAHStudio.App.exe"
        if (-not (Test-Path -LiteralPath $exe)) {
            throw "Published app executable was not found: $exe"
        }

        $logDir = Join-Path $env:LOCALAPPDATA "TLAH Studio\logs"
        $logPath = Join-Path $logDir "startup.log"
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        if (Test-Path -LiteralPath $logPath) {
            Remove-Item -LiteralPath $logPath -Force
        }

        $process = Start-Process -FilePath $exe -WorkingDirectory $PublishDir -WindowStyle Hidden -PassThru
        try {
            $deadline = (Get-Date).AddSeconds(15)
            while ((Get-Date) -lt $deadline) {
                $logText = if (Test-Path -LiteralPath $logPath) {
                    Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
                }
                else {
                    ""
                }

                if ($logText -match "Window activated\.") {
                    Write-Host "Published app startup smoke passed."
                    return
                }

                if ($logText -match "FATAL:" -or $logText -match "UNHANDLED XAML") {
                    throw "Published app startup smoke failed before activation.`n$logText"
                }

                if ($process.HasExited) {
                    throw "Published app exited before activation with code $($process.ExitCode).`n$logText"
                }

                Start-Sleep -Milliseconds 500
            }

            $lastLog = if (Test-Path -LiteralPath $logPath) {
                Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
            }
            else {
                ""
            }
            throw "Published app did not activate within 15 seconds.`n$lastLog"
        }
        finally {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Invoke-DotnetWithRetry -ProjectDir ".\TLAHStudio.App" -Arguments @("publish", $appProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:Platform=x64", "-p:WindowsAppSDKSelfContained=true", "-p:PublishSingleFile=false", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-m:1", "-nr:false")
    Invoke-DotnetWithRetry -ProjectDir ".\TLAHStudio.Updater" -Arguments @("publish", $updaterProject, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:Platform=x64", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-m:1", "-nr:false")

    $appPublishDir = ".\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"
    Test-AppPublishXamlResources -PublishDir $appPublishDir
    if (-not $SkipSmokeTest) {
        Invoke-AppPublishStartupSmoke -PublishDir $appPublishDir
    }

    $hasCertificate = $CertificatePath -or $CertificateThumbprint
    if ($hasCertificate) {
        $signArgs = @{
            Path = @(
                ".\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\TLAHStudio.App.exe",
                ".\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\TLAHStudio.App.dll",
                ".\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\TLAHStudio.Core.dll",
                ".\TLAHStudio.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\TLAHStudio.Data.dll",
                ".\TLAHStudio.Updater\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\TLAHStudio.Updater.exe"
            )
            TimestampUrl = $TimestampUrl
        }
        if ($CertificatePath) { $signArgs.CertificatePath = $CertificatePath }
        if ($CertificatePassword) { $signArgs.CertificatePassword = $CertificatePassword }
        if ($CertificateThumbprint) { $signArgs.CertificateThumbprint = $CertificateThumbprint }
        if ($LocalMachineCertificateStore) { $signArgs.LocalMachineCertificateStore = $true }
        if ($AllowUntrustedCertificate) { $signArgs.AllowUntrustedCertificate = $true }
        & .\tools\sign-authenticode.ps1 @signArgs
    }
    else {
        Write-Warning "No Authenticode certificate was provided; binaries and installer will remain unsigned."
    }

    Invoke-Native $inno @($setupScript)

    $installer = Resolve-Path ".\TLAHStudio.Installer\output\TLAHStudioSetup-$Version.exe"

    if ($hasCertificate) {
        $signInstallerArgs = @{
            Path = @($installer.Path)
            TimestampUrl = $TimestampUrl
        }
        if ($CertificatePath) { $signInstallerArgs.CertificatePath = $CertificatePath }
        if ($CertificatePassword) { $signInstallerArgs.CertificatePassword = $CertificatePassword }
        if ($CertificateThumbprint) { $signInstallerArgs.CertificateThumbprint = $CertificateThumbprint }
        if ($LocalMachineCertificateStore) { $signInstallerArgs.LocalMachineCertificateStore = $true }
        if ($AllowUntrustedCertificate) { $signInstallerArgs.AllowUntrustedCertificate = $true }
        & .\tools\sign-authenticode.ps1 @signInstallerArgs
    }

    $sha256 = (Get-FileHash -LiteralPath $installer.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $latest = Get-Content -LiteralPath $latestJson -Raw | ConvertFrom-Json
    $latest.sha256 = $sha256
    $latest | Add-Member -NotePropertyName installerSizeBytes -NotePropertyValue ((Get-Item -LiteralPath $installer.Path).Length) -Force
    $latest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $latestJson -Encoding UTF8

    & .\tools\sign-latest.ps1 -LatestJsonPath $latestJson -PrivateKeyFile $PrivateKeyFile

    $verifyArgs = @{
        Version = $Version
        InstallerPath = $installer.Path
        LatestJsonPath = $latestJson
    }
    if ($AllowUntrustedCertificate) { $verifyArgs.AllowUntrustedAuthenticode = $true }
    if ($SkipSmokeTest) { $verifyArgs.SkipSmokeInstall = $true }
    if ($ForceSmokeTest) { $verifyArgs.ForceSmokeInstall = $true }
    & .\tools\verify-release.ps1 @verifyArgs

    Copy-Item -LiteralPath $installer.Path -Destination "C:\Users\23157\CODE\00TLAH\TLAHStudioSetup-$Version.exe" -Force

    if ($Upload) {
        Invoke-Native scp @($installer.Path, "$($Server):$RemotePath")
        Invoke-Native scp @($latestJson, "$($Server):$RemotePath")
        Invoke-Native scp @("$latestJson.sig", "$($Server):$RemotePath")
    }

    Write-Host "Release $Version ready."
    Write-Host "Installer: $($installer.Path)"
    Write-Host "SHA256: $sha256"
    if (-not $hasCertificate) {
        Write-Warning "This release is unsigned. Do not publish it as a trust/reputation fix."
    }
}
finally {
    Pop-Location
}
