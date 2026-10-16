# Third-party components

| Component | Version | Licence | Used for |
|-----------|---------|---------|----------|
| [Markdig](https://github.com/xoofx/markdig) | 1.3.2 | BSD-2-Clause | Markdown to HTML conversion |
| [highlight.js](https://github.com/highlightjs/highlight.js) | 11.11.1 | BSD-3-Clause | Syntax highlighting in code blocks (`assets/highlight.min.js`, and `assets/hljs.css`, which is the GitHub and GitHub Dark themes merged by `tools/make_hljs_css.py` from the verbatim originals in `tools/hljs-themes/`) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 1.0.4191.47 | Microsoft Software Licence Terms | Embedded browser control that renders the page |

## Build-time only (not part of the shipped product)

| Tool | Version | Licence | Note |
|------|---------|---------|------|
| [WiX Toolset](https://wixtoolset.org/) | 5.0.2 | MS-RL | Builds the MSI. MS-RL covers WiX's own source; the installer it produces is yours. WiX v6 and later add a paid "Open Source Maintenance Fee" — v5 deliberately avoids that. |
| [xUnit](https://xunit.net/) | 2.9.2 | Apache-2.0 | Unit tests in `tests/MdViewer.Tests`. |

## A note on WebView2

Markdig and highlight.js are permissive open-source licences (BSD), so they place no
practical restriction on commercial use or redistribution.

The WebView2 SDK is *not* an open-source licence — it is Microsoft's own licence terms.
It does grant royalty-free rights to use and distribute WebView2 with an application,
including commercial applications, and the runtime itself ships with Windows 11. If this
project is ever redistributed commercially, that licence text is the one to read:
<https://aka.ms/WebView2LicenseTerms>.
