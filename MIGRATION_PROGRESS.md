# mRemoteNG Cross-Platform Migration — Progress Tracker

> **Instructions for developers:**
> - Update status: `[ ]` = todo, `[~]` = in progress, `[x]` = done, `[!]` = blocked
> - Add your GitHub handle and date when starting a task: `[~] Task — @username 2026-03-10`
> - Add notes under tasks using `>` blockquote
> - Run `grep -r "\[~\]" MIGRATION_PROGRESS.md` to see active tasks
> - Each PR should reference the task ID (e.g. "Implements P1-1.1.1")

**Last updated:** 2026-03-10
**Current Phase:** Phase 1 — Foundation & Abstraction Layer
**Branch:** `claude/analyze-cross-platform-portability-FndER`

---

## Summary Dashboard

| Phase | Tasks | Done | In Progress | Blocked | % Complete |
|-------|-------|------|-------------|---------|------------|
| Phase 1 — Foundation | 24 | 13 | 0 | 0 | 54% |
| Phase 2 — Avalonia UI | 38 | 2 | 0 | 0 | 5% |
| Phase 3 — Protocols | 22 | 0 | 0 | 0 | 0% |
| Phase 4 — Packaging | 16 | 1 | 0 | 0 | 6% |
| **TOTAL** | **100** | **16** | **0** | **0** | **16%** |

---

## PHASE 1 — Foundation & Abstraction Layer

### Milestone 1 Goal: Project compiles on Linux and macOS without errors.

---

#### 1.1 Project Structure

- [ ] **P1-1.1.1** — Create `mRemoteNG.Core` project (`net10.0`)
  - Move domain logic, models, config serializers out of main project
  - Files to move: `App/Info/`, `App/Runtime.cs`, `Config/`, `Security/`, `Tree/`, `Container/`, `Credential/`
  - PR: — *(next: move source files in Phase 1 loop continuation)*

- [x] **P1-1.1.2** — Create `mRemoteNG.Platform` abstraction project (`net10.0`) — @automated 2026-03-10
  - Interfaces created: `IClipboardService`, `IWindowService`, `IProcessService`, `ISettingsProvider`, `ICryptoProvider`, `ISystemTrayService`, `INotificationService`
  - `PlatformServiceFactory` with OS detection and reflection-based registrar loading
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.1.3** — Create `mRemoteNG.Platform.Windows` project (`net10.0-windows`) — @automated 2026-03-10
  - `WindowsClipboardService` — WinForms clipboard
  - `WindowsWindowService` — Win32 P/Invoke (user32.dll)
  - `WindowsProcessService` — Job Object child tracking
  - `WindowsSettingsProvider` — XML to %APPDATA%\mRemoteNG\
  - `DpapiCryptoProvider` — DPAPI encryption
  - `WindowsNotificationService`, `WindowsSystemTrayService` stubs
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.1.4** — Create `mRemoteNG.Platform.Linux` project (`net10.0`) — @automated 2026-03-10
  - `LinuxClipboardService` — xclip/wl-clipboard auto-detection
  - `LinuxProcessService` — AppDomain.ProcessExit + prctl notes
  - `LinuxSettingsProvider` — XDG config dir (~/.config/mRemoteNG/)
  - `LinuxCryptoProvider` — AES-256-GCM with keyfile
  - `LinuxNotificationService` — notify-send
  - `NullWindowService` — Avalonia handles windows
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.1.5** — Create `mRemoteNG.Platform.Mac` project (`net10.0`) — @automated 2026-03-10
  - `MacClipboardService` — pbcopy/pbpaste
  - `MacProcessService` — AppDomain.ProcessExit
  - `MacSettingsProvider` — ~/Library/Application Support/mRemoteNG/
  - `MacCryptoProvider` — AES-256-GCM with keyfile
  - `MacNotificationService` — osascript
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.1.6** — Update `mRemoteNG.sln` to include all new projects — @automated 2026-03-10
  - Added: Platform, Platform.Windows, Platform.Linux, Platform.Mac, Avalonia
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 1.2 Settings System Migration

- [~] **P1-1.2.1** — Remove `#if PORTABLE` gates from `PortableSettingsProvider.cs`
  - Make file-based settings unconditionally available
  - Platform-specific path: Linux `~/.config/mRemoteNG/`, macOS `~/Library/Application Support/mRemoteNG/`, Windows `%APPDATA%\mRemoteNG\`
  - PR: —

- [ ] **P1-1.2.2** — Create `SettingsMigrationHelper.cs`
  - Detect first run after migration from registry
  - Import registry settings to XML on first run (Windows only)
  - PR: —

- [ ] **P1-1.2.3** — Refactor 10 registry settings page classes
  - Files: `OptRegistry*.cs`, `RegistryLoader.cs`, `RegistrySettingsLoader.cs`
  - Create `ISettingsPage` interface
  - Each page reads from `ISettingsProvider`
  - PR: —

- [ ] **P1-1.2.4** — Create `IPuttySessionsProvider` + implementations
  - `WindowsPuttySessionsProvider` — registry (Windows)
  - `FilePuttySessionsProvider` — `~/.ssh/config` (Linux/macOS)
  - PR: —

---

#### 1.3 Encryption Migration

- [x] **P1-1.3.1** — Create `ICryptoProvider` interface — @automated 2026-03-10
  - File: `mRemoteNG.Platform/Security/ICryptoProvider.cs`
  - Methods: `Protect`, `Unprotect`, `CanDecrypt`
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.3.2** — Create `DpapiCryptoProvider` (Windows) — @automated 2026-03-10
  - File: `mRemoteNG.Platform.Windows/Security/DpapiCryptoProvider.cs`
  - Wraps `System.Security.Cryptography.ProtectedData` with entropy
  - Ciphertext prefix: `DPAPI:`
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.3.3** — Create `AesGcmCryptoProvider` (cross-platform) — @automated 2026-03-10
  - File: `mRemoteNG.Platform/Security/AesGcmCryptoProvider.cs`
  - BouncyCastle AES-256-GCM, nonce per encryption, PBKDF2 key derivation
  - Ciphertext prefix: `AESGCM:`, used by Linux & macOS providers
  - PR: claude/analyze-cross-platform-portability-FndER

- [ ] **P1-1.3.4** — Update `SecureXmlHelper` to use `ICryptoProvider`
  - Replace direct DPAPI calls with injected provider
  - PR: —

---

#### 1.4 Win32 P/Invoke Abstraction

- [x] **P1-1.4.1** — Create `IWindowService` interface — @automated 2026-03-10
  - File: `mRemoteNG.Platform/IWindowService.cs`
  - Methods: MoveWindow, SetForeground, IsMinimized, PostMessage, SendMessage, ShowWindow, SetWindowPosition
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.4.2** — Create `IClipboardService` interface — @automated 2026-03-10
  - File: `mRemoteNG.Platform/IClipboardService.cs`
  - Methods: GetText, SetText, Clear, ContainsText
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.4.3** — Implement Windows services — @automated 2026-03-10
  - `WindowsWindowService.cs` — user32.dll P/Invoke (MoveWindow, SetForegroundWindow, etc.)
  - `WindowsClipboardService.cs` — WinForms Clipboard
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.4.4** — Implement Linux clipboard service — @automated 2026-03-10
  - `LinuxClipboardService.cs` — detects Wayland vs X11, uses wl-copy/xclip
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.4.5** — Implement macOS clipboard service — @automated 2026-03-10
  - `MacClipboardService.cs` — pbcopy/pbpaste subprocess
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 1.5 Dependency Injection

- [ ] **P1-1.5.1** — Add `Microsoft.Extensions.DependencyInjection` to `mRemoteNG.Core`
  - Wire up service registrations per platform
  - `PlatformServiceFactory.Create()` returns correct implementations
  - PR: —

- [ ] **P1-1.5.2** — Update `App/Startup.cs` to use DI container
  - Boot sequence with DI resolution
  - PR: —

---

#### 1.6 Compilation Gate

- [ ] **P1-1.6.1** — Change main project TFM from `net10.0-windows10.0.26100.0` to `net10.0`
  - Update `mRemoteNG.csproj`
  - Fix all compilation errors from removed Windows-only APIs
  - PR: —

- [ ] **P1-1.6.2** — Verify project builds on Ubuntu 22.04 CI agent
  - Add Linux build job to GitHub Actions
  - PR: —

- [ ] **P1-1.6.3** — Verify project builds on macOS 14 CI agent
  - Add macOS build job to GitHub Actions
  - PR: —

---

## PHASE 2 — Avalonia UI Migration

### Milestone 2 Goal: Main window renders and is interactive on all 3 platforms.

---

#### 2.1 Project Setup

- [x] **P2-2.1.1** — Create `mRemoteNG.Avalonia` project — @automated 2026-03-10
  - Packages: Avalonia 11.x, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.ReactiveUI, Dock.Avalonia
  - PR: —

- [x] **P2-2.1.2** — Set up ReactiveUI MVVM architecture — @automated 2026-03-10
  - ViewModels directory, Views directory, base classes
  - PR: —

- [ ] **P2-2.1.3** — Set up theming system
  - Dark and light themes, system theme detection
  - PR: —

- [ ] **P2-2.1.4** — Port icon/image resources to Avalonia
  - Convert embedded `.ico`/`.png` to Avalonia resources
  - PR: —

---

#### 2.2 Main Window

- [ ] **P2-2.2.1** — Main window shell (`MainWindow.axaml`)
  - Menu bar, status bar, docking layout host, toolbar
  - PR: —

- [ ] **P2-2.2.2** — Docking system (`DockFactory.cs`)
  - Dock.Avalonia integration
  - 4 panels: Connection Tree, Log, Connection Tabs, Credentials
  - PR: —

- [ ] **P2-2.2.3** — Menu system (File, View, Tools, Help, External Tools)
  - macOS native menu support
  - PR: —

- [ ] **P2-2.2.4** — Toolbar (`MainToolbar.axaml`)
  - Quick connect, action buttons, search
  - PR: —

- [ ] **P2-2.2.5** — Status bar (`MainStatusBar.axaml`)
  - Connection count, status, update notification
  - PR: —

---

#### 2.3 Connection Tree

- [ ] **P2-2.3.1** — TreeView control (`ConnectionTreeView.axaml`)
  - Hierarchical binding, drag-and-drop, right-click menu, inline rename, search
  - PR: —

- [ ] **P2-2.3.2** — Connection tree ViewModel (`ConnectionTreeViewModel.cs`)
  - ObservableCollection, ReactiveCommand, filter observable
  - PR: —

- [ ] **P2-2.3.3** — Connection context menus (`ConnectionContextMenu.axaml`)
  - Connect, Edit, Delete, Move, Export, Set credentials
  - PR: —

---

#### 2.4 Connection Tabs

- [ ] **P2-2.4.1** — Tab host (`ConnectionTabHost.axaml`)
  - TabControl in dock, tab drag-and-drop, context menu
  - PR: —

- [ ] **P2-2.4.2** — Embedded connection view (`EmbeddedConnectionView.axaml`)
  - Native window embedding surface (per-platform)
  - Fullscreen toggle, toolbar overlay
  - PR: —

---

#### 2.5 Options Dialog

- [ ] **P2-2.5.1** — Options window shell (`OptionsWindow.axaml`)
  - Category tree + settings page area
  - PR: —

- [ ] **P2-2.5.2** — Appearance settings page
  - PR: —

- [ ] **P2-2.5.3** — Connection settings page
  - PR: —

- [ ] **P2-2.5.4** — Tabs settings page
  - PR: —

- [ ] **P2-2.5.5** — Security settings page
  - PR: —

- [ ] **P2-2.5.6** — Advanced settings page
  - PR: —

- [ ] **P2-2.5.7** — Updates settings page
  - PR: —

- [ ] **P2-2.5.8** — Notifications settings page
  - PR: —

- [ ] **P2-2.5.9** — Credentials settings page
  - PR: —

- [ ] **P2-2.5.10** — Theme settings page
  - PR: —

- [ ] **P2-2.5.11** — Protocols settings page
  - PR: —

---

#### 2.6 Dialogs

- [ ] **P2-2.6.1** — About dialog
- [ ] **P2-2.6.2** — Connection add/edit dialog
- [ ] **P2-2.6.3** — Credential manager dialog
- [ ] **P2-2.6.4** — Quick connect dialog
- [ ] **P2-2.6.5** — Port scanner dialog
- [ ] **P2-2.6.6** — SSH file transfer dialog
- [ ] **P2-2.6.7** — Import dialog
- [ ] **P2-2.6.8** — Export dialog
- [ ] **P2-2.6.9** — External tools dialog

---

#### 2.7 System Tray & Splash

- [ ] **P2-2.7.1** — System tray icon (`TrayIconService.cs`)
  - Avalonia TrayIcon, context menu, per-platform behavior
  - PR: —

- [ ] **P2-2.7.2** — Avalonia splash screen (`SplashScreen.axaml`)
  - Replace WPF splash with Avalonia version
  - PR: —

---

## PHASE 3 — Protocol Replacement

### Milestone 3 Goal: SSH, VNC, RDP, and HTTP connections work on Linux and macOS.

---

#### 3.1 SSH (PuTTYNG → SSH.NET)

- [ ] **P3-3.1.1** — Create `SshNetProtocol.cs`
  - SSH.NET integration, auth methods, port forwarding
  - PR: —

- [ ] **P3-3.1.2** — Avalonia terminal emulator
  - VtNetCore or XtermSharp integration
  - Full VT100/xterm emulation
  - PR: —

- [ ] **P3-3.1.3** — SSH session manager
  - Session multiplexing, reconnect, logging
  - PR: —

- [ ] **P3-3.1.4** — SFTP browser view
  - File tree, upload/download with progress
  - PR: —

- [ ] **P3-3.1.5** — SSH config file importer
  - Parse `~/.ssh/config`, import as connections
  - PR: —

---

#### 3.2 Telnet & Rlogin (PuTTYNG → Custom)

- [ ] **P3-3.2.1** — Create `TelnetProtocol.cs`
  - Pure .NET TcpClient, option negotiation
  - PR: —

- [ ] **P3-3.2.2** — Create `RloginProtocol.cs`
  - RFC 1282 implementation
  - PR: —

---

#### 3.3 RDP (MSTSCLib → FreeRDP)

- [ ] **P3-3.3.1** — Evaluate and decide on FreeRDP integration approach
  - Options: FreeRDP-Sharp, subprocess+embed, AwesomeRDP
  - Document decision in ADR (Architecture Decision Record)
  - PR: —

- [ ] **P3-3.3.2** — Create `FreeRdpProtocol.cs`
  - Spawn xfreerdp/wfreerdp subprocess
  - Window embedding in Avalonia surface
  - PR: —

- [ ] **P3-3.3.3** — Keep `WindowsRdpProtocol.cs` (MSTSCLib fallback)
  - Guard with `[SupportedOSPlatform("windows")]`
  - PR: —

- [ ] **P3-3.3.4** — RDP feature parity validation
  - Test: multi-monitor, audio, clipboard, drive redirect, NLA, gateway
  - PR: —

---

#### 3.4 VNC

- [ ] **P3-3.4.1** — Create Avalonia VNC view
  - VncSharpCore + Avalonia canvas renderer
  - Keyboard/mouse forwarding, clipboard sync
  - PR: —

---

#### 3.5 HTTP/HTTPS

- [ ] **P3-3.5.1** — Integrate `Avalonia.WebView`
  - WebView2 on Windows, WebKitGtk on Linux, WKWebView on macOS
  - Address bar, navigation, cert handling
  - PR: —

---

#### 3.6 PowerShell & Serial

- [ ] **P3-3.6.1** — Cross-platform PowerShell protocol (`pwsh`)
  - PR: —

- [ ] **P3-3.6.2** — Cross-platform serial port protocol
  - `System.IO.Ports`, `/dev/tty*` on Linux/macOS
  - PR: —

---

#### 3.7 External App Protocols

- [ ] **P3-3.7.1** — Generic external app protocol
  - AnyDesk, ARD, custom external tools
  - PR: —

---

## PHASE 4 — Integration, Polish & Packaging

### Milestone 4 Goal: Installable packages available for all 3 platforms.

---

#### 4.1 Testing

- [ ] **P4-4.1.1** — Update all test projects to `net10.0`
- [ ] **P4-4.1.2** — Integration tests (SSH, VNC, settings round-trip)
- [ ] **P4-4.1.3** — UI automation tests (Avalonia headless)

---

#### 4.2 Linux Packaging

- [ ] **P4-4.2.1** — AppImage packaging
- [ ] **P4-4.2.2** — Flatpak manifest
- [ ] **P4-4.2.3** — Snap packaging
- [ ] **P4-4.2.4** — .deb package

---

#### 4.3 macOS Packaging

- [ ] **P4-4.3.1** — `.app` bundle + code signing
- [ ] **P4-4.3.2** — DMG installer
- [ ] **P4-4.3.3** — Homebrew cask

---

#### 4.4 CI/CD

- [x] **P4-4.4.1** — Multi-platform GitHub Actions matrix — @automated 2026-03-10
- [ ] **P4-4.4.2** — Nightly builds for all platforms
- [ ] **P4-4.4.3** — Code signing pipeline

---

#### 4.5 Documentation

- [ ] **P4-4.5.1** — Update README with cross-platform install instructions
- [ ] **P4-4.5.2** — User migration guide (from Windows registry)
- [ ] **P4-4.5.3** — Developer contributing guide (cross-platform)

---

## Architecture Decision Records

| ADR | Status | Decision |
|-----|--------|---------|
| ADR-001 | DECIDED | Use Avalonia UI 11.x as cross-platform UI framework |
| ADR-002 | DECIDED | Use ReactiveUI for MVVM pattern |
| ADR-003 | DECIDED | Use Dock.Avalonia for docking panel layout |
| ADR-004 | DECIDED | Use SSH.NET to replace PuTTYNG.exe |
| ADR-005 | DECIDED | Use BouncyCastle AES-256-GCM to replace DPAPI |
| ADR-006 | PENDING | RDP implementation: FreeRDP subprocess vs FreeRDP-Sharp bindings |
| ADR-007 | DECIDED | Use `Microsoft.Extensions.DependencyInjection` for IoC |
| ADR-008 | PENDING | Terminal emulator: VtNetCore vs XtermSharp vs custom |
| ADR-009 | DECIDED | Keep MSTSCLib COM as Windows-only RDP fallback |
| ADR-010 | PENDING | Linux clipboard: xclip vs wl-clipboard detection |

---

## Blocked Items Log

_No blocked items yet._

---

## Changelog

| Date | Change | Author |
|------|--------|--------|
| 2026-03-10 | Initial migration plan and progress tracker created | automated |
| 2026-03-10 | Phase 1 implementation started | automated |
| 2026-03-10 | P1-1.1.2 through P1-1.1.6: All 5 new projects created and added to solution | automated |
| 2026-03-10 | P1-1.3.1–1.3.3: ICryptoProvider, DpapiCryptoProvider, AesGcmCryptoProvider implemented | automated |
| 2026-03-10 | P1-1.4.1–1.4.5: IWindowService, IClipboardService + all 3 platform impls | automated |
| 2026-03-10 | P2-2.1.1, P2-2.1.2: Avalonia project skeleton + ReactiveUI MVVM structure | automated |
| 2026-03-10 | P4-4.4.1: GitHub Actions cross-platform CI/CD pipeline created | automated |

---

## Notes

- When updating this file in a PR, always update the **Summary Dashboard** counts.
- Use `[!]` for blocked items and add an entry to the **Blocked Items Log** with the reason.
- Label your PRs with `migration-phase-1`, `migration-phase-2`, etc. for tracking.
- Breaking changes to the connection XML schema **must** maintain backward compatibility.
