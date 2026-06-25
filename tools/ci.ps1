param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$SkipRestore,
    [switch]$SkipBuild
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
    if (-not $SkipRestore) {
        Invoke-Native dotnet @("restore", ".\TLAHStudio.sln")
    }

    Invoke-Native dotnet @(
        "test",
        ".\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj",
        "-c",
        $Configuration,
        "--no-restore",
        "-p:UseSharedCompilation=false",
        "-p:BuildInParallel=false",
        "-m:1",
        "-nr:false"
    )

    if (-not $SkipBuild) {
        & dotnet build-server shutdown
        Invoke-Native dotnet @(
            "build",
            ".\TLAHStudio.App\TLAHStudio.App.csproj",
            "-c",
            $Configuration,
            "-p:Platform=$Platform",
            "-p:UseSharedCompilation=false",
            "-p:BuildInParallel=false",
            "-m:1",
            "-nr:false",
            "--no-restore"
        )
        Invoke-Native dotnet @(
            "build",
            ".\TLAHStudio.Updater\TLAHStudio.Updater.csproj",
            "-c",
            $Configuration,
            "-p:Platform=$Platform",
            "-p:UseSharedCompilation=false",
            "-p:BuildInParallel=false",
            "-m:1",
            "-nr:false",
            "--no-restore"
        )
    }

    Write-Host "CI gate passed: tests and build are green."
}
finally {
    Pop-Location
}
