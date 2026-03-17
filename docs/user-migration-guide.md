# mRemoteNG Cross-Platform Migration Guide

This guide helps existing Windows users migrate to the cross-platform version of mRemoteNG, and helps new users on Linux and macOS understand how the application stores data on their platform.

---

## 1. What Changed

mRemoteNG has been rebuilt on top of .NET 10 and the Avalonia UI framework, replacing the Windows-only WinForms/MSTSCLib stack. The application now runs natively on Windows, Linux (X11 and Wayland), and macOS without emulation or compatibility layers.

Key architectural changes:

- **UI layer**: WinForms replaced by Avalonia — the interface looks and behaves the same across all platforms.
- **SSH protocol**: PuTTYNG.exe replaced by SSH.NET, an embedded managed SSH library. No external binary is required.
- **RDP protocol**: On Linux and macOS, the Microsoft RDP ActiveX control (mstscax.dll) is replaced by FreeRDP (`xfreerdp`). On Windows the original control remains available.
- **Settings storage**: Windows registry keys are replaced by an XML configuration file on all platforms.
- **Credential storage**: Windows DPAPI encryption is replaced by AES-256-GCM with a per-user keyfile on Linux and macOS.

---

## 2. Settings Migration

On Windows, mRemoteNG previously stored application settings (theme, panel layout, update preferences, etc.) in the Windows registry under `HKCU\Software\mRemoteNG`.

Starting with the cross-platform release, settings are stored in a portable XML file:

| Platform | Settings file location |
|----------|------------------------|
| Windows  | `%APPDATA%\mRemoteNG\settings.xml` |
| Linux    | `~/.config/mRemoteNG/settings.xml` |
| macOS    | `~/Library/Application Support/mRemoteNG/settings.xml` |

**Automatic migration on first run**: When you launch the cross-platform build on Windows for the first time, mRemoteNG detects registry-based settings from a previous installation and automatically imports them into `settings.xml`. No manual action is required. A backup of the original registry values is written to `%APPDATA%\mRemoteNG\registry-backup.xml` before migration.

On Linux and macOS there are no registry values to migrate; a fresh `settings.xml` is created with defaults.

---

## 3. Connection File Location

Your connection file (`confCons.xml`) stores all saved connections, folders, and their configuration. Its default location is:

| Platform | Default path |
|----------|--------------|
| Windows  | `%APPDATA%\mRemoteNG\confCons.xml` |
| Linux    | `~/.config/mRemoteNG/confCons.xml` |
| macOS    | `~/Library/Application Support/mRemoteNG/confCons.xml` |

**Moving your connections to Linux or macOS**: Copy `confCons.xml` from your Windows machine to the appropriate path above. mRemoteNG will load it on next launch. Connections that rely on Windows-only protocols (such as those using mstscax.dll directly) will automatically fall back to FreeRDP on non-Windows platforms.

The connection file format is identical across platforms — the same file can be opened on Windows, Linux, and macOS without conversion.

---

## 4. Credential Storage

mRemoteNG can store passwords for saved connections inside `confCons.xml`. On Windows, these were optionally protected using the Windows Data Protection API (DPAPI), which ties encryption to the Windows user account.

Because DPAPI is Windows-specific, the cross-platform release uses **AES-256-GCM** encryption with a per-user keyfile:

| Platform | Keyfile location |
|----------|-----------------|
| Windows  | `%APPDATA%\mRemoteNG\mremoteng.key` |
| Linux    | `~/.config/mRemoteNG/mremoteng.key` |
| macOS    | `~/Library/Application Support/mRemoteNG/mremoteng.key` |

**Transferring passwords from Windows to Linux/macOS**:

1. On Windows, open mRemoteNG and export your connection file with the **Save passwords in plain text** option (File > Export Connections > uncheck encryption). Keep this file secure.
2. Copy the exported file to your Linux or macOS machine.
3. Open mRemoteNG on the new platform, import the file, then re-enable password encryption via Tools > Options > Security.
4. mRemoteNG will re-encrypt stored passwords using the AES-256-GCM keyfile.
5. Delete the plain-text export once the migration is complete.

If the `mremoteng.key` file is lost, encrypted passwords cannot be recovered. Back up this file alongside `confCons.xml`.

---

## 5. PuTTY Sessions

On Windows, mRemoteNG could import PuTTY saved sessions from the Windows registry (`HKCU\Software\SimonTatham\PuTTY\Sessions`).

On Linux and macOS, PuTTY sessions do not exist in the same form. Instead, mRemoteNG reads `~/.ssh/config` to import SSH host entries on first launch. Each `Host` block in your SSH config is offered as a connection that can be saved to `confCons.xml`.

**Auto-import on first run**: When mRemoteNG starts on Linux or macOS and finds no existing `confCons.xml`, it scans `~/.ssh/config` and presents a dialog offering to import recognised hosts. You can accept, skip individual hosts, or decline the import entirely.

On Windows, the existing PuTTY registry import wizard is still available under File > Import > PuTTY Sessions.

---

## 6. Protocol Changes

| Protocol | Windows | Linux / macOS |
|----------|---------|---------------|
| RDP | mstscax.dll (ActiveX) or FreeRDP | FreeRDP (`xfreerdp`) |
| SSH / SFTP | SSH.NET (embedded) | SSH.NET (embedded) |
| VNC | LibVNCSharp | LibVNCSharp |
| Telnet | Rebex Terminal Emulator | Rebex Terminal Emulator |
| HTTP/HTTPS | Embedded browser (WebView2 on Windows, WebKitGTK on Linux) | WebKitGTK |

SSH connections no longer launch an external PuTTYNG.exe process. All SSH, SFTP, and SCP transfers are handled by the embedded SSH.NET library, which provides key-based authentication, agent forwarding, and SFTP file browsing without any external dependency.

---

## 7. Known Limitations

- **RDP window embedding**: On Linux and macOS, RDP sessions using FreeRDP run in a floating window rather than being embedded in the mRemoteNG tab strip. Tabbed embedding requires the Windows RDP ActiveX control (mstscax.dll) and is not available outside Windows.
- **MSTSCLib**: The MSTSCLib protocol type (direct use of the Microsoft Terminal Services Client COM control) is Windows-only and will not appear in the protocol list on Linux or macOS.
- **Windows Credential Manager**: Saved credentials from the Windows Credential Manager are not imported automatically. Passwords stored via this method must be re-entered after migration.
- **Serial port connections**: Serial port support depends on the operating system exposing the device (e.g., `/dev/ttyUSB0` on Linux). Some USB-to-serial adapters may require additional drivers.
