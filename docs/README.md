# TLAH Studio Documentation

This directory separates current operational guidance from historical design records. The code, automated tests, and current release metadata take precedence when an older plan conflicts with the product.

## Current Guides

| Guide | Scope | Verified against |
|---|---|---|
| [Architecture](./ARCHITECTURE.md) | Runtime, persistence, permissions, recovery, tools, MCP, and update topology | 4.13.0 |
| [Development](./DEVELOPMENT.md) | Environment, commands, conventions, permission/recovery testing, and CI | 4.13.0 |
| [Release and signing](./RELEASING.md) | Version sync, CI, Authenticode, manifest signing, and deployment | 4.13.0 |
| [Privacy and data flows](./PRIVACY.md) | Local storage, external endpoints, permission boundaries, export, and deletion | 4.13.0 |
| [Security policy](../SECURITY.md) | Supported versions and private reporting | Current stable |
| [Contributing](../CONTRIBUTING.md) | Contribution workflow and quality gates | Current stable |

## Roadmap

The following documents describe planned directions, not committed release dates or current capabilities:

- [5.0 orchestration](./TLAH_5_0_PHASE2_ORCHESTRATION.md)
- [5.0 roadmap](./TLAH_5_0_ROADMAP.md)
- [5.1 platform](./TLAH_5_1_PHASE3_PLATFORM.md)

## Historical Design Records

These files explain how major systems evolved. They may contain completed checklists, old baselines, or implementation sketches and should not be used as current setup instructions.

| Record | Historical topic |
|---|---|
| `TLAH_3_0_AGENT_DEVELOPMENT_PLAN.md` | Agent runtime extraction and initial GA plan |
| `TLAH_3_3_STABILITY_AND_TOOL_LIFECYCLE_PLAN.md` | Tool lifecycle and safety pipeline |
| `TLAH_4_0_AGENT_PLATFORM_PLAN.md` | Persistent tasks and platform services |
| `TLAH_4_1_1_TOOL_CAPABILITY_PLAN.md` | File, code, and web tool expansion |
| `TLAH_4_8_PHASE0_FOUNDATION.md` | 4.8 foundation work |
| `TLAH_4_9_PHASE1_AUTONOMY.md` | Autonomy and permission model |
| `TLAH_4_9_4_UI_OVERHAUL.md` | Earlier UI overhaul design |
| `TLAH_4_9_7_RELEASE_AUDIT.md` | 4.9.7 release audit |
| `TLAH_4_9_8_RELEASE_AUDIT.md` | 4.9.8 release audit |

## Documentation Rules

- Use repository-relative paths and replace personal machine paths with placeholders.
- Put current behavior in the guides above; keep milestone narratives in historical records.
- State the version or commit used to verify technical claims.
- Update English and Chinese READMEs together when changing public product facts.
- Never place credentials, private keys, production SSH details, or user data in documentation.
