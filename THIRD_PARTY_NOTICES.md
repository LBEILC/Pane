# Third-party notices

Pane's license is in `LICENSE`. Third-party components retain their own licenses.
The installer places the unmodified license and notice texts in `ThirdParty/`
next to the application. In the source tree these files are in
[src/Pane.App/ThirdParty](src/Pane.App/ThirdParty/dotnet-LICENSE.txt).

This inventory describes the x64 NativeAOT installer, including its maintenance
helper. Versions come from the resolved NuGet packages, not the SDK product name.

| Component | Version | License and source | Included license / notices (under `ThirdParty/`) |
| --- | --- | --- | --- |
| .NET runtime, including runtime code statically linked by NativeAOT into Pane.App, Pane.Setup and Pane.Maintenance | 10.0.13 | MIT and upstream third-party notices; [dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.13) | `dotnet-LICENSE.txt`, `dotnet-NOTICES.txt`, `nativeaot-NOTICES.txt` |
| System.Formats.Nrbf, statically linked by NativeAOT into Pane.App for reading NRBF gesture imports | 10.0.0 | MIT and upstream third-party notices; [dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.0), [NuGet](https://www.nuget.org/packages/System.Formats.Nrbf/10.0.0) | `dotnet-LICENSE.txt`, `dotnet-NOTICES.txt` |
| Visual C++ runtime statically linked with `/MT`, and C++ standard library code in the native modules | MSVC toolset 14.44.35207 (VS 2022 17.14) | Microsoft runtime / Build Tools terms; STL Apache-2.0 WITH LLVM-exception; [Microsoft license directory](https://visualstudio.microsoft.com/license-terms/), [STL](https://github.com/microsoft/STL/tree/vs-2022-17.14) | `VisualStudio-BuildTools-LICENSE.docx`, `VisualCpp-Runtime-LICENSE.docx` (originals; corresponding `.txt` files are text extractions), `MSVC-STL-LICENSE.txt` |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | Microsoft Windows App SDK binary license; [NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.9) | `WindowsAppSDK-LICENSE.txt`, `WinUI-NOTICE.txt` |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | Microsoft Windows App SDK binary license; [NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.9) | `WindowsAppSDK-LICENSE.txt` |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | Microsoft Windows App SDK binary license; [NuGet](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Foundation/2.3.12) | `WindowsAppSDK-LICENSE.txt` |
| Microsoft.Web.WebView2 SDK loader and managed bridge (not the Edge WebView2 browser runtime) | 1.0.3719.77 | BSD-3-Clause and upstream notices; [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77) | `WebView2-LICENSE.txt`, `WebView2-NOTICE.txt` |
| Lucide icons, including Feather-derived icons; selected paths embedded in the application | lucide-static 1.60.0 | ISC / MIT; [Lucide](https://github.com/lucide-icons/lucide) | `Lucide-LICENSE.txt` |
| Unicode Unihan readings, transformed into the bundled pinyin table | 17.0.0 | Unicode License V3; [Unihan data](https://www.unicode.org/Public/17.0.0/ucd/Unihan.zip) | `Unicode-LICENSE.txt` (also `LauncherData/Unicode-LICENSE.txt`) |

Microsoft.WindowsAppSDK.Base 2.0.4 and the Windows SDK build tools are build-time
dependencies. They are not separately shipped runtime components. The upstream
Base notices are retained as `WindowsAppSDK-Base-NOTICE.txt` to preserve the SDK's
notice chain. Upstream notice files describe their complete distributions; their
entries do not imply that all listed build tools are in this installer.

The installed payload includes WinUI and composition/input DLLs, Windows App
Runtime / resource DLLs and metadata, WebView2Loader.dll and
Microsoft.Web.WebView2.Core.dll, plus the statically linked .NET runtime and the
two data/art components above. It does not bundle Everything, an Edge browser
runtime, or another ChatGPT client. The authentication implementation follows
the public protocol; no copied third-party client implementation was identified.

## License compatibility status

MIT, ISC and BSD-3-Clause permit use with GPL-3.0; Unicode License V3 likewise
permits modification and redistribution with its notice retained. These notices
do not relicense Microsoft's distributed binaries.
The STL's Apache-2.0 WITH LLVM-exception is GPL-3.0-compatible. Microsoft's
compiler runtime remains under its own terms; compiler runtime code is considered
under GPL-3.0's System Libraries definition, not relicensed as GPL source.

The actual Windows App SDK NuGet packages include Microsoft's binary license,
even though the public Windows App SDK source repository has an MIT license.
Sections 3(b)(ii), 3(c)(ii), and 4 impose additional distribution/use conditions.
Pane is therefore distributed under GPL-3.0 **with an additional permission under
section 7** (see `LICENSE-EXCEPTION.md`) that allows conveying Pane combined with
the Windows App SDK, the Microsoft Visual C++ runtime libraries, the .NET runtime
and the Microsoft Edge WebView2 SDK. Those Microsoft components remain under their
own license terms listed above; they are not relicensed by Pane.
