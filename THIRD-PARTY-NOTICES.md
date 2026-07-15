# Third-Party Notices

TLAH Studio includes or depends on third-party software. Each component remains governed by its own license; the repository's proprietary license does not replace those terms.

This inventory covers the principal direct build and runtime dependencies in version 4.13.0. Transitive NuGet packages and native runtime files may add further notices. The authoritative license is the one shipped with the relevant package or upstream source.

| Component | Version | Project | License / terms |
|---|---:|---|---|
| .NET and Microsoft.Extensions | .NET 8 / 8.x | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT and component-specific notices |
| Windows App SDK | 2.1.3 | [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | MIT and Microsoft notices |
| CommunityToolkit.Mvvm | 8.4.0 | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) | MIT |
| CommunityToolkit WinUI Markdown | 7.1.2 | [CommunityToolkit/WindowsCommunityToolkit](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | MIT |
| Entity Framework Core | 8.0.28 | [dotnet/efcore](https://github.com/dotnet/efcore) | MIT |
| SQLite | bundled | [sqlite.org](https://www.sqlite.org/copyright.html) | Public domain |
| SQLitePCLRaw | 3.0.3 | [ericsink/SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) | Apache-2.0 |
| xUnit.net | 2.9.3; runner 3.1.5 | [xunit/xunit](https://github.com/xunit/xunit) | Apache-2.0 |
| coverlet | 10.0.1 | [coverlet-coverage/coverlet](https://github.com/coverlet-coverage/coverlet) | MIT |
| ColorCode.WinUI | 2.0.15 | [CommunityToolkit/ColorCode-Universal](https://github.com/CommunityToolkit/ColorCode-Universal) | MIT |
| TextMateSharp | 2.0.4 | [danipen/TextMateSharp](https://github.com/danipen/TextMateSharp) | MIT |
| Inno Setup | 6 | [jrsoftware.org](https://jrsoftware.org/isinfo.php) | Inno Setup license |

Bundled TextMate grammar files under `TLAHStudio.App/Assets/grammars/` may originate from language-specific upstream grammar projects. Their copyright and license headers, where present, must be preserved. Contributors adding a grammar, icon, font, copied algorithm, or other third-party asset must record its source, version/commit, and license in this file.

If you believe a notice is missing or inaccurate, open an issue with the component name, upstream URL, version, and supporting license information.
