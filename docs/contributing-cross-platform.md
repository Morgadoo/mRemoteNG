# Contributing to mRemoteNG — Cross-Platform Development

This guide covers everything you need to build, test, and extend mRemoteNG on Windows, Linux, and macOS. For general contribution guidelines (branching strategy, code review, commit messages) see the [Wiki](https://github.com/mRemoteNG/mRemoteNG/wiki).

---

## 1. Prerequisites

### All platforms

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — `dotnet --version` should report `10.0.x`
- Git 2.40+
- An IDE: Visual Studio 2022 (Windows), JetBrains Rider, or VS Code with the C# Dev Kit extension

### Linux

```bash
# Debian/Ubuntu (.NET from Microsoft's packages or https://dot.net/v1/dotnet-install.sh)
sudo apt-get install dotnet-sdk-10.0 freerdp3-x11 xclip wl-clipboard

# Fedora
sudo dnf install dotnet-sdk-10.0 freerdp xclip wl-clipboard

# Arch
sudo pacman -S dotnet-sdk freerdp xclip wl-clipboard
```

FreeRDP is only needed to run RDP sessions; `xclip`/`wl-clipboard` provide clipboard access (X11/Wayland).
RDP embedding needs X11 or XWayland.

### macOS

```bash
brew install dotnet freerdp
```

### Windows

- Visual Studio 2022 with the ".NET desktop development" workload, **or** the .NET 10 SDK standalone
- For RDP sessions in the cross-platform app, install FreeRDP (`wfreerdp.exe` on `PATH` or in
  `C:\Program Files\FreeRDP\`)

---

## 2. Build Instructions

On Linux and macOS, build the cross-platform projects (the solution also contains the Windows-only WinForms
app, which only builds on Windows with Visual Studio's MSBuild):

```bash
git clone https://github.com/mRemoteNG/mRemoteNG.git
cd mRemoteNG
dotnet build mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj
dotnet run --project mRemoteNG.Avalonia -- path/to/confCons.xml   # optional file to open
```

On Windows, `msbuild mRemoteNG.sln -restore -p:Configuration=Debug -p:Platform=x64` builds everything.

### Windows

- Visual Studio 2022 with the ".NET desktop development" workload, **or** the .NET 10 SDK standalone
- No additional native libraries are required; FreeRDP and mstscax.dll paths are resolved at runtime

---

## 2. Build Instructions

Clone the repository and build the solution:

```bash
git clone https://github.com/mRemoteNG/mRemoteNG.git
cd mRemoteNG
dotnet build mRemoteNG.sln
```

To produce a self-contained executable for your current platform:

```bash
# Linux x64
dotnet publish mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj \
  -c Release -r linux-x64 --self-contained true -o out/

# macOS arm64 (Apple Silicon)
dotnet publish mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj \
  -c Release -r osx-arm64 --self-contained true -o out/

# Windows x64
dotnet publish mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj \
  -c Release -r win-x64 --self-contained true -o out/
```

---

## 3. Running Tests

| Project | Framework | Purpose |
|---------|-----------|---------|
| `mRemoteNGTests` | NUnit, Windows only | Legacy WinForms app |
| `mRemoteNG.Tests.CrossPlatform` | xUnit | Core, Platform and Protocols: unit tests and integration tests against real servers |
| `mRemoteNG.Avalonia.Tests` | xUnit + Avalonia.Headless | The real main window and view-models, rendered headlessly (no display needed) |

```bash
dotnet test mRemoteNG.Tests.CrossPlatform/mRemoteNG.Tests.CrossPlatform.csproj
dotnet test mRemoteNG.Avalonia.Tests/mRemoteNG.Avalonia.Tests.csproj
```

Integration tests skip themselves (`[SkippableFact]`) when their server is not available:

| Tests | Needs |
|-------|-------|
| SSH / SFTP | root, `/usr/sbin/sshd`, free port 2222 (the test starts its own sshd) |
| VNC | `Xvnc` and `vncpasswd` (TigerVNC); uses display :61 / port 5961 |
| Telnet | `socat` and inetutils `telnetd` |
| RDP | `xfreerdp3` and `RDP_TEST_HOST`, `RDP_TEST_USER`, `RDP_TEST_PASS` (e.g. a local xrdp); `RDP_TEST_NLA=true` for a Windows host with NLA |

On Debian/Ubuntu: `sudo apt-get install openssh-server tigervnc-standalone-server socat inetutils-telnetd xrdp freerdp3-x11`.

For everything at once on Ubuntu 24.04 (also MariaDB, OpenLDAP, OpenBao, x11vnc, pwsh and an xrdp test user), run
`ci/linux-integration-setup.sh`, then the tests as root with the settings it writes to
`/var/tmp/mrng-integration/env.sh`. The "Integration Tests (real servers)" workflow does the same and fails when a
test is skipped that `ci/expected-skips-linux.txt` does not list (`ci/check-test-skips.py`).

Code coverage:

```bash
dotnet test mRemoteNG.Tests.CrossPlatform/ --collect:"XPlat Code Coverage" --results-directory ./coverage/
```

---

## 4. Project Structure

```
mRemoteNG.sln
├── mRemoteNG.Core/                  # Domain model, confCons.xml/CSV serializers, import/export, settings model
├── mRemoteNG.Platform/              # Platform service interfaces + shared implementations
├── mRemoteNG.Platform.Windows/      # Windows: DPAPI, Win32 clipboard/windows, registry PuTTY sessions
├── mRemoteNG.Platform.Linux/        # Linux: XDG settings, keyfile crypto, xclip/wl-clipboard, notify-send
├── mRemoteNG.Platform.Mac/          # macOS: settings, keyfile crypto, pbcopy/pbpaste, osascript
├── mRemoteNG.Protocols/             # SSH.NET, managed RFB (VNC), FreeRDP embedding, Telnet/Rlogin/Raw, …
├── mRemoteNG.Avalonia/              # Avalonia app: views, view-models, services, entry point
├── mRemoteNG.Avalonia.Tests/        # Headless UI tests
├── mRemoteNG.Tests.CrossPlatform/   # Cross-platform unit and integration tests
├── mRemoteNG/ , mRemoteNGTests/     # Legacy WinForms app and its tests (Windows only)
```

`mRemoteNG.Core` must not reference any platform-specific APIs or NuGet packages that only work on one OS. All OS-specific code belongs in the appropriate `mRemoteNG.Platform.*` project.

---

## 5. Adding a New Platform Service

Platform services abstract OS-specific capabilities (e.g., secure credential storage, clipboard integration, update checks) behind interfaces defined in `mRemoteNG.Core`.

**Step 1** — Define the interface in `mRemoteNG.Core/Services/`:

```csharp
// mRemoteNG.Core/Services/ISecureStorage.cs
public interface ISecureStorage
{
    void Save(string key, string value);
    string? Load(string key);
    void Delete(string key);
}
```

**Step 2** — Implement it in the relevant platform project:

```csharp
// mRemoteNG.Platform.Linux/Services/LinuxSecureStorage.cs
public class LinuxSecureStorage : ISecureStorage
{
    public void Save(string key, string value) { /* libsecret / keyfile */ }
    public string? Load(string key) { /* ... */ }
    public void Delete(string key) { /* ... */ }
}
```

**Step 3** — Register it in `PlatformServiceFactory` (in `mRemoteNG.Platform`):

```csharp
// mRemoteNG.Platform/PlatformServiceFactory.cs
public static ISecureStorage CreateSecureStorage() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new WindowsSecureStorage()
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new MacSecureStorage()
            : new LinuxSecureStorage();
```

**Step 4** — Write cross-platform unit tests in `mRemoteNG.Tests.CrossPlatform/` mocking the interface, plus a real integration test guarded by `[SkippableFact]` when the platform is unavailable.

---

## 6. Adding a New Protocol

Protocols are implemented in `mRemoteNG.Protocols` and registered in `ProtocolFactory`.

**Step 1** — Implement `IConnectionProtocol` in `mRemoteNG.Protocols/`:

```csharp
public class MyProtocol : IConnectionProtocol
{
    public string Name => "MyProtocol";
    public Task ConnectAsync(ConnectionInfo info, CancellationToken ct) { /* ... */ }
    public Task DisconnectAsync() { /* ... */ }
    public bool IsConnected { get; private set; }
}
```

**Step 2** — Register it in `ProtocolFactory`:

```csharp
// mRemoteNG.Protocols/ProtocolFactory.cs
_protocols.Register(ProtocolType.MyProtocol, () => new MyProtocol());
```

**Step 3** — Add the protocol type to the `ProtocolType` enum in `mRemoteNG.Core`.

**Step 4** — Add a connection panel / view-model in `mRemoteNG.Avalonia` if the protocol needs dedicated UI controls beyond the standard terminal.

**Step 5** — Add unit tests covering connection state transitions, and an integration test (guarded by environment variables) for live connectivity.

---

## 7. UI Development

The UI is built with [Avalonia](https://avaloniaui.net/) using the MVVM pattern powered by [ReactiveUI](https://www.reactiveui.net/).

**Key conventions**:

- Views live in `mRemoteNG.Avalonia/Views/` as `.axaml` + `.axaml.cs` pairs.
- ViewModels live in `mRemoteNG.Avalonia/ViewModels/` and inherit from `ReactiveObject`.
- Commands use `ReactiveCommand.CreateFromTask(...)` for async operations.
- Bindings use Avalonia's compiled binding syntax (`x:CompileBindings="True"`) where possible for compile-time safety.

**Themes**: mRemoteNG ships a light and a dark theme. Theme resources are defined in `mRemoteNG.Avalonia/Assets/Themes/`. When adding new controls, define colours using theme resource keys rather than hard-coded hex values:

```xml
<Border Background="{DynamicResource SystemControlBackgroundAltHighBrush}">
```

**Hot reload**: Avalonia supports XAML hot reload during development. Launch the app with `dotnet run` from the `mRemoteNG.Avalonia` directory and edit `.axaml` files; changes apply without a full rebuild.

**Testing UI logic**: ViewModels should be unit-testable without a running UI. Inject dependencies through the constructor; avoid static service locators. Use `NSubstitute` to mock `IConnectionProtocol`, platform services, and other interfaces in `mRemoteNG.Tests.CrossPlatform/`.

---

## Getting Help

- Open a discussion on [GitHub Discussions](https://github.com/mRemoteNG/mRemoteNG/discussions) for questions about architecture or implementation approach before investing significant effort.
- Join the community on [Matrix / Element](https://app.element.io/#/room/#mremoteng:matrix.org) for real-time chat.
- Check existing issues labelled `cross-platform` or `help wanted` for a starting point.
