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
# Debian/Ubuntu
sudo apt-get install dotnet-sdk-10 xfreerdp libgtk-3-dev libglib2.0-dev xclip

# Fedora
sudo dnf install dotnet-sdk-10 freerdp gtk3-devel glib2-devel xclip

# Arch
sudo pacman -S dotnet-sdk freerdp gtk3 glib2 xclip
```

Wayland sessions are supported. If you develop under Wayland and need X11 fallback for testing, install `xwayland`.

### macOS

```bash
# Install Homebrew if not already present
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"

brew install dotnet@10 freerdp

# XQuartz is required for X11-based testing and some RDP scenarios
brew install --cask xquartz
```

After installing XQuartz, log out and back in for the `DISPLAY` environment variable to be set correctly.

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

To build only the UI application:

```bash
dotnet build mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj -c Release
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

The solution contains two test projects:

| Project | Purpose |
|---------|---------|
| `mRemoteNG.Tests` | Existing Windows-oriented unit tests |
| `mRemoteNG.Tests.CrossPlatform` | Platform-agnostic unit and integration tests |

Run all cross-platform tests:

```bash
dotnet test mRemoteNG.Tests.CrossPlatform/
```

Run only unit tests (skip integration tests that require live services):

```bash
dotnet test mRemoteNG.Tests.CrossPlatform/ --filter "Category!=Integration"
```

Run integration tests with a live SSH server:

```bash
export SSH_TEST_HOST=192.168.1.10
export SSH_TEST_USER=testuser
export SSH_TEST_PASS=secret
dotnet test mRemoteNG.Tests.CrossPlatform/ --filter "Category=Integration"
```

To generate a code-coverage report:

```bash
dotnet test mRemoteNG.Tests.CrossPlatform/ \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage/
```

---

## 4. Project Structure

```
mRemoteNG.sln
├── mRemoteNG.Core/              # Domain models, interfaces, business logic (no UI, no OS calls)
├── mRemoteNG.Platform/          # Platform abstraction layer
│   ├── mRemoteNG.Platform.Windows/   # Windows implementations (DPAPI, registry, WMI)
│   ├── mRemoteNG.Platform.Linux/     # Linux implementations (libsecret, D-Bus, xdg-open)
│   └── mRemoteNG.Platform.Mac/       # macOS implementations (Keychain, NSWorkspace)
├── mRemoteNG.Protocols/         # Protocol implementations (SSH.NET, LibVNCSharp, FreeRDP interop)
├── mRemoteNG.Avalonia/          # Avalonia UI host — views, view-models, app entry point
├── mRemoteNG.Tests/             # Legacy unit tests (Windows-only acceptable here)
└── mRemoteNG.Tests.CrossPlatform/   # Cross-platform unit and integration tests
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
