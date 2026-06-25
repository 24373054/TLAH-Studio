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
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if (-not $signature.SignerCertificate) {
            throw "Signature was not embedded: $($file.FullName)"
        }

        if ($signature.Status -in @(
                [System.Management.Automation.SignatureStatus]::NotSigned,
                [System.Management.Automation.SignatureStatus]::HashMismatch)) {
            throw "Signature is invalid: $($file.FullName) [$($signature.Status)]"
        }

        if ($CertificateThumbprint -and
            $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "Unexpected signer certificate on $($file.FullName). Expected $CertificateThumbprint, got $($signature.SignerCertificate.Thumbprint)."
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
