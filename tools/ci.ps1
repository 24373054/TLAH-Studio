param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [switch]$SkipVulnerabilityAudit
)

$ErrorActionPreference = "Stop"
$minimumLineCoverage = 0.60
$minimumBranchCoverage = 0.50

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
    $testArtifactsRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $repo.Path "artifacts\test-results")
    )
    $coverageRoot = Join-Path $testArtifactsRoot "coverage"
    $testBuildRoot = Join-Path $testArtifactsRoot "build"

    function Reset-TestArtifacts {
        $repoPrefix = $repo.Path.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        ) + [System.IO.Path]::DirectorySeparatorChar

        if (-not $testArtifactsRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to manage test artifacts outside repo: $testArtifactsRoot"
        }

        if (Test-Path -LiteralPath $testArtifactsRoot) {
            Remove-Item -LiteralPath $testArtifactsRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Path $coverageRoot -Force | Out-Null
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

    if (-not $SkipVulnerabilityAudit) {
        $auditOutput = & dotnet list ".\TLAHStudio.sln" package `
            --vulnerable --include-transitive --format json --output-version 1 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet vulnerability audit failed:`n$($auditOutput -join [Environment]::NewLine)"
        }

        $auditText = $auditOutput -join [Environment]::NewLine
        $audit = $auditText | ConvertFrom-Json
        if ($audit.problems) {
            throw "NuGet vulnerability audit reported project errors:`n$($audit.problems | ConvertTo-Json -Depth 10)"
        }
        if (($audit | ConvertTo-Json -Depth 30) -match '"vulnerabilities"\s*:') {
            throw "NuGet vulnerability audit found one or more vulnerable packages:`n$auditText"
        }
        Write-Host "NuGet vulnerability audit passed."
    }

    Reset-TestArtifacts
    Invoke-Native dotnet @(
        "test",
        ".\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj",
        "-c",
        $Configuration,
        "--collect:XPlat Code Coverage",
        "--results-directory",
        $coverageRoot,
        "--artifacts-path",
        $testBuildRoot,
        # Release projects suppress PDBs; portable symbols are required for
        # Coverlet instrumentation. The artifacts path isolates this build
        # from the symbol-free outputs used for release packaging.
        "-p:DebugType=portable",
        "-p:DebugSymbols=true",
        "-p:UseSharedCompilation=false",
        "-p:BuildInParallel=false",
        "-m:1",
        "-nr:false"
    )

    $coverageReports = @(
        Get-ChildItem -LiteralPath $coverageRoot -Filter "coverage.cobertura.xml" -File -Recurse
    )
    if ($coverageReports.Count -eq 0) {
        throw "Test run completed without producing coverage.cobertura.xml under $coverageRoot."
    }

    $reportsWithCoverage = @(
        foreach ($report in $coverageReports) {
            [xml]$coverageXml = Get-Content -LiteralPath $report.FullName -Raw
            if ([int]$coverageXml.coverage.'lines-valid' -gt 0) {
                $report
            }
        }
    )
    if ($reportsWithCoverage.Count -eq 0) {
        throw "Coverage reports were generated, but none contain coverable lines."
    }
    $coverageMetrics = @(
        foreach ($report in $reportsWithCoverage) {
            [xml]$coverageXml = Get-Content -LiteralPath $report.FullName -Raw
            [pscustomobject]@{
                Path = $report.FullName
                LineRate = [double]::Parse(
                    [string]$coverageXml.coverage.'line-rate',
                    [Globalization.CultureInfo]::InvariantCulture)
                BranchRate = [double]::Parse(
                    [string]$coverageXml.coverage.'branch-rate',
                    [Globalization.CultureInfo]::InvariantCulture)
            }
        }
    )
    $qualifiedCoverage = @(
        $coverageMetrics | Where-Object {
            $_.LineRate -ge $minimumLineCoverage -and
            $_.BranchRate -ge $minimumBranchCoverage
        }
    )
    if ($qualifiedCoverage.Count -eq 0) {
        $details = $coverageMetrics | ForEach-Object {
            "  $($_.Path): line=$([Math]::Round($_.LineRate * 100, 2))%, branch=$([Math]::Round($_.BranchRate * 100, 2))%"
        }
        throw "Coverage is below the required line $($minimumLineCoverage * 100)% / branch $($minimumBranchCoverage * 100)% thresholds:`n$($details -join [Environment]::NewLine)"
    }
    Write-Host "Coverage report(s):"
    $coverageMetrics | ForEach-Object {
        Write-Host "  $($_.Path) (line $([Math]::Round($_.LineRate * 100, 2))%, branch $([Math]::Round($_.BranchRate * 100, 2))%)"
    }

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

    if ($SkipBuild) {
        Write-Host "CI gate passed: tests and coverage are green (build skipped)."
    }
    else {
        Write-Host "CI gate passed: tests, coverage, and build are green."
    }
}
finally {
    Pop-Location
}
