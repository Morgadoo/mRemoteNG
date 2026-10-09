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

## Status — 2026-10-09 (feature parity pass)

Verification environment: Linux (.NET 10.0.401, Xvfb + openbox) with real servers — OpenSSH sshd, TigerVNC
Xvnc, x11vnc, xrdp + FreeRDP 3.32, inetutils telnetd, MariaDB 10.11, OpenBao 2.4, OpenLDAP slapd — plus
GitHub Actions on ubuntu/macos/windows-latest. Tests: 1219 cross-platform (unit + integration) and 61 headless
UI tests.

**Works (verified on Linux against real servers unless noted)**
- Connection files: legacy confCons.xml 2.5–2.8 incl. master passwords and full-file encryption, byte-compatible
  with the WinForms app; every attribute the legacy writer emits round-trips (`LegacyAttributeContractTests`).
  Import (8 formats + Active Directory + port scan) and export (XML/CSV).
- Storage: SQL backend in the legacy `tblCons/tblRoot/tblUpdate` schema (MySQL/MariaDB verified; SQL Server
  only unit-tested), multi-user change polling with auto-reload, rolling backups, autosave, portable mode.
- Protocols: SSH (known_hosts, SFTP, PuTTY saved sessions, SSHOptions, local/remote/dynamic forwards,
  tunnels through other connections), Telnet, Rlogin, RAW, VNC (Raw/CopyRect/RRE/CoRRE/Hextile/Zlib/ZRLE/Tight
  incl. JPEG, 8/16/32 bpp, HTTP/SOCKS5/repeater proxies, ARD and MS-Logon auth — the last two against fakes
  only), RDP (FreeRDP embedded; display/performance/redirection/start program/idle timeout/gateway/Hyper-V
  options mapped), HTTP/HTTPS (system browser), PowerShell, local shell, serial, IntApp (external tool
  embedded in the tab on X11/Windows), AnyDesk.
- Sessions: tab menu (reconnect, duplicate, rename, special keys, smart size, view-only, full screen, refresh,
  SFTP, move to panel, close others/right), auto-reconnect, reopen sessions at startup, named panels (tabbed,
  side by side, stacked, floating), saved layout, Multi-SSH, Favorites, tab/frame colours, environment tags.
- Tools: External Tools (extApps.xml, variables, toolbar, pre/post connection apps), port scanner, Active
  Directory import (slapd with the AD schema), UltraVNC SingleClick listener.
- External providers: Vault/OpenBao (KV v1/v2, LDAP, SSH OTP; verified against a real server); Delinea Secret
  Server, Passwordstate, 1Password CLI and AWS EC2 (verified against simulated services only).
- App: options pages for every area, Dark/Light/System + VS2015 Blue/Darcula + theme editor, log file,
  legacy command-line switches, start minimised, update download with checksum verification.

**Not verified / known limitations**
- Windows and macOS: builds and unit/UI tests run in CI, but the app has not been driven interactively there
  (RDP/IntApp embedding on Windows, DPAPI, registry PuTTY sessions, Keychain).
- RDP against Windows Server (NLA failure path, RD Gateway, Hyper-V, restricted admin) — unit-tested only.
  FreeRDP has no Remote Credential Guard, cursor shadow/blink or RDP version options.
- VNC: ZlibHex falls back to Zlib; UltraVNC Ultra encoding, chat, file transfer and VeNCrypt are not done.
- HTTP/HTTPS is not embedded (no maintained WebView for Avalonia 11). IntApp is not embedded on macOS or
  native Wayland.
- SQL storage cannot hold TabColor, ConnectionFrameColor, Vault fields or the RD Gateway access token (the
  legacy schema has no columns for them); `LocalConnectionProperties.xml` and ODBC are not ported.
- Session tabs cannot be dragged between panels (use Move to Panel).
- Terminal: Rlogin does not send window-size changes; wide (CJK) characters use one cell.
- Translations: ~280 strings that exist only in the new app (`mRemoteNG.Core/Localization/Strings.resx`) show
  in English in every language; legacy translations are used as they are (some are partial or inaccurate).
- Packaging: Flatpak, Snap and macOS DMG/Homebrew scripts have not been run; code signing needs certificates.

## UI refresh — 2026-10-09

The Avalonia UI was redesigned to `docs/design-system.md`: neutral surfaces with one accent colour, Inter,
Material icons with protocol colours, rounded geometry on a 4 px grid, and keyboard-first interaction.
- Design tokens and control styles shared by all views (`Themes/`), Dark/Light/VS2015 Blue/Darcula and user
  themes (old theme files still load), a developer gallery (`--design-gallery`).
- Main window: header bar with address-bar Quick Connect, command palette (Ctrl+K), every menu shortcut bound
  (terminal-safe rule in `Views/Shell/AppShortcuts.cs`), Recent/Favorites cards, status bar, collapsible log
  panel, toasts.
- Tree: Material glyphs, open-session dots, hover connect, search highlighting; tabs with protocol glyphs and
  status dots, overflow list; restyled panels, banners and empty states.
- Dialogs: one pattern (header, footer, focus in the first field, Enter/Esc); Options with icon navigation,
  search, settings cards and theme swatches; connection editor with categories, inherit toggles and inline
  validation.
- UX fixes from the recorded test run: RDP tab menu opens on the first right-click, Ctrl+Alt+Enter leaves
  full screen from terminals, dialogs over embedded RDP/IntApp sessions get the keyboard, New Panel asks for a
  name, Ctrl+, opens Options, Light-theme contrast; PowerShell runs on a pseudo-terminal (Linux) so commands
  and formatted output work.

## Feature Parity with the WinForms App

Audit of 2026-10-09 against the legacy `mRemoteNG/` sources, and what was done about each gap:

| # | Gap found by the audit | Status |
|---|------------------------|--------|
| 1 | RDP options ignored by the FreeRDP launcher; Hyper-V console | Done — every legacy RDP property is mapped (`FreeRdpLegacySettingsTests`); Windows Server/Hyper-V not run live |
| 2 | External Tools, pre/post apps, IntApp | Done |
| 3 | External credential/address providers | Done — Vault live; other providers against simulated services |
| 4 | Session tab menu, reconnect, shortcuts | Done |
| 5 | SQL storage, multi-user, backups, autosave, portable | Done — SQL Server not run |
| 6 | PuTTY sessions, SSHOptions, SSH tunnels | Done |
| 7 | Multi-SSH, AD import, port-scan import, UltraVNC SingleClick | Done |
| 8 | Panels, layout, tab/frame colours, Favorites, environment tags | Done (no drag between panels) |
| 9 | Tree: expand/collapse, copy hostname, inheritance, connect with options, live PuTTY root | Done |
| 10 | Master password / encryption settings of the open file | Done (Connection File Properties) |
| 11 | Translations, theme editor, log file, start minimised, update download, CLI switches | Done — the 24 legacy translations are used (Options ▸ Appearance ▸ Language, applied at restart); strings new in this app are English only until translated |
| 12 | Connection editor for every stored property | Done — conditional fields as in the legacy property grid |

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
  > 2026-10-09: managed RFB 3.3/3.7/3.8 client (None, VNC, ARD and MS-Logon II auth; Raw/CopyRect/RRE/CoRRE/Hextile/Zlib/ZRLE/Tight incl. JPEG; 8/16/32 bpp; DesktopSize, Cursor), HTTP/SOCKS5/UltraVNC repeater proxies, scaling modes, view-only, special keys, UltraVNC SingleClick listener. Encodings verified pixel-exact against TigerVNC Xvnc and x11vnc.
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
| 2026-10-09 | Feature parity pass: RDP options/Hyper-V, External Tools/IntApp, credential & address providers, session tabs/panels/layout/Multi-SSH, SQL storage/backups/portable/logging/themes, VNC encodings/auth/proxies, AD import, port scanner, UltraVNC SingleClick, PuTTY sessions/SSH tunnels | claude |

---


## Notes

- When updating this file in a PR, always update the **Summary Dashboard** counts.
- Use `[!]` for blocked items and add an entry to the **Blocked Items Log** with the reason.
- Label your PRs with `migration-phase-1`, `migration-phase-2`, etc. for tracking.
- Breaking changes to the connection XML schema **must** maintain backward compatibility.
