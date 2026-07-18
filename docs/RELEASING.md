# Release and Signing Guide

Verified against the 4.14.0 release pipeline.

This document describes the repository workflow; it does not grant access to production signing keys or deployment hosts.

## Prerequisites

- Clean Windows checkout and authenticated Git remote
- .NET 8 SDK, PowerShell 7, Inno Setup 6, Windows SDK SignTool
- Code-signing certificate with an accessible private key
- ECDSA P-256 update-metadata private key stored outside Git
- SSH/SCP access to the download host

Never commit a `.pfx`, private key, certificate password, SSH credential, or production configuration.

## Build

```powershell
.\tools\build-release.ps1 `
  -Version X.Y.Z `
  -ReleaseNotes '<verified release notes>' `
  -MinSupportedVersion <oldest-supported-version> `
  -CertificateThumbprint <thumbprint> `
  -AllowUntrustedCertificate
```

The script synchronizes versions across projects, manifests, app settings, Inno Setup, and update JSON; runs the CI gate; publishes self-contained x64 App and Updater files; checks XBF/PRI/startup; signs product binaries; builds and signs the installer; writes SHA-256 and size metadata; signs `latest.json`; and verifies the release.

Always pass release-specific notes; the script's fallback text is not a substitute for reviewed notes. Decide `-ForceUpdate`, `-MinSupportedVersion`, and `rolloutPercent` explicitly. The script preserves the existing rollout percentage rather than selecting one for the release.

`-AllowUntrustedCertificate` is required only for the project's current self-signed certificate. It does not make that certificate trusted. `-ForceSmokeTest` can replace an existing user install and must be used only on a disposable Windows VM. Do not use the build script's `-Upload` switch before the verified source and metadata have merged; deploy the immutable artifacts separately after tagging.

For 4.14.0, the release gate must preserve the complete 4.13 permission/recovery matrix and additionally exercise the 15-tool context cap, deferred catalog promotion, official-provider strict schemas, compatible-provider fallbacks, structured tool failures, research partial success and private-address blocking, content-free quality metrics, and reopen validation for generated XLSX, DOCX, PDF, SVG, and PNG files. Manually confirm that **Create & Research** opens from the expanded sidebar, compact sidebar, composer, and command palette and that completed workflows expose normal open-file/open-folder actions. Do not record a final test count until the release gate has completed.

## Release Files

```text
TLAHStudio.Installer/output/TLAHStudioSetup-X.Y.Z.exe
TLAHStudio.Installer/latest.json
TLAHStudio.Installer/latest.json.sig
TLAHStudio.Installer/latest-X.Y.Z.json.sig
release-notes-X.Y.Z.md
```

The build script generates the installer and signatures; the reviewed release-notes file is maintained with the release change. Generated binaries are intentionally ignored by Git, while the versioned manifest signature and release notes are committed with the matching metadata.

## Verify

```powershell
.\tools\verify-release.ps1 `
  -Version X.Y.Z `
  -AllowUntrustedAuthenticode
```

Verification checks release metadata, manifest signature, installer SHA-256 and size, Authenticode presence, and optional smoke-install behavior. It does not independently inspect every project, application manifest, README, or changelog version anchor. Before committing, search for the previous version across those files and confirm that `latest.json.sig` and `latest-X.Y.Z.json.sig` are byte-identical.

```powershell
rg -n "<previous-version>" `
  CHANGELOG.md README.md README-CN.md CLAUDE.md `
  TLAHStudio.App TLAHStudio.Core TLAHStudio.Data `
  TLAHStudio.Updater TLAHStudio.Installer docs `
  THIRD-PARTY-NOTICES.md .github/ISSUE_TEMPLATE

$legacy = Get-FileHash .\TLAHStudio.Installer\latest.json.sig -Algorithm SHA256
$versioned = Get-FileHash .\TLAHStudio.Installer\latest-X.Y.Z.json.sig -Algorithm SHA256
if ($legacy.Hash -ne $versioned.Hash) { throw "Manifest signatures differ." }
```

When allowing an untrusted self-signed chain, also compare the installer's signer thumbprint with the expected release identity; do not treat every non-`Valid` Authenticode state as equivalent to an untrusted root. Do not publish an installer rebuilt after the release metadata was committed.

## GitHub Release

1. Merge the exact verified source and metadata state into `main`.
2. Create annotated tag `vX.Y.Z` at that commit.
3. Publish a non-draft, non-prerelease GitHub Release.
4. Upload the installer and `latest-X.Y.Z.json.sig`, using `release-notes-X.Y.Z.md` as the reviewed release body.
5. Compare GitHub's asset digest with the local installer SHA-256.

## Download Deployment

```powershell
.\tools\deploy.ps1 -Server <ssh-user>@<download-host>
```

Deployment re-verifies the local release, uploads files with an `.uploading` suffix, then promotes the installer, versioned signature, legacy signature, and `latest.json` atomically. The manifest is promoted last so clients never observe metadata for a missing installer.

After deployment, verify:

- public `latest.json` matches the committed bytes;
- public versioned signature matches the committed signature;
- installer HTTP status and `Content-Length` match the manifest;
- server or downloaded installer SHA-256 matches the manifest;
- GitHub tag and Release target the same `main` commit.

## Trust Model

| Layer | Protects | Current state |
|---|---|---|
| ECDSA P-256 | Update metadata authenticity | Embedded public-key verification |
| SHA-256 | Installer content integrity | Digest stored in signed metadata |
| Authenticode | Executable publisher signature | Signed with a self-signed project certificate; Windows may warn |

Production signing identity details may be documented in release audit records, but private material must remain outside the repository.
