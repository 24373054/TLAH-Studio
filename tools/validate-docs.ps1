param()

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repo
try {
    $publicDocs = @(
        "README.md",
        "README-CN.md",
        "AGENTS.md",
        "CLAUDE.md",
        "CONTRIBUTING.md",
        "CONTRIBUTING-CN.md",
        "SECURITY.md",
        "SUPPORT.md",
        "CODE_OF_CONDUCT.md",
        "CHANGELOG.md",
        "THIRD-PARTY-NOTICES.md",
        "docs/README.md",
        "docs/ARCHITECTURE.md",
        "docs/DEVELOPMENT.md",
        "docs/RELEASING.md",
        "docs/PRIVACY.md"
    )

    foreach ($path in $publicDocs) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required documentation file is missing: $path"
        }
    }

    foreach ($required in @(
        "LICENSE",
        ".github/PULL_REQUEST_TEMPLATE.md",
        ".github/ISSUE_TEMPLATE/bug_report.yml",
        ".github/ISSUE_TEMPLATE/feature_request.yml",
        "docs/assets/readme/tlah-studio-overview.png"
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required repository-health asset is missing: $required"
        }
    }

    $linkPattern = [regex]'!?\[[^\]]*\]\(([^)]+)\)|(?:src|href)="([^"]+)"'
    $missingLinks = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $publicDocs) {
        $text = Get-Content -LiteralPath $path -Raw
        foreach ($match in $linkPattern.Matches($text)) {
            $raw = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
            $raw = $raw.Trim()
            $target = ($raw -split '#', 2)[0].Trim('<', '>')
            if ([string]::IsNullOrWhiteSpace($target) -or $target -match '^(?:https?://|mailto:)') {
                continue
            }

            $base = Split-Path -Parent $path
            if ([string]::IsNullOrEmpty($base)) { $base = "." }
            $resolved = [System.IO.Path]::GetFullPath((Join-Path $base $target), $repo.Path)
            if (-not (Test-Path -LiteralPath $resolved)) {
                $missingLinks.Add("${path}: $raw")
            }
        }
    }
    if ($missingLinks.Count -gt 0) {
        throw "Broken local documentation links:`n$($missingLinks -join "`n")"
    }

    $englishHeadings = @(Select-String -Path README.md -Pattern '^## ')
    $chineseHeadings = @(Select-String -Path README-CN.md -Pattern '^## ')
    if ($englishHeadings.Count -ne $chineseHeadings.Count) {
        throw "README section counts differ: English=$($englishHeadings.Count), Chinese=$($chineseHeadings.Count)."
    }

    $version = [string](Get-Content TLAHStudio.Installer/latest.json -Raw | ConvertFrom-Json).version
    foreach ($readme in @("README.md", "README-CN.md")) {
        if ((Get-Content -LiteralPath $readme -Raw) -notmatch [regex]::Escape($version)) {
            throw "$readme does not mention the current stable version $version."
        }
    }

    $currentText = ($publicDocs | ForEach-Object { Get-Content -LiteralPath $_ -Raw }) -join "`n"
    if ($currentText -match 'C:\\Users\\[^\\]+\\') {
        throw "Current public documentation contains a personal Windows absolute path."
    }

    Write-Host "Documentation validation passed."
    Write-Host "Files: $($publicDocs.Count)"
    Write-Host "README sections: $($englishHeadings.Count) per language"
    Write-Host "Stable version: $version"
}
finally {
    Pop-Location
}
