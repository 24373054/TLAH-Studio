# Release and Signing Guide

Verified against the 4.12.0 release pipeline.

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
  -CertificateThumbprint <thumbprint> `
  -AllowUntrustedCertificate
```

The script synchronizes versions across projects, manifests, app settings, Inno Setup, and update JSON; runs the CI gate; publishes self-contained x64 App and Updater files; checks XBF/PRI/startup; signs product binaries; builds and signs the installer; writes SHA-256 and size metadata; signs `latest.json`; and verifies the release.

`-AllowUntrustedCertificate` is required only for the project's current self-signed certificate. It does not make that certificate trusted. `-ForceSmokeTest` can replace an existing user install and must be used only on a disposable Windows VM.

## Generated Artifacts

```text
TLAHStudio.Installer/output/TLAHStudioSetup-X.Y.Z.exe
TLAHStudio.Installer/latest.json
TLAHStudio.Installer/latest.json.sig
TLAHStudio.Installer/latest-X.Y.Z.json.sig
```

Generated binaries are intentionally ignored by Git; the versioned manifest signature is committed with the matching release metadata.

## Verify

```powershell
.\tools\verify-release.ps1 `
  -Version X.Y.Z `
  -AllowUntrustedAuthenticode
```

Verification checks version consistency, manifest signature, installer SHA-256 and size, Authenticode presence, and optional smoke-install behavior. Do not publish an installer rebuilt after the release metadata was committed.

## GitHub Release

1. Merge the exact verified source and metadata state into `main`.
2. Create annotated tag `vX.Y.Z` at that commit.
3. Publish a non-draft, non-prerelease GitHub Release.
4. Upload the installer and `latest-X.Y.Z.json.sig`.
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
