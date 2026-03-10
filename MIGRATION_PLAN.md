# mRemoteNG → Cross-Platform Migration Plan
## Full Avalonia UI Port for Linux and macOS

**Project:** mRemoteNG v1.78.2-dev
**Target:** `net10.0` (cross-platform, drop `net10.0-windows10.0.26100.0`)
**UI Target:** Avalonia UI 11.x
**Platforms:** Windows (existing), Linux (new), macOS (new)
**Total estimated effort:** 18–24 months (team of 3–4 developers)

---

## Architecture Overview

### Before (Windows-only)
```
mRemoteNG.exe
├── WinForms + WPF UI
├── COM/MSTSCLib (RDP)
├── PuTTYNG.exe (SSH/Telnet)
├── Win32 P/Invoke (NativeMethods)
├── Windows Registry (config)
└── DPAPI (encryption)
```

### After (Cross-platform)
```
mRemoteNG (Avalonia App)
├── mRemoteNG.Core          ← business logic, protocols, config (net10.0)
├── mRemoteNG.Platform      ← platform abstraction interfaces
│   ├── mRemoteNG.Platform.Windows   ← Win32 impl
│   ├── mRemoteNG.Platform.Linux     ← GTK/X11 impl
│   └── mRemoteNG.Platform.Mac       ← Cocoa impl
├── mRemoteNG.Avalonia      ← new cross-platform UI project
└── mRemoteNG.Protocols     ← protocol implementations
    ├── Protocol.RDP        ← FreeRDP-based
    ├── Protocol.SSH        ← SSH.NET-based
    ├── Protocol.VNC        ← VncSharpCore
    └── Protocol.Web        ← WebView (platform-specific)
```

---

## Phase 1 — Foundation & Abstraction Layer

**Goal:** Separate Windows-specific code from business logic; make the codebase compilable on all platforms.
**Duration:** 3–4 months
**Risk:** Medium — existing Windows functionality must remain working.

### 1.1 Project Structure Changes

#### Task 1.1.1 — Add `mRemoteNG.Core` project
- **File:** `mRemoteNG.Core/mRemoteNG.Core.csproj`
- **TFM:** `net10.0` (no OS suffix)
- **Purpose:** All domain logic, models, interfaces, serializers
- **Move from `mRemoteNG/`:**
  - `App/Info/` — application metadata
  - `App/Runtime.cs` — state container
  - `Config/` — all serializers, data providers
  - `Connection/` — connection models
  - `Security/` — encryption logic
  - `Tree/` — connection tree models
  - `Container/` — credential models
  - `Credential/` — credential repositories

#### Task 1.1.2 — Add `mRemoteNG.Platform` abstraction project
- **File:** `mRemoteNG.Platform/mRemoteNG.Platform.csproj`
- **TFM:** `net10.0`
- **Define interfaces for all OS-specific operations:**
  - `IClipboardService` — clipboard read/write
  - `IWindowManager` — window positioning, focus, minimize/maximize
  - `IProcessService` — child process spawning, job-object equiv.
  - `ISettingsProvider` — settings persistence (registry vs file)
  - `ICredentialEncryption` — DPAPI vs AES-GCM
  - `ISystemTrayService` — system tray icon
  - `IFileDialogService` — open/save dialogs
  - `IUpdateChecker` — OS-specific update mechanisms
  - `INotificationService` — toast/notification popups

#### Task 1.1.3 — Add `mRemoteNG.Platform.Windows` project
- **File:** `mRemoteNG.Platform.Windows/mRemoteNG.Platform.Windows.csproj`
- **TFM:** `net10.0-windows10.0.17763.0`
- **Purpose:** Win32 implementations of all `mRemoteNG.Platform` interfaces
- **Move from `mRemoteNG/`:**
  - `App/NativeMethods.cs`
  - `Tools/WindowsRegistry/`
  - `Tools/ChildProcessTracker.cs`
  - `Tools/ADhelper.cs`
  - `Tools/ProcessController.cs`
  - `App/Checks/DotNetRuntimeCheck.cs`
  - `App/Checks/VCppRuntimeCheck.cs`

#### Task 1.1.4 — Add `mRemoteNG.Platform.Linux` project
- **File:** `mRemoteNG.Platform.Linux/mRemoteNG.Platform.Linux.csproj`
- **TFM:** `net10.0`
- **Runtime check:** `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)`
- **Implement:**
  - `LinuxProcessService` — uses `prctl(PR_SET_PDEATHSIG, SIGTERM)` for child tracking
  - `LinuxSettingsProvider` — XDG config dir (`~/.config/mRemoteNG/`)
  - `LinuxCredentialEncryption` — SecretService via `libsecret`
  - `LinuxClipboardService` — uses `xclip`/`wl-clipboard` via subprocess
  - `LinuxNotificationService` — uses `libnotify`

#### Task 1.1.5 — Add `mRemoteNG.Platform.Mac` project
- **File:** `mRemoteNG.Platform.Mac/mRemoteNG.Platform.Mac.csproj`
- **TFM:** `net10.0`
- **Runtime check:** `RuntimeInformation.IsOSPlatform(OSPlatform.OSX)`
- **Implement:**
  - `MacProcessService` — uses `kqueue` for child tracking
  - `MacSettingsProvider` — macOS `~/Library/Application Support/mRemoteNG/`
  - `MacCredentialEncryption` — Keychain via `Security.framework`
  - `MacNotificationService` — NSUserNotification via P/Invoke

#### Task 1.1.6 — Update Solution file
- **File:** `mRemoteNG.sln`
- Add all new projects to solution
- Configure build configurations per project

---

### 1.2 Settings System Migration

#### Task 1.2.1 — Make `PortableSettingsProvider` the primary provider
- **File:** `mRemoteNG/Config/Settings/Providers/PortableSettingsProvider.cs`
- Remove `#if PORTABLE` gates — make it unconditionally available
- Move settings file to platform-appropriate location via `ISettingsProvider`
- **Linux path:** `~/.config/mRemoteNG/settings.xml`
- **macOS path:** `~/Library/Application Support/mRemoteNG/settings.xml`
- **Windows path:** `%APPDATA%\mRemoteNG\settings.xml` (instead of registry)

#### Task 1.2.2 — Create settings migration utility
- **File:** `mRemoteNG.Core/Config/Settings/SettingsMigrationHelper.cs`
- Detect first run after migration
- Import existing settings from Windows registry if available
- Write to new XML format
- Mark migration complete to avoid re-import

#### Task 1.2.3 — Remove registry-only settings classes
- **Files to refactor:** 10 files in `Config/Settings/Registry/`
  - `OptRegistryAppearancePage.cs`
  - `OptRegistryAdvancedPage.cs`
  - `OptRegistryConnectionsPage.cs`
  - `OptRegistryCredentialsPage.cs`
  - `OptRegistryNotificationsPage.cs`
  - `OptRegistrySecurityPage.cs`
  - `OptRegistryUpdatesPage.cs`
  - `OptRegistryThemePage.cs`
  - `RegistryLoader.cs`
  - `RegistrySettingsLoader.cs`
- Create unified `ISettingsPage` interface
- Each page reads from `ISettingsProvider` not directly from registry

#### Task 1.2.4 — Abstract PuTTY session import
- **File:** `Config/Putty/PuttySessionsRegistryProvider.cs`
- Create `IPuttySessionsProvider` interface
- `WindowsPuttySessionsProvider` — registry-based (existing code)
- `FilePuttySessionsProvider` — reads from `~/.ssh/config` on Linux/macOS

---

### 1.3 Encryption Migration

#### Task 1.3.1 — Create `ICryptoProvider` interface
- **File:** `mRemoteNG.Platform/Security/ICryptoProvider.cs`
- Methods: `Protect(string plaintext) → string`, `Unprotect(string ciphertext) → string`
- Purpose: Abstract DPAPI vs cross-platform crypto

#### Task 1.3.2 — Windows DPAPI implementation
- **File:** `mRemoteNG.Platform.Windows/Security/DpapiCryptoProvider.cs`
- Wraps existing `System.Security.Cryptography.ProtectedData`
- Used when running on Windows

#### Task 1.3.3 — Cross-platform AES-GCM implementation
- **File:** `mRemoteNG.Platform/Security/AesGcmCryptoProvider.cs`
- Uses `BouncyCastle.Cryptography` (already a dependency)
- Key derivation: PBKDF2 with SHA-256, 600,000 iterations
- Key stored in platform keychain via `ICredentialEncryption`
- Backward-compatible: can decrypt DPAPI secrets when migrating from Windows

#### Task 1.3.4 — Update `SecureXmlHelper`
- **File:** `mRemoteNG/Security/SecureXmlHelper.cs`
- Replace direct DPAPI calls with `ICryptoProvider`
- Inject via constructor

---

### 1.4 Win32 P/Invoke Abstraction

#### Task 1.4.1 — Create `IWindowService` interface
- **File:** `mRemoteNG.Platform/IWindowService.cs`
- Methods:
  - `MoveWindow(IntPtr hwnd, int x, int y, int w, int h)`
  - `SetForeground(IntPtr hwnd)`
  - `IsMinimized(IntPtr hwnd) → bool`
  - `PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)`
  - `SetWindowPosition(IntPtr hwnd, ...)`

#### Task 1.4.2 — Create `IClipboardService` interface
- **File:** `mRemoteNG.Platform/IClipboardService.cs`
- Methods:
  - `GetText() → string`
  - `SetText(string text)`
  - `Clear()`
  - `ContainsText() → bool`

#### Task 1.4.3 — Windows implementations
- Move all P/Invoke code from `NativeMethods.cs` into:
  - `mRemoteNG.Platform.Windows/WindowsWindowService.cs`
  - `mRemoteNG.Platform.Windows/WindowsClipboardService.cs`
- Register via DI in Windows startup

#### Task 1.4.4 — Linux/macOS implementations
- `mRemoteNG.Platform.Linux/LinuxClipboardService.cs` — xclip/wl-clipboard
- `mRemoteNG.Platform.Mac/MacClipboardService.cs` — `pbcopy`/`pbpaste`
- Window management: deferred to Avalonia's built-in APIs

---

### 1.5 Dependency Injection Setup

#### Task 1.5.1 — Add DI framework
- **Package:** `Microsoft.Extensions.DependencyInjection`
- **File:** `mRemoteNG.Core/DependencyInjection/ServiceLocator.cs`
- Register all `mRemoteNG.Platform` implementations at startup
- Factory method: `PlatformServiceFactory.Create()` detects OS and returns correct impl

#### Task 1.5.2 — Update startup initialization
- **File:** `App/Startup.cs`
- Replace static/singleton pattern with DI container
- Boot sequence:
  1. Detect OS platform
  2. Register platform services
  3. Register core services
  4. Initialize settings
  5. Launch UI

---

## Phase 2 — Avalonia UI Migration

**Goal:** Replace all WinForms/WPF UI with Avalonia. Full visual and functional parity.
**Duration:** 6–9 months
**Risk:** High — largest scope, touches 200+ files.

### 2.1 Avalonia Project Setup

#### Task 2.1.1 — Create `mRemoteNG.Avalonia` project
- **File:** `mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj`
- **TFM:** `net10.0`
- **Packages:**
  - `Avalonia` 11.x
  - `Avalonia.Desktop` 11.x
  - `Avalonia.Themes.Fluent` or `Avalonia.Themes.Simple`
  - `Avalonia.ReactiveUI`
  - `ReactiveUI` (MVVM framework)
  - `Dock.Avalonia` (docking panels — cross-platform DockPanelSuite replacement)
  - `Avalonia.Controls.DataGrid`
  - `Material.Icons.Avalonia` (icon set)

#### Task 2.1.2 — Set up MVVM architecture
- **Pattern:** ReactiveUI MVVM (ViewModel → View binding)
- **ViewModels directory:** `mRemoteNG.Avalonia/ViewModels/`
  - `MainWindowViewModel`
  - `ConnectionTreeViewModel`
  - `OptionsWindowViewModel`
  - `ConnectionTabViewModel`
  - `CredentialManagerViewModel`
- **Views directory:** `mRemoteNG.Avalonia/Views/`
- **Models:** Reuse from `mRemoteNG.Core`

#### Task 2.1.3 — Set up theming
- **File:** `mRemoteNG.Avalonia/Themes/`
- Dark theme (matching existing VS2015 Dark)
- Light theme (matching existing VS2015 Light)
- System theme detection: `Application.Current.ActualThemeVariant`
- Custom accent color support

#### Task 2.1.4 — Resource system
- **File:** `mRemoteNG.Avalonia/Assets/`
- Port icon resources from embedded `.ico`/`.png`
- Convert to Avalonia `IImage` / `WindowIcon`
- SVG icons via `Avalonia.Svg.Skia`

---

### 2.2 Main Window (`frmMain` → `MainWindow`)

#### Task 2.2.1 — Main window shell
- **File:** `mRemoteNG.Avalonia/Views/MainWindow.axaml`
- Menu bar (platform-native on macOS)
- Status bar
- Docking layout host
- Toolbar

#### Task 2.2.2 — Docking system
- **Package:** `Dock.Avalonia` (by wieslawsoltes)
- **File:** `mRemoteNG.Avalonia/Docking/DockFactory.cs`
- Panels:
  - Connection Tree (left panel, dockable)
  - Error/Log panel (bottom, dockable)
  - Connection tabs (center, tabbed)
  - Credential panel (right, dockable)

#### Task 2.2.3 — Menu system
- **File:** `mRemoteNG.Avalonia/Views/Menus/`
- Port all menus: File, View, Tools, Help, External Tools
- macOS: use Avalonia's native menu support
- Linux/Windows: embedded menu bar

#### Task 2.2.4 — Toolbar
- **File:** `mRemoteNG.Avalonia/Views/MainToolbar.axaml`
- Quick connect bar
- Connection action buttons
- Search field

#### Task 2.2.5 — Status bar
- **File:** `mRemoteNG.Avalonia/Views/MainStatusBar.axaml`
- Active connection count
- Connection status indicator
- Update notification area

---

### 2.3 Connection Tree (`TreeWindow` → `ConnectionTreeView`)

#### Task 2.3.1 — Tree view control
- **File:** `mRemoteNG.Avalonia/Views/ConnectionTreeView.axaml`
- Avalonia `TreeView` with hierarchical binding
- Drag-and-drop reordering
- Right-click context menu (connect, edit, delete, sort)
- Inline rename editing
- Search/filter support
- Icons per protocol type

#### Task 2.3.2 — Connection tree ViewModel
- **File:** `mRemoteNG.Avalonia/ViewModels/ConnectionTreeViewModel.cs`
- ObservableCollection binding to connection list
- `ReactiveCommand` for connect/edit/delete
- Filter observable for search

#### Task 2.3.3 — Connection Node context menus
- **File:** `mRemoteNG.Avalonia/Views/ConnectionContextMenu.axaml`
- Connect, Connect in new tab, Connect in background
- Edit, Duplicate, Delete
- Move to folder
- Export node
- Set credentials

---

### 2.4 Connection Tabs (`ConnectionTab` → `ConnectionTabView`)

#### Task 2.4.1 — Tab host
- **File:** `mRemoteNG.Avalonia/Views/ConnectionTabHost.axaml`
- Avalonia `TabControl` embedded in dock
- Tab drag-and-drop
- Tab context menu (close, close others, detach)
- Tab indicator (protocol icon, connection state)

#### Task 2.4.2 — Embedded connection view
- **File:** `mRemoteNG.Avalonia/Views/EmbeddedConnectionView.axaml`
- Native window embedding surface (per-platform)
  - Windows: `HwndHost`-equivalent via Avalonia's `EmbeddableControlRoot`
  - Linux: X11 window embedding (`XEmbedHost`)
  - macOS: NSView embedding
- Fullscreen toggle
- Toolbar overlay (shows on hover)

---

### 2.5 Options/Settings Dialog

#### Task 2.5.1 — Options window shell
- **File:** `mRemoteNG.Avalonia/Views/OptionsWindow.axaml`
- Left panel: category tree
- Right panel: settings page for selected category
- Apply / Cancel / OK buttons

#### Task 2.5.2 — Port each settings page (10 pages)
For each page create `{Page}SettingsView.axaml` + `{Page}SettingsViewModel.cs`:
- `AppearanceSettingsView` — fonts, colors, DPI
- `ConnectionSettingsView` — default connection options
- `TabsSettingsView` — tab behavior
- `SecuritySettingsView` — encryption, master password
- `AdvancedSettingsView` — timeouts, reconnect
- `UpdatesSettingsView` — auto-update
- `NotificationsSettingsView` — alerts, sounds
- `CredentialsSettingsView` — default credentials
- `ThemeSettingsView` — dark/light/custom theme
- `ProtocolsSettingsView` — protocol defaults

---

### 2.6 Additional Dialogs

#### Task 2.6.1 — About dialog
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/AboutDialog.axaml`

#### Task 2.6.2 — Connection dialog (add/edit)
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/ConnectionDialog.axaml`
- All connection properties
- Per-protocol settings panel (dynamic)
- Credential selection

#### Task 2.6.3 — Credential Manager
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/CredentialManagerDialog.axaml`
- List/edit/delete credentials
- Import from password manager

#### Task 2.6.4 — Quick Connect dialog
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/QuickConnectDialog.axaml`

#### Task 2.6.5 — Port Scanner dialog
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/PortScannerDialog.axaml`

#### Task 2.6.6 — SSH File Transfer dialog
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/SshFileTransferDialog.axaml`

#### Task 2.6.7 — Import/Export dialogs
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/ImportDialog.axaml`
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/ExportDialog.axaml`

#### Task 2.6.8 — External tools dialog
- **File:** `mRemoteNG.Avalonia/Views/Dialogs/ExternalToolsDialog.axaml`

---

### 2.7 System Tray

#### Task 2.7.1 — System tray support
- **Package:** Avalonia has `TrayIcon` support built-in
- **File:** `mRemoteNG.Avalonia/Services/TrayIconService.cs`
- Context menu: Show/Hide, Quick Connect, Recent Connections, Exit
- Windows: native tray
- Linux: StatusNotifierItem (D-Bus)
- macOS: NSStatusItem

---

### 2.8 Splash Screen

#### Task 2.8.1 — Avalonia splash screen
- **File:** `mRemoteNG.Avalonia/Views/SplashScreen.axaml`
- Replace existing WPF splash screen
- Show during async initialization
- Progress bar for loading steps

---

## Phase 3 — Protocol Replacement

**Goal:** Replace all Windows-only protocol implementations with cross-platform alternatives.
**Duration:** 4–6 months
**Risk:** High for RDP; Medium for others.

### 3.1 SSH Protocol (PuTTYNG → SSH.NET)

#### Task 3.1.1 — Create `SshNetProtocol`
- **File:** `mRemoteNG.Protocols/SSH/SshNetProtocol.cs`
- Use `Renci.SshNet.Async` (already a dependency)
- Replace `PuttyBase.cs` process spawning
- Features:
  - Password, keyboard-interactive, public key auth
  - SSH agent forwarding
  - Port forwarding
  - Keep-alive

#### Task 3.1.2 — Avalonia terminal emulator
- **Package:** `VtNetCore` or `XtermSharp` or `AvaloniaTerminal`
- **File:** `mRemoteNG.Protocols/SSH/TerminalView.axaml`
- Full VT100/xterm emulation
- Copy-paste support
- Font size, color scheme
- Scrollback buffer

#### Task 3.1.3 — SSH session management
- **File:** `mRemoteNG.Protocols/SSH/SshSessionManager.cs`
- Session multiplexing (like SSH ControlMaster)
- Reconnect on disconnect
- Session logging

#### Task 3.1.4 — SFTP browser
- **File:** `mRemoteNG.Protocols/SSH/SftpBrowserView.axaml`
- File tree for remote filesystem
- Upload/download with progress
- Replace existing SSH file transfer dialog

#### Task 3.1.5 — SSH config file import
- **File:** `mRemoteNG.Core/Config/Import/SshConfigImporter.cs`
- Parse `~/.ssh/config` on Linux/macOS
- Import hosts as mRemoteNG connections

---

### 3.2 Telnet Protocol (PuTTYNG → Custom)

#### Task 3.2.1 — Create `TelnetProtocol`
- **File:** `mRemoteNG.Protocols/Telnet/TelnetProtocol.cs`
- Pure .NET implementation using `TcpClient`
- Telnet option negotiation (RFC 854, 855, 857, 858)
- Share terminal emulator from SSH implementation

#### Task 3.2.2 — Rlogin Protocol
- **File:** `mRemoteNG.Protocols/Rlogin/RloginProtocol.cs`
- Simple TCP-based protocol (RFC 1282)
- Reuse terminal emulator

---

### 3.3 RDP Protocol (MSTSCLib → FreeRDP)

This is the most complex protocol migration.

#### Task 3.3.1 — Evaluate FreeRDP .NET bindings
- **Library options:**
  a. `FreeRDP-Sharp` — C# bindings to FreeRDP
  b. `AwesomeRDP` — Pure C# RDP library
  c. `MsRdpEx` — Extended MSTSCLib with more features (still Windows-only)
  d. Subprocess-based: spawn `xfreerdp`/`wfreerdp` and embed window
- **Decision:** Use subprocess embedding as primary approach (same as PuTTY approach)
  - `xfreerdp3` on Linux (available via package manager)
  - `wfreerdp3` on Windows (cross-platform FreeRDP build)
  - `freerdp-mac` on macOS (Homebrew)

#### Task 3.3.2 — Create `FreeRdpProtocol`
- **File:** `mRemoteNG.Protocols/RDP/FreeRdpProtocol.cs`
- Spawn `xfreerdp`/`wfreerdp` subprocess
- Pass connection parameters via command-line arguments
- Embed resulting window in Avalonia surface (X11 on Linux, Win32 on Windows)
- Window embedding via `EmbedInto` / `XEmbedHost`

#### Task 3.3.3 — Windows RDP fallback
- **File:** `mRemoteNG.Protocols/RDP/WindowsRdpProtocol.cs`
- Keep existing MSTSCLib-based implementation for Windows
- Guard with `[SupportedOSPlatform("windows")]`
- Select via `IProtocolFactory` based on OS

#### Task 3.3.4 — RDP feature parity checklist
- [ ] Multiple monitor support
- [ ] Audio redirection
- [ ] Clipboard sharing
- [ ] Drive redirection
- [ ] USB redirection
- [ ] RemoteFX / H.264 encoding
- [ ] NLA (Network Level Authentication)
- [ ] Smart card redirection
- [ ] Gateway support (RD Gateway)
- [ ] Custom resolution / dynamic resize

---

### 3.4 VNC Protocol

#### Task 3.4.1 — Avalonia VNC view
- **Library:** `VncSharpCore` (already cross-platform)
- **File:** `mRemoteNG.Protocols/VNC/VncView.axaml`
- Replace WinForms `VncControl` with Avalonia canvas-based renderer
- Keyboard and mouse input forwarding
- Clipboard sync

---

### 3.5 HTTP/HTTPS Protocol (WebView2 → Platform Web View)

#### Task 3.5.1 — Avalonia WebView integration
- **Package:** `Avalonia.WebView` (community package, wraps per-platform)
  - Windows: WebView2 (Chromium)
  - Linux: WebKitGtk
  - macOS: WKWebView
- **File:** `mRemoteNG.Protocols/Web/WebViewProtocol.axaml`
- Address bar, navigation controls
- Certificate warning handling
- Basic auth injection

---

### 3.6 PowerShell Protocol

#### Task 3.6.1 — Cross-platform PowerShell
- **Executable:** `pwsh` (PowerShell 7+, available on all platforms)
- **File:** `mRemoteNG.Protocols/PowerShell/PowerShellProtocol.cs`
- Spawn `pwsh` subprocess
- Embed in terminal emulator
- PSRemoting via SSH transport (on Linux/macOS)

---

### 3.7 Serial Protocol

#### Task 3.7.1 — Cross-platform serial
- **Library:** `System.IO.Ports` (built-in, cross-platform in .NET)
- **File:** `mRemoteNG.Protocols/Serial/SerialProtocol.cs`
- Use same terminal emulator
- Port enumeration: `/dev/ttyUSB*` on Linux, `/dev/tty.*` on macOS

---

### 3.8 External Application Protocols (AnyDesk, ARD)

#### Task 3.8.1 — Generic external app protocol
- **File:** `mRemoteNG.Protocols/External/ExternalAppProtocol.cs`
- Configurable executable path per platform
- Pass connection args
- Optional window embedding

---

## Phase 4 — Integration, Polish & Packaging

**Goal:** Full testing, platform packaging, CI/CD.
**Duration:** 2–3 months
**Risk:** Low.

### 4.1 Testing

#### Task 4.1.1 — Cross-platform unit tests
- Update all test projects to `net10.0`
- Remove Windows-specific test assumptions
- Add platform skip attributes where needed: `[SkipOnPlatform(TestPlatforms.Windows)]`
- Target: 80%+ code coverage on Core

#### Task 4.1.2 — Integration tests
- **File:** `mRemoteNGTests/Integration/`
- SSH connection test (against test Docker container)
- VNC connection test
- Settings save/load round-trip
- Encryption/decryption round-trip

#### Task 4.1.3 — UI automation tests
- **Package:** `Avalonia.Headless.NUnit`
- Screenshot comparison tests
- Connection tree interaction tests
- Dialog open/close tests

---

### 4.2 Linux Packaging

#### Task 4.2.1 — AppImage
- **Tool:** `appimagetool`
- **File:** `packaging/linux/AppRun`
- Self-contained: bundles .NET runtime
- Target: Ubuntu 22.04+, Fedora 38+

#### Task 4.2.2 — Flatpak
- **File:** `packaging/linux/org.mRemoteNG.mRemoteNG.yml`
- Freedesktop runtime
- Sandboxed permissions

#### Task 4.2.3 — Snap
- **File:** `packaging/linux/snap/snapcraft.yaml`
- Strict confinement
- Auto-update via Snap store

#### Task 4.2.4 — Debian package (.deb)
- **File:** `packaging/linux/debian/`
- For Ubuntu/Debian apt repos

---

### 4.3 macOS Packaging

#### Task 4.3.1 — macOS `.app` bundle
- **File:** `packaging/macos/Info.plist`
- Code signing with Apple Developer ID
- Notarization via `xcrun altool`

#### Task 4.3.2 — DMG installer
- **Tool:** `create-dmg`
- **File:** `packaging/macos/create-dmg.sh`
- Drag-to-Applications UI

#### Task 4.3.3 — Homebrew cask
- **File:** `packaging/macos/mremoteng.rb`
- Submit to Homebrew Cask

---

### 4.4 CI/CD Pipeline

#### Task 4.4.1 — Multi-platform build matrix
- **File:** `.github/workflows/cross-platform.yml`
- Build on: `ubuntu-latest`, `macos-latest`, `windows-latest`
- Test on all platforms
- Artifact upload per platform

#### Task 4.4.2 — Nightly builds
- Automated nightly builds for all platforms
- Published to GitHub releases as pre-releases

#### Task 4.4.3 — Code signing pipeline
- Windows: Authenticode signing
- macOS: Apple code signing + notarization
- Linux: GPG signing of packages

---

### 4.5 Documentation

#### Task 4.5.1 — Update README
- Cross-platform installation instructions
- Linux prerequisites (xfreerdp, libsecret, etc.)
- macOS prerequisites (freerdp via Homebrew)

#### Task 4.5.2 — User migration guide
- How settings migrate from old Windows registry installation
- How to import existing connection files

#### Task 4.5.3 — Developer guide
- How to build on each platform
- Platform abstraction architecture
- How to add a new protocol

---

## Dependencies Reference

### New packages to add
| Package | Version | Purpose |
|---------|---------|---------|
| `Avalonia` | 11.x | Core UI framework |
| `Avalonia.Desktop` | 11.x | Desktop app support |
| `Avalonia.Themes.Fluent` | 11.x | Default theme |
| `Avalonia.ReactiveUI` | 11.x | MVVM support |
| `ReactiveUI` | 20.x | MVVM framework |
| `Dock.Avalonia` | 0.14.x | Docking panels |
| `Avalonia.WebView` | 0.1.x | Cross-platform web view |
| `VtNetCore` | latest | Terminal emulator core |
| `Microsoft.Extensions.DependencyInjection` | 10.x | DI container |
| `Microsoft.Extensions.Logging` | 10.x | Logging abstraction |

### Packages to remove
| Package | Reason |
|---------|--------|
| `DockPanelSuite` | WinForms-only; replaced by Dock.Avalonia |
| `Microsoft.Web.WebView2` | Windows-only; replaced by Avalonia.WebView |
| `Microsoft-WindowsAPICodePack-Shell` | Windows Shell API, not needed |
| `System.Windows.Extensions` | Windows-only graphics |
| `Microsoft.Xaml.Behaviors.Wpf` | WPF-only |

### Packages to keep (already cross-platform)
| Package | Purpose |
|---------|---------|
| `SSH.NET` / `Renci.SshNet.Async` | SSH protocol |
| `VncSharpCore` | VNC protocol |
| `BouncyCastle.Cryptography` | Cryptography |
| `Newtonsoft.Json` | JSON serialization |
| `Google.Protobuf` | Protobuf serialization |
| `LiteDB` | Embedded database |
| `MySql.Data` | MySQL connector |
| `Microsoft.Data.SqlClient` | SQL Server |
| `log4net` | Logging |
| `NUnit` | Testing |
| `NSubstitute` | Test mocking |

---

## Risk Register

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| FreeRDP integration complexity | High | High | Use subprocess+embed approach; fallback to external window |
| Avalonia terminal emulator quality | Medium | High | Evaluate VtNetCore, XtermSharp; may need custom impl |
| Window embedding on Wayland (Linux) | High | Medium | Use XWayland for X11 apps; native Wayland for Avalonia |
| macOS Gatekeeper/Notarization | Low | Medium | Automate signing in CI |
| RDP feature parity gaps | High | Medium | Document limitations; keep Windows-only MSTSCLib as option |
| SSH.NET missing PuTTY-specific features | Low | Low | Fallback to PuTTY binary on Windows |
| Performance regression (VNC rendering) | Medium | Medium | Profile Avalonia rendering; use GPU acceleration |
| Breaking changes in existing configs | Low | High | Maintain XML schema backward compatibility |

---

## Milestone Checklist

### Milestone 1 (End of Phase 1)
- [ ] Project compiles on Linux without errors
- [ ] Project compiles on macOS without errors
- [ ] All settings read/write without registry
- [ ] All encryption works without DPAPI
- [ ] DI container wires up platform services

### Milestone 2 (End of Phase 2)
- [ ] Main window renders on all 3 platforms
- [ ] Connection tree functional
- [ ] Settings dialog functional
- [ ] All dialogs functional
- [ ] Theme switching works

### Milestone 3 (End of Phase 3)
- [ ] SSH connections work on Linux
- [ ] SSH connections work on macOS
- [ ] VNC connections work cross-platform
- [ ] RDP connections work on Linux (FreeRDP)
- [ ] HTTP/HTTPS rendering works cross-platform

### Milestone 4 (End of Phase 4)
- [ ] AppImage built and tested on Ubuntu
- [ ] DMG built and tested on macOS Ventura+
- [ ] Windows installer still works
- [ ] CI/CD pipeline builds all platforms on every PR
- [ ] 80%+ test coverage on Core

---

*Plan created: March 10, 2026*
*Based on codebase analysis of mRemoteNG v1.78.2-dev (526 C# source files)*
