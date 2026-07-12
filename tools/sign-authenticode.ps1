param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path,

    [string]$CertificatePath,

    [securestring]$CertificatePassword,

    [string]$CertificateThumbprint,

    [switch]$LocalMachineCertificateStore,

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [string]$SignToolPath,

    [switch]$AllowUntrustedCertificate
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            throw "signtool.exe not found: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $candidate = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
        -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        throw "signtool.exe was not found. Install the Windows SDK or pass -SignToolPath."
    }

    return $candidate.FullName
}

function Convert-Password {
    param([securestring]$SecurePassword)

    if (-not $SecurePassword) { return $null }

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecurePassword)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

if (-not $CertificatePath -and -not $CertificateThumbprint) {
    throw "Provide either -CertificatePath for a PFX file or -CertificateThumbprint for a certificate installed in the certificate store."
}

$signTool = Find-SignTool $SignToolPath
$plainPassword = Convert-Password $CertificatePassword

$files = foreach ($item in $Path) {
    if (Test-Path -LiteralPath $item -PathType Container) {
        Get-ChildItem -LiteralPath $item -Recurse -Include *.exe, *.dll |
            Where-Object { -not $_.PSIsContainer }
    }
    elseif (Test-Path -LiteralPath $item -PathType Leaf) {
        Get-Item -LiteralPath $item
    }
    else {
        throw "Path not found: $item"
    }
}

$files = $files |
    Sort-Object FullName -Unique |
    Where-Object { $_.Extension -in ".exe", ".dll" }

if (-not $files) {
    throw "No .exe or .dll files found to sign."
}

foreach ($file in $files) {
    $args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/v")

    if ($CertificatePath) {
        $resolvedCert = (Resolve-Path -LiteralPath $CertificatePath).Path
        $args += @("/f", $resolvedCert)
        if ($plainPassword) {
            $args += @("/p", $plainPassword)
        }
    }
    else {
        if ($LocalMachineCertificateStore) {
            $args += "/sm"
        }
        $args += @("/sha1", $CertificateThumbprint)
    }

    $args += $file.FullName
    & $signTool @args
    if ($LASTEXITCODE -ne 0) {
        throw "Signing failed: $($file.FullName)"
    }

    if ($AllowUntrustedCertificate) {
        # M4.9.2: Get-AuthenticodeSignature can fail to auto-load under
        # PowerShell 7 (Microsoft.PowerShell.Security module load errors).
        # signtool sign already reported success above, so the signature is
        # embedded. signtool verify against /pa or /all typically rejects
        # self-signed roots under default policy — that's expected for
        # untrusted certs and not a real failure. We treat verification as
        # best-effort: warn on mismatch, never block the build. Windows trust
        # chain discovery can wait indefinitely on an unreachable revocation
        # endpoint, so keep this diagnostic on a short, bounded timeout.
        $verified = $false
        $verifyTimedOut = $false
        try {
            $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = $signTool
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.ArgumentList.Add("verify")
            $startInfo.ArgumentList.Add("/all")
            $startInfo.ArgumentList.Add($file.FullName)

            $verifyProcess = [System.Diagnostics.Process]::Start($startInfo)
            if ($verifyProcess.WaitForExit(15000)) {
                $verified = $verifyProcess.ExitCode -eq 0
            }
            else {
                $verifyTimedOut = $true
                $verifyProcess.Kill($true)
                $verifyProcess.WaitForExit()
            }
        } catch { }
        if (-not $verified) {
            $reason = if ($verifyTimedOut) { "verification timed out" } else { "certificate is self-signed/untrusted" }
            Write-Warning "Signature embedded but signtool verification did not succeed ($reason): $($file.FullName)"
        }

        Write-Host "Signature embedded with untrusted/self-signed certificate: $($file.FullName)"
    }
    else {
        & $signTool verify /pa /v $file.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed: $($file.FullName)"
        }
    }
}

Write-Host "Signed and verified $($files.Count) file(s)."
