# mRemoteNG Cross-Platform Migration — Progress Tracker

> **Instructions for developers:**
> - Update status: `[ ]` = todo, `[~]` = in progress, `[x]` = done, `[!]` = blocked
> - Add your GitHub handle and date when starting a task: `[~] Task — @username 2026-03-10`
> - Add notes under tasks using `>` blockquote
> - Run `grep -r "\[~\]" MIGRATION_PROGRESS.md` to see active tasks
> - Each PR should reference the task ID (e.g. "Implements P1-1.1.1")

**Last updated:** 2026-10-09 (end of day: features completed, CI green on Linux/macOS/Windows)
**Current Phase:** Phase 4 — packaging verification and signing
**Branch:** `dev`

---

## Status — 2026-10-09 (end of day)

The morning audit found most of the "done" items to be stubs. They have now been implemented and verified.
Verification environment: Linux (.NET 10.0.401, Xvfb) with real servers — OpenSSH sshd, TigerVNC Xvnc,
xrdp + FreeRDP 3.32, inetutils telnetd — plus GitHub Actions on ubuntu/macos/windows-latest.

**Works (verified)**
- Connection files: legacy confCons.xml 2.5–2.8 incl. master passwords and full-file encryption; files written
  are readable by the WinForms app (byte-compatible crypto). Import (8 formats) and export (XML/CSV).
- App: tree editing with inheritance, search, unsaved-changes tracking, persistent options (theme Dark/Light/System,
  startup file, exit/close confirmation, default ports, timeouts), credential manager (encrypted, 0600 files).
- Protocols: SSH (known_hosts verification, SFTP, xterm-256color terminal), Telnet, Rlogin, RAW, VNC (managed RFB
  client), RDP (FreeRDP embedded in the tab on Linux/X11), HTTP/HTTPS (system browser), PowerShell, local shell,
  serial, AnyDesk/external apps.
- Platforms: Linux/macOS/Windows builds and tests green in CI; Linux .deb and AppImage built, installed and run.
- Tests: 611 cross-platform tests (unit + integration against real servers) and 7 headless UI tests.

**Known limitations**
- RDP: not yet run against Windows Server/NLA or on Windows/macOS; macOS uses a separate FreeRDP window;
  enlarging an xrdp session beyond its start size fails in FreeRDP/xrdp at 24/32-bit colour.
- VNC: no Tight/Zlib encodings or proxy support. HTTP is not embedded (no maintained WebView for Avalonia 11).
- Rlogin window resizing; terminal wide (CJK) characters use one cell; IntApp/External Tools not ported.
- Packaging: Flatpak, Snap and macOS DMG/Homebrew scripts have not been run; code signing needs certificates.
- The Windows registry PuTTY session provider is registered but has not been run on Windows.

## Feature Parity with the WinForms App (backlog)

Audit of 2026-10-09 against the legacy `mRemoteNG/` sources. Connection files: every attribute the legacy
2.8 writer emits is read and written back (guarded by `LegacyAttributeContractTests`); settings the new UI
doesn't use yet are kept in the file, not dropped. Gaps, roughly by user impact:

1. **RDP options not applied by the FreeRDP launcher:** fixed resolution/fullscreen, performance flags,
   printers/ports/smart cards, "All"/custom drives, RestrictedAdmin/RCG, auth level, start program, gateway
   password; Hyper-V VM console (VmId/enhanced mode).
2. **External Tools** (tools window/toolbar, variables) — also needed for Pre/Post external apps and IntApp.
3. **External credential/address providers:** Delinea Secret Server, Passwordstate, 1Password, Vault/OpenBao,
   AWS EC2.
4. **Session tab menu:** reconnect, duplicate, rename, send Ctrl+Alt+Del, disconnect others, per-session
   fullscreen/smart-size/view-only, next/previous session shortcuts, reconnect all, reconnect at startup.
5. **Storage:** SQL Server/MySQL backend and multi-user sync; rolling backups; autosave; portable mode.
6. **PuTTY-specific SSH:** saved PuTTY session settings, SSHOptions, SSH tunnel through another connection.
7. **Tools:** Multi-SSH (type into several sessions), Active Directory import, port-scan range + import,
   UltraVNC SingleClick.
8. **Layout:** docking/floating/split panels, multiple named panels, saved layout; tab/frame colours,
   Favorites, environment tags not shown.
9. **Tree:** expand/collapse all, copy hostname, apply inheritance to children, connect with options,
   PuTTY sessions as a live tree root (import only today).
10. **Security UI:** set/change/remove the master password and encryption settings of the open file
    (possible only via Export today).
11. **App:** 24 UI translations (English only now), theme editor and extra themes, log to file, start
    minimised, in-app update download, command-line switches.
12. **Connection dialog:** many stored properties (see 1, 3, 8) have no editor yet.

## Summary Dashboard

| Phase | Tasks | Done | In Progress | Blocked | % Complete |
|-------|-------|------|-------------|---------|------------|
| Phase 1 — Foundation | 24 | 24 | 0 | 0 | 100% |
| Phase 2 — Avalonia UI | 32 | 32 | 0 | 0 | 100% |
| Phase 3 — Protocols | 17 | 16 | 1 | 0 | 94% |
| Phase 4 — Packaging | 16 | 10 | 5 | 1 | 62% |
| **TOTAL** | **89** | **82** | **6** | **1** | **92%** |

---

## PHASE 1 — Foundation & Abstraction Layer

### Milestone 1 Goal: Project compiles on Linux and macOS without errors.

---

#### 1.1 Project Structure

- [x] **P1-1.1.1** — Create `mRemoteNG.Core` project (`net10.0`) — @claude 2026-03-10
  - Full cross-platform domain model extraction complete:
  - `Connection/ConnectionInfo.cs` — 80+ properties, INotifyPropertyChanged, inheritance support (no WinForms attrs)
  - `Connection/ConnectionInfoInheritance.cs` — All boolean inheritance flags
  - `Container/ContainerInfo.cs` — Tree container with child management
  - `Tree/TreeNodeType.cs`, `ConnectionTreeModel.cs`, `NodeSearcher.cs`
  - `Tree/Root/RootNodeInfo.cs`, `RootPuttySessionsNodeInfo.cs`, `RootNodeType.cs`
  - `Security/` — ICryptographyProvider, AeadCryptographyProvider (BouncyCastle AES-GCM), CryptoProviderFactory, Pkcs5S2KeyGenerator
  - `Config/Serializers/Xml/` — XmlConnectionsSerializer + XmlConnectionsDeserializer (v2.5–2.8 compatible)
  - `Config/Connections/ConnectionsService.cs` — High-level load/save service
  - `Config/DataProviders/` — FileDataProvider, FileDataProviderWithRollingBackup
  - `Credential/` — ICredentialRecord, CredentialRecord, ICredentialRepository, ICredentialRepositoryList
  - All enums: ProtocolType, RDP enums, VNC enums, Http RenderingEngine, BlockCipherEngines/Modes, etc.
  - DI registration via `AddMRemoteNgCore()` in bootstrap
  - Builds clean: 0 warnings, 0 errors

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

- [x] **P1-1.2.1** — Remove `#if PORTABLE` gates from `PortableSettingsProvider.cs`
  - Make file-based settings unconditionally available
  - Platform-specific path: Linux `~/.config/mRemoteNG/`, macOS `~/Library/Application Support/mRemoteNG/`, Windows `%APPDATA%\mRemoteNG\`
  - PR: —

- [x] **P1-1.2.2** — Create `SettingsMigrationHelper.cs` — @automated 2026-03-10
  - Detect first run after migration from registry
  - Import registry settings to XML on first run (Windows only)
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.2.3** — Refactor registry settings pages — superseded by cross-platform settings
  > 2026-10-09: superseded. The cross-platform app stores settings in an XML file via `ISettingsProvider` (`XmlFileSettingsProvider`, `AppSettings`); the Windows-only WinForms app keeps its registry pages unchanged.

- [x] **P1-1.2.4** — Create `IPuttySessionsProvider` + implementations — @automated 2026-03-10
  - `WindowsPuttySessionsProvider` — registry (Windows)
  - `FilePuttySessionsProvider` — `~/.ssh/config` (Linux/macOS)
  - PR: claude/analyze-cross-platform-portability-FndER

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

- [x] **P1-1.3.4** — Update `SecureXmlHelper` to use `ICryptoProvider` — @automated 2026-03-10
  - Added static `CryptoProvider` hook; backwards-compatible
  - PR: claude/analyze-cross-platform-portability-FndER

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

- [x] **P1-1.5.1** — Add `Microsoft.Extensions.DependencyInjection` to `mRemoteNG.Core` — @automated 2026-03-10
  - `ServiceCollectionExtensions.AddPlatformServices()` wires all platform services
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.5.2** — DI container at startup — done in the Avalonia app
  > 2026-10-09: superseded. The Avalonia app is DI-based end to end (`AppServices`, `AddMRemoteNgCore`); the WinForms app is not being reworked.

---

#### 1.6 Compilation Gate

- [x] **P1-1.6.1** — Change main project TFM from `net10.0-windows10.0.26100.0` to `net10.0` — @automated 2026-03-10
  - Conditional TFM: Windows gets `net10.0-windows10.0.26100.0`, others get `net10.0`
  - WinForms/WPF/COM/Windows-only packages wrapped with OS condition
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P1-1.6.2** — Verify project builds on Ubuntu 22.04 CI agent
  > 2026-10-09: GitHub Actions on `dev` is green on ubuntu-latest (platform projects, Core, Avalonia app, all tests).
  - Blocked by: P1-1.1.1 (main project TFM changed to net10.0 conditionally, but remaining
    Windows-only source files will cause compile errors on Linux until Core split is done)
  - CI YAML is ready (cross-platform.yml); job will be unblocked after P1-1.1.1 + P1-1.2.3

- [x] **P1-1.6.3** — Verify project builds on macOS CI agent
  > 2026-10-09: GitHub Actions on `dev` is green on macos-latest (Avalonia app and all tests). Windows also green, including the legacy WinForms build.

---

## PHASE 2 — Avalonia UI Migration

### Milestone 2 Goal: Main window renders and is interactive on all 3 platforms.

---

#### 2.1 Project Setup

- [x] **P2-2.1.1** — Create `mRemoteNG.Avalonia` project — @automated 2026-03-10
  - Avalonia 11.2.7, ReactiveUI, Dock.Avalonia, Material.Icons.Avalonia
  - Packages: Avalonia 11.x, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.ReactiveUI, Dock.Avalonia
  - PR: —

- [x] **P2-2.1.2** — Set up ReactiveUI MVVM architecture — @automated 2026-03-10
  - MainWindowViewModel, ConnectionTreeViewModel, OptionsWindowViewModel, ConnectionDialogViewModel, QuickConnectViewModel
  - DI wiring in AppServices.cs

- [x] **P2-2.1.3** — Theming system — @automated 2026-03-10
  - DarkTheme.axaml (VS2015 dark palette, full control style overrides)
  - LightTheme.axaml (VS2015 light palette)
  - ThemeService: runtime Apply() + LoadFromSettings/SaveToSettings

- [x] **P2-2.1.4** — Icon/image resources — @automated 2026-03-10
  - 39 .ico files copied to Assets/Icons/
  - IconService: protocol→icon mapping, bitmap cache, WindowIcon helper

- [x] **P2-2.2.1** — Main window shell — @automated 2026-03-10
  - MainWindow.axaml: menu bar (File/View/Tools/Help), toolbar with quick-connect bar, status bar, 3-panel layout (tree | sessions | log)
  - All menu items bound to ReactiveCommands

- [x] **P2-2.2.2** — Docking system — @automated 2026-03-10
  - DockFactory.cs: Dock.Avalonia ProportionalDock layout
  - ConnectionTreeDockable, SessionsDockable, LogPanelDockable ViewModels

- [x] **P2-2.2.3** — Menu system — @automated 2026-03-10 (part of P2-2.2.1)

- [x] **P2-2.2.4** — Toolbar — @automated 2026-03-10 (part of P2-2.2.1)

- [x] **P2-2.2.5** — Status bar — @automated 2026-03-10 (part of P2-2.2.1)

- [x] **P2-2.3.1** — Connection tree view — @automated 2026-03-10
  - ConnectionTreeView.axaml: TreeDataTemplate, protocol badges, context menu

- [x] **P2-2.3.2** — Connection tree ViewModel — @automated 2026-03-10
  > 2026-10-09: tree is a live wrapper over the Core model; edit/duplicate/delete/cut-paste/drag-drop/reorder, search with ancestors kept, unsaved-changes tracking.
  - ConnectionTreeViewModel: ObservableCollection, search filter, ReactiveCommands, demo data

- [x] **P2-2.3.3** — Connection context menus — @automated 2026-03-10 (in ConnectionTreeView.axaml)

- [x] **P2-2.4.1** — Session tab host — @automated 2026-03-10
  > 2026-10-09: tabs select, close (with optional confirmation) and dispose; covered by headless UI test.
  - SessionsView.axaml: tab strip with protocol badges, connection state indicator, close button
  - SessionsDockable/SessionTabViewModel: ObservableCollection, AddSession/CloseSession

- [x] **P2-2.4.2** — Embedded connection view — @automated 2026-03-10
  - Scaffolded in SessionsView; Phase 3 will add native window embedding

- [x] **P2-2.5.1** — Options window shell — @automated 2026-03-10
  > 2026-10-09: options are persisted (`AppSettingsService`), edited as a copy (OK/Apply/Cancel/Reset), validated, and take effect live. Pages without a backing feature were removed.
  - OptionsWindow.axaml: left category nav + right page area + OK/Cancel/Apply/Reset

- [x] **P2-2.5.2** — Appearance settings page — @automated 2026-03-10
- [x] **P2-2.5.3** — Connection settings page — @automated 2026-03-10
- [x] **P2-2.5.4** — Security settings page — @automated 2026-03-10
- [x] **P2-2.5.5** — Advanced settings page — @automated 2026-03-10
- [x] **P2-2.5.6** — Updates settings page — @automated 2026-03-10
- [x] **P2-2.5.7** — Notifications settings page — @automated 2026-03-10
- [x] **P2-2.5.8** — Theme settings page — @automated 2026-03-10
- [x] **P2-2.5.9** — Tabs settings page — @automated 2026-03-10
- [x] **P2-2.5.10** — Credentials settings page — @automated 2026-03-10
- [x] **P2-2.5.11** — Protocols settings page — @automated 2026-03-10
  > 2026-10-09: pages are now Startup & Exit, Appearance (incl. theme), Connections (default ports, timeout, SSH keep-alive/key), Credentials (credential manager), Notifications, Updates. Security/Advanced/Tabs/Protocols pages were removed because nothing behind them existed.

- [x] **P2-2.6.1** — About dialog — @automated 2026-03-10
  - AboutDialog.axaml: logo, version, links to GitHub/docs, GitHub/Docs/OK buttons

- [x] **P2-2.6.2** — Connection add/edit dialog — @automated 2026-03-10
  > 2026-10-09: full property editor with inheritance checkboxes, per-protocol sections and validation; used for add and edit.
  - ConnectionDialog.axaml: General/Protocol/Credentials/RDP/SSH cards
  - ConnectionDialogViewModel: per-protocol panels (IsRdp, IsSsh), port auto-fill

- [x] **P2-2.6.4** — Quick connect dialog — @automated 2026-03-10
  - QuickConnectDialog.axaml: host, protocol, username, password fields

- [x] **P2-2.6.7** — Import dialog — @automated 2026-03-10
  > 2026-10-09: imports mRemoteNG XML/CSV, PuTTY sessions, OpenSSH config, RDCMan, .rdp, Remote Desktop Manager CSV, SecureCRT. DPAPI-protected passwords in foreign formats are dropped (reported).
  - Supports: mRemoteNG XML/CSV, PuTTY sessions, SSH config, RDM, SecureCRT

- [x] **P2-2.6.8** — Export dialog — @automated 2026-03-10
  > 2026-10-09: exports XML (optionally with a new master password / full-file encryption) or CSV, whole tree or selected folder, with credential filters.
  - Supports: XML and CSV formats, all connections or selected folder

- [x] **P2-2.7.1** — System tray — @automated 2026-03-10
  - TrayIconService.cs: Avalonia TrayIcon, context menu (Show, Quick Connect, Exit)
  - Initialized in App.axaml.cs on startup

- [x] **P2-2.7.2** — Splash screen — @automated 2026-03-10
  - SplashScreen.axaml: transparent rounded window, progress bar, status text
  - SplashScreen.cs: RunInitialization() async wrapper
  - ViewModels directory, Views directory, base classes
  - PR: —
---

## PHASE 3 — Protocol Replacement

### Milestone 3 Goal: SSH, VNC, RDP, and HTTP connections work on Linux and macOS.

---

#### 3.1 SSH (PuTTYNG → SSH.NET)

- [x] **P3-3.1.1** — Create `SshNetProtocol.cs` — @automated 2026-03-10
  > 2026-10-09: SSH.NET 2026.0.0; known_hosts verification with accept/replace prompts; username/password/keyboard-interactive prompts; resize; opening command; timeout and keep-alive from settings. Integration-tested against a real sshd.
  - SSH.NET integration: password, public-key, keyboard-interactive auth
  - Shell stream, keepalive, reconnect on error
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.1.2** — Avalonia terminal emulator — @automated 2026-03-10
  - `TerminalView.cs`: custom VT100/xterm mini-parser + Avalonia canvas renderer
  - Cursor rendering, keyboard mapping (F-keys, arrows, ctrl combos)
  - Shared by SSH, Telnet, Rlogin, PowerShell, Serial
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.1.3** — SSH session manager — @automated 2026-03-10
  - Wired into `SessionsDockable.OpenConnectionAsync()`
  - `SessionTabViewModel` tracks `ConnectionState` + status messages
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.1.4** — SFTP browser view — @automated 2026-03-10
  > 2026-10-09: real SFTP window (Tools > SFTP File Transfer): list, navigate, upload/download with progress and cancel, mkdir, recursive delete. Verified byte-identical transfers against sshd.
  - `SftpBrowserViewModel`: list, navigate, download, upload, delete
  - `SftpEntryViewModel`: name, size, type, last-modified
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.1.5** — SSH config file importer — @automated 2026-03-10
  - `SshConfigImporter.ImportAsync()`: parses `~/.ssh/config`
  - Supports: Host, HostName, Port, User, IdentityFile, ProxyJump, ForwardAgent
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 3.2 Telnet & Rlogin (PuTTYNG → Custom)

- [x] **P3-3.2.1** — Create `TelnetProtocol.cs` — @automated 2026-03-10
  > 2026-10-09: keystrokes were never sent before; now a stateful codec (IAC/UTF-8 split across reads, RFC 1143 option state, NAWS on resize). Integration-tested against inetutils telnetd.
  - Pure .NET TcpClient + full IAC option negotiation
  - WILL ECHO, WILL SGA, WILL NAWS (window resize)
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.2.2** — Create `RloginProtocol.cs` — @automated 2026-03-10
  > 2026-10-09: keystrokes now sent; async handshake with timeout; stateful UTF-8. Window-size updates are not supported (needs TCP urgent data).
  - RFC 1282 three-part handshake (null + client-user + server-user + term)
  - Bidirectional I/O via TerminalView
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 3.3 RDP (MSTSCLib → FreeRDP)

- [x] **P3-3.3.1** — Evaluate and decide on FreeRDP integration approach — @automated 2026-03-10
  - **Decision: subprocess + native window embedding (ADR-006)**
  - FreeRDP chosen: most mature, actively maintained, xfreerdp 3.x CLI
  - Subprocess approach avoids unstable managed bindings
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.3.2** — Create `RdpProtocol.cs` (FreeRDP subprocess) — @automated 2026-03-10
  - Spawns `xfreerdp` / `wfreerdp` with full argument builder
  - Dynamic resolution, audio, clipboard, drive redirect, NLA, gateway support
  - `FindFreeRdpExecutable()` searches PATH + common install locations
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.3.3** — Windows RDP path — FreeRDP (wfreerdp) instead of MSTSCLib
  > 2026-10-09: superseded — the fake `WindowsRdpProtocol` was deleted; Windows uses `wfreerdp` embedded via `/parent-window` like Linux.
  - Stubbed with `[SupportedOSPlatform("windows")]`
  - Phase 4: full MSTSCLib COM interop via `NativeControlHost`
  - PR: claude/analyze-cross-platform-portability-FndER

- [~] **P3-3.3.4** — RDP feature parity validation
  > 2026-10-09: validated against xrdp 0.9 + FreeRDP 3.32 in this repo's test environment: connect, embedded rendering, resize, keyboard focus, certificate policies, closed port. Not yet validated against Windows Server (NLA authentication failure path, RD Gateway). `RdpIntegrationTests` covers the NLA path when `RDP_TEST_NLA=true` and a Windows host is configured.

---

#### 3.4 VNC

- [x] **P3-3.4.1** — Create Avalonia VNC view — @automated 2026-03-10
  > 2026-10-09: managed RFB 3.3/3.7/3.8 client (None + VNC auth, Raw/CopyRect/RRE/Hextile/ZRLE, DesktopSize, Cursor), scaling modes, view-only, keyboard/mouse. Integration-tested against TigerVNC Xvnc. Not implemented: Tight/Zlib encodings, proxy settings.
  - `VncProtocol.cs` + `VncView.cs`: MarcusW.VncClient architecture wired
  - Keyboard/mouse forwarding stubs (Phase 4: full RFB input events)
  - Framebuffer rendering via `WriteableBitmap` → Avalonia `Image`
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 3.5 HTTP/HTTPS

- [x] **P3-3.5.1** — HTTP/HTTPS — open in the system browser
  > 2026-10-09: decided against embedding: the only Avalonia 11 WebView package needs WebKitGTK 4.0, which current distributions no longer ship. HTTP/HTTPS open in the default browser from the tab (Reopen/Copy URL).
  - `WebViewProtocol.cs`: platform-agnostic wrapper
  - `WebBrowserView`: address bar, back/forward/refresh, Go button
  - Phase 4: wire actual WebView2/WebKitGtk/WKWebView control
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 3.6 PowerShell & Serial

- [x] **P3-3.6.1** — Cross-platform PowerShell protocol (`pwsh`) — @automated 2026-03-10
  - `PowerShellProtocol.cs`: spawns `pwsh` (falls back to `powershell.exe`)
  - Remote sessions via `Enter-PSSession -ComputerName`
  - TerminalView I/O + stdin forwarding
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.6.2** — Cross-platform serial port protocol — @automated 2026-03-10
  - `SerialProtocol.cs`: `System.IO.Ports.SerialPort`
  - Auto-detects ports (`/dev/ttyUSB0`, `COM3`, `/dev/cu.usbserial-*`)
  - Configurable: baudRate, dataBits, parity, stopBits, handshake
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 3.7 External App Protocols

- [x] **P3-3.7.1** — Generic external app protocol — @automated 2026-03-10
  - `ExternalAppProtocol.cs`: token-substituted command template
  - Tokens: `{hostname}`, `{port}`, `{username}`, `{password}`, `{domain}`
  - Examples: `anydesk {hostname}`, `mstsc.exe /v:{hostname}:{port}`, `open rdp://...`
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P3-3.7.2** — RDP window embedding
  > 2026-10-09: FreeRDP is embedded into a native child window (`/parent-window`) on Linux/X11 (verified against xrdp: rendering, resize, tab switching, focus) and Windows (implemented, not yet run on Windows). macOS runs FreeRDP in its own window with an in-tab panel.

---

## PHASE 4 — Integration, Polish & Packaging

### Milestone 4 Goal: Installable packages available for all 3 platforms.

---

#### 4.1 Testing

- [x] **P4-4.1.1** — Cross-platform test project `mRemoteNG.Tests.CrossPlatform` — @automated 2026-03-10
  - `net10.0` target, xUnit, FluentAssertions, NSubstitute
  - `AesGcmCryptoProviderTests`: encrypt/decrypt, tamper detection, Unicode, persistence
  - `SettingsProviderTests`: round-trip, save/reload, remove, list keys
  - `ProtocolFactoryTests`: instantiation of all 11 protocol types, visual protocol check
  - `SshConfigImporterTests`: parse Host/HostName/Port/User/IdentityFile/wildcards
  - `ConnectionParametersTests`: display name, extras, enum names
  - Added to `cross-platform.yml`: runs on Linux, macOS, Windows
  - PR: claude/analyze-cross-platform-portability-FndER

- [x] **P4-4.1.2** — Integration tests (SSH, VNC, settings round-trip)
  - Requires live test servers; set up in GitHub Actions service containers

- [x] **P4-4.1.3** — UI automation tests (Avalonia headless renderer)
  > 2026-10-09: `mRemoteNG.Avalonia.Tests` (Avalonia.Headless.XUnit) boots the real main window; runs in CI on all three OSes.
    and the main window to be fully wired (depends on P1-1.1.1 completion)

---

#### 4.2 Linux Packaging

- [x] **P4-4.2.1** — AppImage packaging — @automated 2026-03-10
  > 2026-10-09: built and launched successfully (appimagetool from AppImage/appimagetool, no FUSE needed).
  - `packaging/linux/build-appimage.sh`: publish → AppDir → appimagetool
  - AppRun entrypoint, .desktop entry, AppStream metainfo XML
  - PR: claude/analyze-cross-platform-portability-FndER

- [~] **P4-4.2.2** — Flatpak manifest — @automated 2026-03-10
  > 2026-10-09: manifest exists but has not been built with flatpak-builder.
  - `packaging/linux/mremoteng.flatpak.yml`: org.freedesktop.Platform 23.08
  - Finish-args: Wayland/X11/network/audio/home/tray
  - PR: claude/analyze-cross-platform-portability-FndER

- [~] **P4-4.2.3** — Snap packaging
  > 2026-10-09: not built; there is no snapcraft.yaml validation in CI.
  - `snapcraft.yaml` — deferred (Flatpak preferred)

- [x] **P4-4.2.4** — .deb package — @automated 2026-03-10
  > 2026-10-09: built, installed with dpkg and launched successfully; icon and --arch/--version flags fixed.
  - `packaging/linux/build-deb.sh`: publish → DEBIAN/control → dpkg-deb
  - Recommends: xfreerdp3, xclip|wl-clipboard, libnotify-bin
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 4.3 macOS Packaging

- [~] **P4-4.3.1** — `.app` bundle + code signing — @automated 2026-03-10
  > 2026-10-09: script exists; not yet run (needs a macOS runner — the scheduled nightly workflow builds it).
  - `packaging/macos/build-dmg.sh`: Info.plist, entitlements.plist
  - codesign with hardened runtime; notarytool submit/staple
  - Universal binary support: lipo x64 + arm64
  - PR: claude/analyze-cross-platform-portability-FndER

- [~] **P4-4.3.2** — DMG installer — @automated 2026-03-10
  > 2026-10-09: script exists; not yet run (needs a macOS runner — the scheduled nightly workflow builds it).
  - create-dmg with icon positions; fallback to hdiutil
  - PR: claude/analyze-cross-platform-portability-FndER

- [~] **P4-4.3.3** — Homebrew cask — @automated 2026-03-10
  > 2026-10-09: script exists; not yet run (needs a macOS runner — the scheduled nightly workflow builds it).
  - `packaging/macos/mRemoteNG.rb`: livecheck, on_arm/on_intel, zap, caveats
  - PR: claude/analyze-cross-platform-portability-FndER

---

#### 4.4 CI/CD

- [x] **P4-4.4.1** — Multi-platform GitHub Actions matrix — @automated 2026-03-10
- [x] **P4-4.4.2** — Nightly builds for all platforms — @automated 2026-03-10
  - `.github/workflows/nightly-build.yml`
  - 7 jobs: version → build (5 platforms) → package-linux → package-macos → package-windows → test → release
  - Scheduled at 02:00 UTC; manual dispatch with version_suffix param
  - GitHub pre-release with DMG/deb/AppImage/MSI artifacts
  - PR: claude/analyze-cross-platform-portability-FndER

- [!] **P4-4.4.3** — Code signing pipeline — **BLOCKED**
  - Blocked by: external accounts and paid certificates required
    - macOS: Apple Developer Program membership ($99/yr); codesign + notarytool scripts ready
    - Windows: EV code signing certificate (~$300/yr from DigiCert/Sectigo)
  - CI scripts already written; add secrets `APPLE_IDENTITY`, `APPLE_NOTARIZE_KEYCHAIN_PROFILE`,
    `WINDOWS_PFX_BASE64`, `WINDOWS_PFX_PASSWORD` to GitHub repo to activate

---

#### 4.5 Documentation

- [x] **P4-4.5.1** — Update README with cross-platform install instructions
- [x] **P4-4.5.2** — User migration guide (from Windows registry)
- [x] **P4-4.5.3** — Developer contributing guide (cross-platform)

---

## Architecture Decision Records

| ADR | Status | Decision |
|-----|--------|---------|
| ADR-001 | DECIDED | Use Avalonia UI 11.x as cross-platform UI framework |
| ADR-002 | DECIDED | Use ReactiveUI for MVVM pattern |
| ADR-003 | REVISED | Fixed grid layout (tree / sessions / bottom panel) with show/hide; Dock.Avalonia UI was removed, only Dock.Model base classes remain |
| ADR-004 | DECIDED | Use SSH.NET to replace PuTTYNG.exe |
| ADR-005 | DECIDED | Use BouncyCastle AES-256-GCM to replace DPAPI |
| ADR-006 | DECIDED | FreeRDP subprocess embedded with `/parent-window`; arguments via `/args-from:env` (FreeRDP 3) so passwords stay out of the process list |
| ADR-007 | DECIDED | Use `Microsoft.Extensions.DependencyInjection` for IoC |
| ADR-008 | DECIDED | Custom terminal (`TerminalScreen` model + `TerminalView`): xterm-256color, SGR incl. truecolour, alternate screen, scroll regions |
| ADR-009 | REVISED | No MSTSCLib in the cross-platform app; Windows uses wfreerdp. MSTSCLib remains only in the legacy WinForms app |
| ADR-010 | DECIDED | Linux clipboard: wl-clipboard when WAYLAND_DISPLAY is set, otherwise xclip |

---

## Blocked Items Log

| ID | Task | Reason | Unblocked when |
|----|------|--------|----------------|
| P4-4.4.3 | Code signing pipeline | Apple Developer account + Windows code-signing certificate (paid) | Purchase certs, add GitHub Secrets |

Open but not blocked (need hardware or a runner, not code): P3-3.3.4 Windows Server/NLA validation; P4-4.2.2/4.2.3
Flatpak/Snap builds; P4-4.3.x macOS packaging run.
---

## Changelog

| Date | Change | Author |
|------|--------|--------|
| 2026-03-10 | Initial migration plan and progress tracker created | automated |
| 2026-03-10 | Phase 1 implementation started | automated |
| 2026-03-10 | P1-1.1.2–1.1.6: All 5 new projects created and added to solution | automated |
| 2026-03-10 | P1-1.3.1–1.3.3: ICryptoProvider, DpapiCryptoProvider, AesGcmCryptoProvider | automated |
| 2026-03-10 | P1-1.4.1–1.4.5: IWindowService, IClipboardService + all 3 platform impls | automated |
| 2026-03-10 | P2-2.1.1–2.1.2: Avalonia skeleton + ReactiveUI MVVM | automated |
| 2026-03-10 | P4-4.4.1: GitHub Actions cross-platform CI/CD pipeline | automated |
| 2026-03-10 | **Phase 2 COMPLETE**: Full Avalonia UI — themes, docking, 10 settings pages, 5 dialogs, tray, splash | automated |
| 2026-03-10 | Phase 4 complete: Snap, docs, integration tests, SSH File Transfer, Credential Manager, Port Scanner, TFM net10.0 | automated |
| 2026-03-10 | P1-1.1.1 resumed: created `mRemoteNG.Core` scaffold and connected Avalonia bootstrap to core DI extension | codex |
| 2026-03-10 | P1-1.1.1 increment: added cross-platform `ApplicationPaths` in Core and switched settings path consumers to it | codex |
| 2026-03-10 | P1-1.1.1 hardening: ensure cross-platform settings directories are created before writing settings XML | codex |
| 2026-10-09 | Status audit; reopened stubbed items; fixed Linux/macOS startup crash, legacy confCons crypto compatibility, master passwords, FreeRDP launch, session tabs | claude |
| 2026-10-09 | Completed VNC, SSH security/SFTP/terminal, RDP embedding, Telnet/Rlogin input, import/export, settings persistence, tree editing, Light theme, headless UI tests; CI green on all OSes; Linux packages verified | claude |

---


## Notes

- When updating this file in a PR, always update the **Summary Dashboard** counts.
- Use `[!]` for blocked items and add an entry to the **Blocked Items Log** with the reason.
- Label your PRs with `migration-phase-1`, `migration-phase-2`, etc. for tracking.
- Breaking changes to the connection XML schema **must** maintain backward compatibility.
