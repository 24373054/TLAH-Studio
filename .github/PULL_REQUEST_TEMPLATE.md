## Summary

<!-- What changed, and why? -->

## User impact

<!-- Describe the visible behavior, compatibility, data, security, or performance impact. -->

## Root cause

<!-- Required for fixes. Remove this section for changes without a defect. -->

## Validation

- [ ] `dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release`
- [ ] `.\tools\ci.ps1 -Configuration Release -Platform x64`
- [ ] Manual WinUI validation completed where relevant
- [ ] Light/dark, maximized/narrow, keyboard, and reduced-motion states considered

## Screenshots

<!-- Add before/after images for visible UI changes. Remove sensitive prompts, paths, keys, and user data. -->

## Risk and rollback

<!-- What can fail, and how can the change be reverted or disabled? -->

## Checklist

- [ ] The change is focused and contains no unrelated formatting/generated files.
- [ ] Tests cover new logic or the regression where practical.
- [ ] Documentation and release notes are updated when behavior changes.
- [ ] No credentials, private data, local databases, logs, or build artifacts are included.
- [ ] I have read `CONTRIBUTING.md`, `SECURITY.md`, and `LICENSE`.
