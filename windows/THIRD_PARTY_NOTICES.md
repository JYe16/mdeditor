# Third-party notices (Windows build)

The preview page bundles the libraries listed in the repository's top-level `THIRD_PARTY_NOTICES.md`
(marked, DOMPurify, highlight.js, KaTeX, github-markdown-css). Their full license texts ship in
`preview/vendor/licenses/` next to the application.

The Windows application also redistributes these NuGet packages:

| Package | Version | License |
| --- | --- | --- |
| Microsoft.WindowsAppSDK (including WinUI 3 and the WebView2 SDK) | 2.6.5 | MIT |
| .NET Runtime (self-contained deployment) | 10.0 | MIT |

Test-only packages, not redistributed: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

The installer is built with Inno Setup (https://jrsoftware.org/isinfo.php). It requires the separately distributed
Microsoft Edge WebView2 Runtime, which is covered by Microsoft's own license terms.
