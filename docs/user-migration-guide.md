# mRemoteNG Cross-Platform Migration Guide

This guide is for Windows users moving to the cross-platform (Avalonia) version of mRemoteNG, and for new
users on Linux and macOS. It explains what changed, where data is stored, and what is not available yet.

---

## 1. What Changed

The cross-platform app is built on .NET 10 and Avalonia and runs on Windows, Linux and macOS. The classic
WinForms app is unchanged and remains Windows-only.

- **UI:** Avalonia instead of WinForms. Dark, Light and "follow the system" themes.
- **SSH / SFTP:** handled in-process by SSH.NET; PuTTYNG.exe is not used. Host keys are checked against
  `known_hosts` (see section 5).
- **RDP:** FreeRDP (`xfreerdp3`/`xfreerdp` on Linux, `wfreerdp` on Windows, FreeRDP from Homebrew on macOS)
  instead of the Microsoft RDP ActiveX control.
- **VNC:** a built-in VNC (RFB) client; no external viewer needed.
- **Settings:** an XML file instead of the Windows registry.

---

## 2. Connection Files

The connection file format (`confCons.xml`) is the same on every platform, and the cross-platform app
reads and writes files that the WinForms app can open — and the other way round. Versions 2.5 to 2.8 are
supported, including:

- passwords stored in the file,
- a **master password** (you are asked for it when opening the file),
- **full-file encryption**,
- custom encryption settings (cipher, key-derivation iterations) — these are kept when the file is saved.

Passwords in `confCons.xml` are encrypted with the file's master password (or mRemoteNG's built-in default
key when no master password is set), **not** with Windows DPAPI. To move your connections to Linux or macOS,
copy `confCons.xml` and open it with **File > Open Connection File…**. No export or re-encryption is needed.

By default the app reopens the last connection file at startup. You can change this in
**Tools > Options > Startup & Exit**, or pass a file on the command line: `mremoteng /path/to/confCons.xml`.

Files older than 2.6 are saved back in the current format (2.8, AES-GCM) the first time you save them, as
the WinForms app also does.

---

## 3. Settings

Application settings are stored in an XML file:

| Platform | Settings directory |
|----------|--------------------|
| Windows  | `%APPDATA%\mRemoteNG\` |
| Linux    | `$XDG_CONFIG_HOME/mRemoteNG/` (normally `~/.config/mRemoteNG/`) |
| macOS    | `~/Library/Application Support/mRemoteNG/` |

The directory contains `settings.xml`, plus `credentials.xml`, `known_hosts` and (Linux/macOS) `.keyfile`
once those features are used.

Settings from the WinForms app's registry keys are **not** imported; the cross-platform app starts with
default settings. Review **Tools > Options** after the first start.

---

## 4. Credential Manager

Credentials saved in the Credential Manager (**Tools > Options > Credentials > Manage credentials…**) are
stored in `credentials.xml` in the settings directory, with file permissions restricted to your user. Their
passwords are encrypted per machine:

- **Windows:** DPAPI (tied to your Windows account).
- **Linux / macOS:** AES-256-GCM with a random key stored in `.keyfile` (permissions `0600`).

Because of this, `credentials.xml` is not portable between machines. Back up `.keyfile` together with
`credentials.xml` — without the key the stored passwords cannot be recovered. (Passwords in `confCons.xml`
are not affected; see section 2.)

---

## 5. SSH Host Keys

The first time you connect to an SSH server, mRemoteNG shows the server's key fingerprint
(`SHA256:…`) and asks whether to trust it. Accepted keys are saved to `known_hosts` in the settings
directory. Keys in your own `~/.ssh/known_hosts` are also trusted (that file is only read, never changed).

If a server's key **changes**, the connection is refused and a warning is shown. You can replace the stored
key explicitly if you know the change is legitimate (for example after reinstalling the server).

---

## 6. Importing Connections

**File > Import Connections…** imports into the whole tree or the selected folder:

| Source | Notes |
|--------|-------|
| mRemoteNG XML / CSV | Asks for the master password of protected files. |
| PuTTY sessions | Windows: registry. Linux/macOS: `~/.putty/sessions`. |
| OpenSSH config | `~/.ssh/config` host entries (wildcard patterns are skipped). |
| Remote Desktop Connection Manager (`.rdg`) | Passwords protected with Windows DPAPI are not imported. |
| Remote Desktop Connection (`.rdp`) | |
| Remote Desktop Manager (CSV export) | |
| SecureCRT (XML export) | Encrypted passwords are not imported. |

**File > Export Connections…** writes mRemoteNG XML (optionally with a new master password) or CSV, for the
whole tree or the selected folder, with or without credentials.

---

## 7. Protocols

| Protocol | Cross-platform implementation |
|----------|-------------------------------|
| RDP | FreeRDP, shown inside the tab on Windows and Linux (X11/XWayland); in a separate window on macOS |
| SSH / SFTP | SSH.NET with a built-in xterm-256color terminal; SFTP via **Tools > SFTP File Transfer…** |
| Telnet, Rlogin, Raw | Built-in terminal |
| VNC / Apple Remote Desktop | Built-in VNC client |
| HTTP / HTTPS | Opens in your default web browser |
| PowerShell | `pwsh` (PowerShell 7) |
| AnyDesk | Launches the AnyDesk client |

**RDP requirements:** install FreeRDP 3 for the best results (`freerdp3-x11` on Debian/Ubuntu,
`freerdp` on Fedora/Arch, `brew install freerdp` on macOS). With FreeRDP 3 the password is passed in a way
other users on the machine cannot see; FreeRDP 2 still works, but passes it on the command line.

---

## 8. Known Limitations

- **RDP:** embedding is not available on macOS (FreeRDP runs in its own window), and has only been tested on
  Linux so far. RD Gateway and Windows Server NLA scenarios have not been validated yet.
- **VNC:** Tight and Zlib encodings and VNC proxy settings are not supported.
- **HTTP/HTTPS** pages are not embedded in a tab.
- **External Tools** (and the "IntApp" protocol that uses them) are not available yet.
- **Rlogin** does not send window-size changes.
- **Serial ports** depend on the operating system exposing the device (e.g. `/dev/ttyUSB0`).
