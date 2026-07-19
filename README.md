# Blinkered for Windows

Blinkered is a locked-down, managed browser for focused learning on shared and family devices. This is the
Windows client, built on [Safe Exam Browser](https://safeexambrowser.org/) (SEB) with Chromium (via CefSharp) as
the integrated browser engine.

## Attribution & licensing

Blinkered is a **Larger Work** (MPL 2.0 §3.3) built on **Safe Exam Browser**, © ETH Zürich, IT Services
(<https://github.com/SafeExamBrowser/seb-win-refactoring>).

- **Safe Exam Browser's own source files remain licensed under the Mozilla Public License 2.0** (see
  [`LICENSE.txt`](LICENSE.txt)). Files that originate from SEB keep their MPL 2.0 headers.
- **Original Blinkered files are proprietary** — © Alto, all rights reserved — and are not covered by the MPL.
  Each carries a proprietary header.

The MPL 2.0 license text ships with the installed product and is shown during installation.

## Requirements

Installed automatically by the setup bundle; only needed manually with the bare MSI:

- .NET Framework 4.8 Runtime — <https://dotnet.microsoft.com/download/dotnet-framework/net48>
- Visual C++ 2015–2022 Redistributable — <https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist>

Minimum OS: Windows 10 version 1803 (inherited from SEB).

## Build

Open `SafeExamBrowser.sln` in Visual Studio 2022 (or build with MSBuild), platform **x64**. The installer
(`SetupBundle.exe` + MSI) is produced by the `Setup` / `SetupBundle` projects via WiX v3.14. Auto-update signing
is gated behind `$(SignThumbprint)`; unsigned builds are produced by default.

> **Note:** builds from this repository are for development and testing. The namespaces and project folders retain
> the upstream `SafeExamBrowser.*` names by design — only the user-facing branding is Blinkered.
