# mRemoteNG Cross-Platform Portability Analysis Report

**Date:** March 10, 2026
**Scope:** Linux and macOS portability assessment
**Target Application:** mRemoteNG — Multi-Remote-Next-Generation
**Repository:** https://github.com/mRemoteNG/mRemoteNG

---

## Executive Summary

mRemoteNG is a **Windows-exclusive** remote connections manager. The application is built on .NET 10 with a Windows-specific target framework (`net10.0-windows10.0.26100.0`), relies on Windows Forms (WinForms) and WPF for its UI, uses COM interop for RDP protocol support, and has 65+ files decorated with `[SupportedOSPlatform("windows")]`. Porting to Linux or macOS is **technically feasible** but requires a **near-complete architectural rewrite** of the UI layer, protocol subsystem, and configuration system.

**Verdict:** Not portable as-is. Estimated effort for a production-quality cross-platform port: **12–24 months** for an experienced team.

---

## 1. Technology Stack

| Component | Value | Cross-Platform? |
|-----------|-------|-----------------|
| Runtime | .NET 10 | Yes (runtime is x-plat) |
| Target Framework | `net10.0-windows10.0.26100.0` | **No — Windows-only TFM** |
| Runtime Identifiers | `win-x64`, `win-arm64` | **No** |
| Primary UI | Windows Forms (WinForms) | **No** |
| Secondary UI | WPF + XAML | **No** |
| Docking Framework | DockPanelSuite (WinForms) | **No** |
| Browser Component | Microsoft WebView2 (Chromium) | **No** |
| Configuration | Windows Registry + XML | Partial |
| Build | MSBuild / .NET SDK | Yes |

---

## 2. Windows-Specific Dependencies — Detailed Breakdown

### 2.1 P/Invoke & Win32 API (`App/NativeMethods.cs`)

The most fundamental blocker. The app directly calls into Windows system DLLs at 65+ call sites.

**`user32.dll` functions used:**
- `AppendMenu`, `CreatePopupMenu` — Context menu manipulation
- `FindWindowEx` — Window handle enumeration
- `GetForegroundWindow`, `SetForegroundWindow` — Focus management
- `GetSystemMenu`, `SetMenuItemBitmaps` — System menu modification
- `InsertMenu`, `SendMessage`, `PostMessage` — Win32 messaging
- `IsIconic`, `MoveWindow`, `ShowWindow` — Window state
- `SetWindowPos`, `SetWindowLong`, `GetWindowLong` — Window geometry
- `WindowFromPoint` — Hit testing
- `SetClipboardViewer`, `ChangeClipboardChain` — Clipboard chain
- Win32 message constants: `WM_CREATE`, `WM_DESTROY`, `WM_ACTIVATE`, `WM_KEYDOWN`, etc.

**`kernel32.dll` functions used:**
- `CloseHandle` — Handle lifecycle
- `CreateJobObject`, `SetInformationJobObject` — Child process job objects (see `Tools/ChildProcessTracker.cs`)

**Impact:** All these must be replaced with platform-abstracted equivalents or eliminated entirely. No direct equivalent on Linux/macOS without a compatibility layer (e.g., Wine — not a real port).

---

### 2.2 Windows Registry (`Tools/WindowsRegistry/`, `Config/Settings/Registry/`)

The application stores nearly all settings in the Windows Registry:

| Registry Hive | Key | Purpose |
|---------------|-----|---------|
| `HKCU\Software\mRemoteNG\` | App settings | User-level appearance, security, connection prefs |
| `HKLM\SOFTWARE\mRemoteNG\` | System policy | Enterprise/admin-enforced settings |
| `HKCU\Software\SimonTatham\PuTTY\` | PuTTY sessions | Import of saved SSH sessions |

**Files affected (10+ source files):**
- `WinRegistry.cs`, `WinRegistryEntry.cs`
- `OptRegistryAppearancePage.cs`, `OptRegistryAdvancedPage.cs`
- `OptRegistryConnectionsPage.cs`, `OptRegistryCredentialsPage.cs`
- `OptRegistrySecurityPage.cs`, `OptRegistryUpdatesPage.cs`
- `RegistryLoader.cs`, `RegistryImporter.cs`
- `PuttySessionsRegistryProvider.cs`

**Mitigation path:** A `PortableSettingsProvider.cs` (XML file-based) already exists and is used in the PORTABLE build configuration. It could be extended to serve as the primary store on Linux/macOS.

---

### 2.3 COM Interop — RDP Protocol (`Connection/Protocol/RDP/`)

RDP (Remote Desktop Protocol) is one of the most-used features. It is implemented via the Microsoft-proprietary `MSTSCLib` ActiveX COM control:

```xml
<COMReference Include="MSTSCLib">
  <WrapperTool>aximp</WrapperTool>
  <Guid>8c11efa1-92c3-11d1-bc1e-00c04fa31489</Guid>
</COMReference>
```

Classes referenced: `MsRdpClient6NotSafeForScripting`, `AdvancedSettings2`, `AxMSTSCLib`.

This COM control is **Windows-only** and ships with Windows or the Remote Desktop Connection client. There is no Linux/macOS equivalent for this COM object.

**Mitigation path:**
- Use **FreeRDP** (open-source, cross-platform RDP library) via its C# bindings (`freerdp-sharp`)
- Or embed `xfreerdp` as an external process (similar to how PuTTY is embedded today)
- RDP-over-WebSocket via Apache Guacamole is another browser-based alternative

---

### 2.4 UI Framework — Windows Forms & WPF

**Windows Forms** is the primary framework with 150+ UI source files under `/UI/`. Key dependencies:
- `System.Windows.Forms.Form` inheritance throughout
- `System.Drawing.Common` for rendering
- `DockPanelSuite` / `WeifenLuo.WinFormsUI.Docking` for layout
- `System.Windows.Extensions` (Windows-specific graphics)
- TaskDialog (Vista+ native Win32 dialog)
- Per-monitor DPI handling: `DPI_Per_Monitor.cs`

**WPF** is also used:
- Project property: `<UseWPF>True</UseWPF>`
- XAML Behaviors: `Microsoft.Xaml.Behaviors.Wpf`

Neither WinForms nor WPF run natively on Linux or macOS.

**Mitigation path (UI frameworks for .NET cross-platform):**
- **Avalonia UI** — Most mature WinForms/WPF replacement; supports Linux and macOS
- **MAUI** — Microsoft's official x-plat UI; desktop on Linux is limited
- **Uno Platform** — Bridges WinUI to other platforms

**Effort:** Very high. All 150+ UI files would need to be redesigned for the new framework.

---

### 2.5 WebView2 — HTTP/HTTPS Protocol (`Connection/Protocol/Http/`)

HTTP and HTTPS connections render inside an embedded browser. The current implementation uses:
- **Microsoft WebView2** (Chromium-based, Windows-only)
- Fallback: `System.Windows.Forms.WebBrowser` (IE-based, Windows-only)

**Mitigation path:**
- **CefSharp** — Chromium Embedded Framework for .NET (Windows/Linux)
- **WebKitGtk** — Linux native
- **Avalonia's built-in web view** (experimental)

---

### 2.6 SSH/Telnet via PuTTYNG (`Connection/Protocol/PuttyBase.cs`)

SSH and Telnet connections are handled by spawning an embedded `PuTTYNG.exe` subprocess (~1MB Windows executable). Communication uses Windows named pipes (`\\.\pipe\mRemoteNGSecretPipe{random}`).

**PuTTY dependencies:**
- Windows PE binary — does not run on Linux/macOS without Wine
- Windows named pipe IPC — Linux has UNIX domain sockets instead
- Registry-based PuTTY session import

**Mitigation path:**
- Use **SSH.NET** library (already a dependency: `Renci.SshNet.Async`) for SSH2 directly
- Embed an Avalonia/platform-native terminal emulator
- Use `libssh2` bindings for a native experience
- On Linux/macOS, use system `ssh` binary as subprocess

---

### 2.7 Active Directory Integration (`Tools/ADhelper.cs`)

Uses `System.DirectoryServices` (LDAP/AD) for discovering computers in Windows domains:
- `DirectoryEntry` class for LDAP operations
- Domain tree browsing
- Importing hosts from AD into the connection tree

**Mitigation path:**
- `System.DirectoryServices` does support LDAP on Linux in .NET 6+ via `ldap3`/OpenLDAP
- Active Directory browsing would be limited without full Kerberos/NTLM support
- This is medium-priority; Linux/macOS users rarely rely on AD browsing

---

### 2.8 Child Process Management (`Tools/ChildProcessTracker.cs`)

Uses Windows **Job Objects** to ensure child processes (embedded terminals, external apps) are killed when the parent exits:
- `CreateJobObject()` — kernel32.dll
- `SetInformationJobObject()` — kernel32.dll
- `AssignProcessToJobObject()` — kernel32.dll

**Mitigation path:**
- Linux: Use `prctl(PR_SET_PDEATHSIG)` via P/Invoke or `libc` binding
- macOS: Use `kqueue`/`POSIX` signals

---

### 2.9 External Windows-Specific Libraries

| Package | Version | Issue |
|---------|---------|-------|
| `Microsoft.Web.WebView2` | 1.0.3800.47 | Windows-only Chromium wrapper |
| `Microsoft-WindowsAPICodePack-Shell` | 1.1.5 | Windows Shell API (Explorer integration) |
| `System.Windows.Extensions` | 10.0.3 | Windows-only graphics extensions |
| `System.Security.Cryptography.ProtectedData` | 10.0.3 | Uses DPAPI (Windows-only encryption) |
| `System.Security.Cryptography.Cng` | 5.0.0 | Cryptography Next Generation (Windows-only) |
| `DockPanelSuite` | 3.1.1 | WinForms-only docking layout |

**Cross-platform-safe libraries (can be kept):**
- `SSH.NET` — Cross-platform SSH
- `VncSharpCore` — Cross-platform VNC protocol
- `BouncyCastle.Cryptography` — Cross-platform crypto
- `Newtonsoft.Json` — Cross-platform JSON
- `Google.Protobuf` — Cross-platform protobuf
- `LiteDB` — Cross-platform embedded DB
- `MySql.Data`, `Microsoft.Data.SqlClient` — Cross-platform DB clients
- `log4net` — Cross-platform logging
- `NUnit` — Cross-platform test framework

---

## 3. Protocol-by-Protocol Portability Matrix

| Protocol | Implementation | Portability | Migration Path |
|----------|---------------|-------------|----------------|
| **RDP** | COM MSTSCLib | **Blocked** | FreeRDP / Guacamole |
| **SSH** | PuTTYNG.exe | **Blocked** | SSH.NET (already available) |
| **Telnet** | PuTTYNG.exe | **Blocked** | System `telnet` or custom impl |
| **VNC** | VncSharpCore | Medium | Library is x-plat; UI layer needs work |
| **HTTP/HTTPS** | WebView2 | **Blocked** | CefSharp / WebKitGtk |
| **PowerShell** | PSRemoting | Medium | `pwsh` runs on Linux/macOS |
| **ARD** | External cmd | Possible | macOS-native |
| **AnyDesk** | External app | Possible | AnyDesk available on Linux |
| **RAW Socket** | .NET Sockets | Easy | Cross-platform |
| **Rlogin** | PuTTYNG.exe | Blocked | Custom impl or `rlogin` binary |
| **WSL** | Windows-only | **N/A** | No Linux equivalent needed |
| **Terminal** | ConsoleControl | Medium | Replace with x-plat terminal emulator |

---

## 4. Configuration System Portability

| Mechanism | Files | Portable? | Notes |
|-----------|-------|-----------|-------|
| Windows Registry | 10+ files | **No** | Requires Windows API |
| PortableSettingsProvider | `PortableSettingsProvider.cs` | **Yes** | XML, app directory |
| XML Connection Files | `XmlConnectionNodeSerializer28.cs` | **Yes** | Pure XML/JSON |
| CSV Connection Files | `CsvConnectionsSerializerMremotengFormat.cs` | **Yes** | Pure CSV |
| DPAPI Encryption | `SecureXmlHelper.cs` | **No** | Windows DPAPI only |
| SQL Server Config | `SqlConnectionsDataProvider.cs` | **Yes** | Cross-platform SQL client |

**Summary:** The XML/CSV file-based storage is already cross-platform. The registry-based path and DPAPI encryption are the blockers.

---

## 5. Effort Estimation by Subsystem

| Subsystem | Effort | Complexity | Notes |
|-----------|--------|------------|-------|
| UI framework migration (WinForms → Avalonia) | Very High | 5/5 | 150+ form files, designer code, docking system |
| RDP protocol replacement | High | 4/5 | Must integrate FreeRDP or alternative |
| SSH/Telnet (remove PuTTYNG) | Medium | 3/5 | SSH.NET already available |
| Registry → file config | Medium | 3/5 | PortableSettingsProvider exists |
| Win32 P/Invoke removal | High | 4/5 | 65+ files, deep integration |
| WebView2 → CefSharp/WebKit | Medium | 3/5 | API differences |
| DPAPI → cross-platform crypto | Low | 2/5 | BouncyCastle available |
| Active Directory | Low | 2/5 | LDAP on Linux works |
| Child process management | Low | 2/5 | prctl() on Linux |
| Installer (Windows MSI → AppImage/dmg) | Low | 2/5 | Separate tooling |
| CI/CD pipeline | Low | 1/5 | Add Linux/macOS build agents |

---

## 6. Recommended Migration Strategy

If porting is desired, a phased approach is recommended:

### Phase 1 — Foundation (3–4 months)
- Change target framework from `net10.0-windows10.0.26100.0` to `net10.0`
- Add `RuntimeInformation.IsOSPlatform()` checks everywhere platform-specific code runs
- Migrate all settings to `PortableSettingsProvider` (deprecate registry)
- Replace DPAPI encryption with BouncyCastle AES-GCM
- Remove `Microsoft-WindowsAPICodePack-Shell`
- Stub out Win32 P/Invoke calls behind platform-detection interfaces

### Phase 2 — UI Migration (6–9 months)
- Select Avalonia UI as the target framework
- Port the main window, connection tree, and options dialogs
- Migrate DockPanelSuite to Avalonia's docking equivalent
- Replace TaskDialog, DPI-awareness code, and system-menu manipulation

### Phase 3 — Protocol Replacement (4–6 months)
- Replace PuTTYNG.exe with SSH.NET for SSH (already partially done)
- Integrate FreeRDP for RDP support on Linux/macOS
- Replace WebView2 with CefSharp or platform-native WebKit
- Implement cross-platform terminal emulator

### Phase 4 — Integration & Polish (2–3 months)
- Testing on Ubuntu, Fedora, macOS Ventura/Sonoma
- Replace `CreateJobObject` with `prctl()` on Linux
- Package for Linux (AppImage, Flatpak, Snap) and macOS (DMG, Homebrew)
- Update CI/CD to build/test on all three platforms

**Total estimated calendar time:** 15–22 months (single team)
**Could be parallelized** by splitting UI and protocol teams.

---

## 7. Alternative Approaches (Instead of a Full Port)

| Approach | Effort | Quality |
|----------|--------|---------|
| **Run under Wine on Linux** | Very Low | Poor — unstable, no RDP COM support in Wine |
| **Guacamole-based web UI** | Medium | Good for RDP/VNC/SSH, no local client |
| **Fork + Avalonia rewrite** | Very High | Best long-term result |
| **New app inspired by mRemoteNG** | High | Clean slate, better architecture |
| **Remote.IT / Rustdesk integration** | Low | Different product scope |

---

## 8. Files Requiring the Most Changes

The following files are the highest-effort items in a portability effort:

| File | Why It's Hard |
|------|---------------|
| `App/NativeMethods.cs` | All Win32 P/Invoke — must be fully replaced |
| `UI/Forms/frmMain.cs` | 1000+ lines WinForms main window |
| `Connection/Protocol/RDP/RdpProtocol.cs` | Deep COM/MSTSCLib integration |
| `Connection/Protocol/PuttyBase.cs` | PuTTYNG.exe spawning + named pipes |
| `Connection/Protocol/Http/Connection.Protocol.HTTPBase.cs` | WebView2 integration |
| `Config/Settings/Registry/RegistryLoader.cs` | Bootstrap registry settings load |
| `Tools/WindowsRegistry/WinRegistry.cs` | Core registry CRUD |
| `Tools/ADhelper.cs` | Windows AD/LDAP directory browsing |
| `Tools/ChildProcessTracker.cs` | Job Object child process management |
| All `*.Designer.cs` in `UI/` | WinForms auto-generated designer code (150+ files) |

---

## 9. Conclusion

| Category | Assessment |
|----------|-----------|
| **Can it run on Linux today?** | No — `net10.0-windows10.0.26100.0` TFM prevents compilation |
| **Can it run on macOS today?** | No — same reason |
| **Is a port feasible?** | Yes, with significant investment |
| **Is a partial port feasible?** | Yes — SSH/VNC/raw socket on Avalonia first |
| **Are there cross-platform foundations?** | Partial — SSH.NET, VncSharpCore, PortableSettingsProvider, LiteDB |
| **Biggest single blocker?** | WinForms UI framework (150+ files) |
| **Second biggest blocker?** | RDP COM interop (MSTSCLib) |
| **Realistic minimum timeline?** | 12 months for a partial port (no RDP), 18–24 for full feature parity |

The codebase is well-structured and uses modern .NET 10, which means the runtime and language are not obstacles. The primary obstacles are the **UI framework**, **RDP COM dependency**, and **pervasive Win32 P/Invoke usage**. A team that has experience with Avalonia UI and FreeRDP integration could make significant progress on the non-RDP protocols (SSH, VNC, Telnet, HTTP) within 6 months, leaving RDP as a longer-term effort.

---

*Report generated by automated codebase analysis. All file references are relative to the repository root.*
