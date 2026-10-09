# What 0.3.3 adds: dependencies, services and tools, and what each costs

Everything Armory 0.3.3 adds is free. One step needs an administrator, and it is optional:
Armory's status on file icons (the badges), once per computer, only on the computers where
someone wants them. Nothing else 0.3.3 adds needs an administrator on any school computer or
student laptop. Checked on 2026-10-09 against the repository at the 0.3.3 version bump.

## For students and lab computers

| What | New in 0.3.3 | Where it comes from | License, cost | Administrator? |
|---|---|---|---|---|
| NuGet packages in the app | none: the app still references only Microsoft.Web.WebView2 1.0.4258.31 (as in 0.3.2) | nuget.org, pinned by `packages.lock.json` | WebView2 SDK: Microsoft's BSD-style license; free | no |
| Windows SDK projection for .NET (Windows notifications) | yes: the app targets `net10.0-windows10.0.17763.0`, so the .NET SDK adds the targeting pack Microsoft.Windows.SDK.NET.Ref 10.0.17763.57, and `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` ship in the app folder | the .NET SDK downloads it from nuget.org | Microsoft's Windows SDK license (https://aka.ms/WinSDKLicenseURL), which lets the projection ship inside apps; WinRT.Runtime is C#/WinRT, MIT; free | no |
| `ArmoryShell.exe` (File Explorer's right-click items) | yes, in the app folder | built from `native/shell/` in this repository | ours; built with the Visual C++ runtime linked in (`/MT`), so there is nothing extra to install or license | no: it runs as the student |
| Right-click items, notifications, `idea-armory:` links | yes | Windows' own static verbs, notification API and URL schemes, under `HKCU` | part of Windows 10 1809 and later and Windows 11; free | no |
| The SolidWorks link | yes, inside `IdeaArmory.exe` | Windows' Running Object Table and COM, talking to the SolidWorks the student already runs | part of Windows; no SolidWorks SDK, no Dassault DLL, no add-in license, nothing registered | no |
| Shared-computer profiles (PINs) | yes | .NET's PBKDF2 and Windows DPAPI | part of .NET and Windows; free | no |
| `ArmoryBadges.dll` and `IDEA-Armory-Badges-Setup` (status on file icons) | yes, **optional** | built from `native/badges/` in this repository; installed by its own setup | ours; static Visual C++ runtime; free | **yes, once per computer, only where someone wants the badges** (Windows reads icon overlay handlers only from `HKLM`). IT can run it silently: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. Skipping it changes nothing else. |
| Microsoft Edge WebView2 Runtime | no (as in 0.3.2) | Windows 11 includes it; most Windows 10 computers have it | free | no (the per-user runtime installs without one) |
| .NET runtime | no (self-contained in the app, as before) | the .NET SDK | MIT; free | no |
| Windows PowerShell 5.1 (the flash drive's scripts) | no | part of Windows 10 and 11 | free | no, except `Show Armory status on file icons.cmd`, which asks Windows for the password (the badges step above) |

## Services

| What | New in 0.3.3 | Cost |
|---|---|---|
| ideabosco.com's Supabase project | no new service: 0.3.3 calls database functions the website already has (among them `armory_break_locks`, from its migration 0234, and `armory_my_app_feedback`, from 0235) | as today; nothing to provision |
| Supabase Storage bucket `armory-feedback-shots` (feedback pictures) | no: the website provisioned it for 0.3.2's Send feedback | as today; nothing to provision |
| GitHub Releases (downloads of the installers, and the upgrade tests' download of v0.1.0 and v0.3.2) | the badges setup and its `.sha256` are two more assets | free; `pina-hash/idea-armory` is public, so downloads need no token |
| GitHub Actions | longer Windows runs (the native build, the 0.3.2 upgrade, SolidWorks open, the badges: roughly 45 to 70 minutes for agent.yml), and a new Linux run, ui.yml (about 6 minutes, only when the window's files change) | free: GitHub-hosted runners cost nothing for a public repository. If the repository were made private, these minutes would count against the account's included minutes (GitHub Free has included 2,000 a month, with a Windows minute counting as two); check GitHub's current billing page before making it private |

## On GitHub's runners only (CI and releases)

These run on GitHub's throwaway machines, never on a school computer. The runner's steps run as
an administrator, which the badges cycle needs; that is GitHub's machine, not a student's.

| What | Used for | License, cost |
|---|---|---|
| Visual Studio's C++ compiler (MSVC) with the Windows SDK, and its ARM64 tools when the image has them | `tools/build-native.ps1`: the badge handlers, the forwarder and the test tools | preinstalled on GitHub's `windows-latest` image; using it there costs nothing and installs nothing |
| Inno Setup 6.3 or later | both setups (`IdeaArmory.iss`, `IdeaArmoryBadges.iss`) | preinstalled on the image (6.7 when last checked); Chocolatey installs it only if missing; Inno Setup's license is free for any use, Chocolatey's client is Apache-2.0 |
| GitHub Actions `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/upload-artifact@v4`, and new `actions/setup-node@v4` | the workflows | MIT; free |
| Node.js 22 and Playwright 1.56.1 with its Chromium (new in ui.yml) | `tools/agent-ui/check-ui.mjs` and `bbox-diff.mjs` | Node.js MIT, Playwright Apache-2.0, Chromium BSD-style; free from npm |
| PowerShell 7 | the workflows and the install cycles | MIT; preinstalled |
| The test projects' packages (xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1, xunit.runner.visualstudio 3.1.1) | the new tests, including the SolidWorks link's fake | the same packages and versions the other test projects already use; Apache-2.0 and MIT; free |

## On a developer's computer only (local checks, never shipped)

| What | Used for | License, cost |
|---|---|---|
| mingw-w64 (`g++-mingw-w64-x86-64`) | `tools/build-native.sh`: an x64 build of the native parts for checks under Wine | GCC (GPL with the runtime library exception) and mingw-w64 (permissive); free from the Linux distribution |
| Wine 9 | running the native parts and their probes on Linux | LGPL; free |
| Python 3 (standard library only) | `tools/agent-icon/make_badges.py`, which redraws the four badge icons | PSF license; free |
| CMake 3.21 or later (optional) | `native/CMakeLists.txt`, another way to build the native parts | BSD-3-Clause; free |
| Node.js 22 and Playwright 1.56.1 | the window tools, as on CI | as above; free |
| Visual Studio Build Tools or Community (optional, on a developer's Windows computer) | running `tools/build-native.ps1` locally instead of on CI | free for academic and open-source use under Microsoft's Visual Studio Community terms; installing it needs that developer's own administrator rights, never a student's |

Installing the Linux tools uses that computer's package manager; none of them touches a school
computer, a student laptop or the release.
