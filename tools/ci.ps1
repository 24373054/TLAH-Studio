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
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                try {
                    Remove-Item -LiteralPath $resolved -Recurse -Force
                    break
                }
                catch {
                    if ($attempt -eq 3) {
                        throw
                    }
                    & dotnet build-server shutdown | Out-Null
                    Start-Sleep -Seconds $attempt
                }
            }
        }
    }

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
        Clear-ProjectArtifacts -ProjectDir ".\TLAHStudio.App"
        Invoke-Native dotnet @(
            "build",
            ".\TLAHStudio.App\TLAHStudio.App.csproj",
            "-c",
            $Configuration,
            "-p:Platform=$Platform",
            "-p:UseSharedCompilation=false",
            "-p:BuildInParallel=false",
            "-m:1",
            "-nr:false"
        )
        & dotnet build-server shutdown
        Clear-ProjectArtifacts -ProjectDir ".\TLAHStudio.Updater"
        Invoke-Native dotnet @(
            "build",
            ".\TLAHStudio.Updater\TLAHStudio.Updater.csproj",
            "-c",
            $Configuration,
            "-p:Platform=$Platform",
            "-p:UseSharedCompilation=false",
            "-p:BuildInParallel=false",
            "-m:1",
            "-nr:false"
        )
    }

    Write-Host "CI gate passed: tests and build are green."
}
finally {
    Pop-Location
}
