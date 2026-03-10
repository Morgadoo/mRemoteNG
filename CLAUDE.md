# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

mRemoteNG is an open-source, multi-protocol, tabbed remote connections manager. It supports RDP, VNC, SSH, Telnet, HTTP/HTTPS, rlogin, Raw Socket, PowerShell remoting, and AnyDesk. Currently migrating from Windows-only WinForms to cross-platform Avalonia UI.

## Build Commands

```bash
# Restore packages
dotnet restore

# Build full solution (x64)
msbuild mRemoteNG.sln -p:Configuration=Debug -p:Platform=x64

# Build specific projects
dotnet build mRemoteNG.Platform/mRemoteNG.Platform.csproj
dotnet build mRemoteNG.Protocols/mRemoteNG.Protocols.csproj
dotnet build mRemoteNG.Avalonia/mRemoteNG.Avalonia.csproj
dotnet build mRemoteNG/mRemoteNG.csproj  # Windows-only (WinForms)

# Run tests
dotnet test mRemoteNGTests/mRemoteNGTests.csproj              # Legacy NUnit (Windows-only)
dotnet test mRemoteNG.Tests.CrossPlatform/mRemoteNG.Tests.CrossPlatform.csproj  # xUnit (all platforms)

# Run a single test by filter
dotnet test mRemoteNGTests/mRemoteNGTests.csproj --filter "FullyQualifiedName~ClassName.MethodName"
dotnet test mRemoteNG.Tests.CrossPlatform/mRemoteNG.Tests.CrossPlatform.csproj --filter "FullyQualifiedName~TestName"

# Test with coverage
dotnet test --collect:"XPlat Code Coverage"
```

**Target**: .NET 10.0 (`net10.0-windows10.0.26100.0` for WinForms projects, `net10.0` for cross-platform). Platforms: x64 and ARM64.

## Architecture

### Dual-UI Strategy (Migration In Progress)

The codebase has two UI layers running in parallel during migration:
- **mRemoteNG/** — Legacy WinForms app (Windows-only, `net10.0-windows`)
- **mRemoteNG.Avalonia/** — New cross-platform Avalonia UI (`net10.0`)

### Solution Projects

| Project | Purpose | Target |
|---------|---------|--------|
| `mRemoteNG` | Legacy WinForms main app | net10.0-windows |
| `mRemoteNG.Platform` | Cross-platform service abstractions (interfaces) | net10.0 |
| `mRemoteNG.Platform.Windows` | Windows service implementations (DPAPI, Win32) | net10.0 |
| `mRemoteNG.Platform.Linux` | Linux service implementations | net10.0 |
| `mRemoteNG.Platform.Mac` | macOS service implementations | net10.0 |
| `mRemoteNG.Avalonia` | Cross-platform Avalonia UI | net10.0 |
| `mRemoteNG.Protocols` | Protocol abstractions and implementations | net10.0 |
| `ExternalConnectors` | External protocol connectors | net10.0-windows |
| `ObjectListView.NetCore` | Custom list view control | Any CPU |
| `mRemoteNGTests` | Legacy NUnit tests (Windows) | net10.0-windows |
| `mRemoteNG.Tests.CrossPlatform` | New xUnit tests (all platforms) | net10.0 |

### Platform Abstraction Layer

`mRemoteNG.Platform` defines interfaces (`IClipboardService`, `IWindowService`, `IProcessService`, `ISettingsProvider`, `ICryptoProvider`, `INotificationService`, `ISystemTrayService`, `IPuttySessionsProvider`). Platform-specific assemblies are loaded at runtime via reflection in `PlatformServiceFactory.cs` using `RuntimeInformation.IsOSPlatform()`. Each platform assembly has a `PlatformRegistrar.Register(IServiceCollection)` static method. DI uses Microsoft.Extensions.DependencyInjection.

Crypto is platform-specific: DPAPI on Windows (`DpapiCryptoProvider`), OpenSSL-based on Linux (`LinuxCryptoProvider`), Keychain on Mac (`MacCryptoProvider`). Cross-platform fallback: `AesGcmCryptoProvider`.

### Connection/Protocol System

- **`ConnectionInfo`** (in `mRemoteNG/Connection/`): Core model with 200+ properties for connection configuration
- **`ConnectionInfoInheritance`**: Each property has an `Inherit<PropertyName>` boolean — child connections inherit from parent containers
- **`IProtocol`** (in `mRemoteNG.Protocols/Abstractions/`): Unified interface for all protocols with state machine (Disconnected→Connecting→Connected→Reconnecting→Error)
- **`ConnectionParameters`**: Sealed record with hostname, port, credentials, and protocol-specific extras dictionary
- Visual protocols implement `IVisualProtocol` with `CreateView()` returning Avalonia controls

### Serialization & Backward Compatibility

XML connection files use versioned serializers in `mRemoteNG/Config/Serializers/ConnectionSerializers/Xml/`:
- `XmlConnectionNodeSerializer28` is the latest (version 2.8)
- Deserializers handle all older versions (2.5–2.7)
- **Critical rule**: Never break loading of old connection files. Always provide default values for new properties.
- Supports full-document encryption and per-field password encryption via `ICryptographyProvider`
- Also supports CSV and SQL serialization backends

### Package Management

Dependencies are centrally managed in `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`.

## Code Style

Defined in `mRemoteNG/.editorconfig`:
- 4 spaces indentation, CRLF line endings, UTF-8 with BOM
- Allman-style braces (opening brace on new line)
- No `this.` qualifier
- Sort System directives first
- PascalCase for classes, methods, properties, constants; camelCase with `_` prefix for private fields

## Key Conventions

### Adding a New Connection Property

1. Add enum (if needed) in `Connection/`
2. Add property to `AbstractConnectionRecord.cs` or `ConnectionInfo.cs` with `[Category]` and `[Description]` attributes
3. Add inheritance flag in `ConnectionInfoInheritance.cs`
4. Update serializers: XML (`XmlConnectionNodeSerializer28` + deserializer), CSV, SQL
5. Add localization string in `Language/Language.resx`

### Localization

All user-facing strings go in `Language/Language.resx`. Access via `Language.ResourceName`. Naming: `PropertyDescription<Name>` for tooltips, `<EnumName><Value>` for enum display names.

### Testing

- Legacy tests: NUnit 4.5.1 with NSubstitute — naming: `MethodName_Scenario_ExpectedBehavior`
- New cross-platform tests: xUnit with FluentAssertions
- Mirror the main project directory structure in test projects
