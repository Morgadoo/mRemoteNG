# Plugin build and install workflow

This repository now separates the app solution from the plugin solution.

Plugins are built against the shared contracts assembly `mRp.Contracts.dll` (`mRemoteNG.PluginContracts`). Both apps load them:

- the Windows (WinForms) app loads every kind of plugin;
- the cross-platform (Avalonia) app loads plugins that don't use WinForms: connection properties, address resolvers and tree context actions. Tool-window plugins (`IToolWindowPlugin`) build a WinForms control, so it lists them in Options ▸ Plugins as "Windows app only" and doesn't load them.

`mRp.Contracts` targets both `net10.0-windows10.0.26100.0` (with `IToolWindowPlugin`) and `net10.0` (without it). A plugin that targets both frameworks too, like `mRp.MultiAddress`, works in both apps.

| Plugin | Kinds | Windows app | Cross-platform app |
|---|---|---|---|
| `mRp.MultiAddress` | Connection properties, address resolver | Yes | Yes (`net10.0` build) |
| `mRp.PortScan` | Tool window | Yes | No (WinForms) |
| `mRp.SshTransfer` | Tool window | Yes | No (WinForms; the cross-platform app has its own SSH file transfer dialog) |
| `mRp.AWS` | Connection properties, address resolver | Yes | No (uses `ExternalConnectors`; the cross-platform app has a built-in AWS EC2 address provider) |

## Build the plugins

From the repository root on Windows using the Visual Studio 2026 toolchain:

```powershell
& "$env:ProgramFiles(x86)\Microsoft Visual Studio\2026\Enterprise\MSBuild\Current\Bin\MSBuild.exe" mRemoteNG.Plugins.sln /p:Configuration=Release /p:Platform=x64
```

If you do not use the Enterprise edition folder name, replace it with your installed VS2026 edition (Community, Professional, or Enterprise).

This builds the shared contract assembly and the plugin assemblies:

- `mRp.Contracts.dll`
- `mRp.PortScan.dll`
- `mRp.SshTransfer.dll`
- `mRp.AWS.dll`
- `mRp.MultiAddress.dll`

On Linux or macOS, build a cross-platform plugin for `net10.0`:

```bash
dotnet build mRemoteNG.Plugins.MultiAddress/mRemoteNG.Plugins.MultiAddress.csproj -c Release -f net10.0
```

## Install into the Windows app

After building, copy the generated plugin DLLs into the app's default plugin folder, while keeping the shared contracts assembly in the app's `Assemblies` folder:

The example below uses the x64 output path. For ARM64 builds, replace `x64` with `arm64`.

```powershell
Copy-Item .\mRemoteNG.PluginContracts\bin\x64\Release\net10.0-windows10.0.26100.0\mRp.Contracts.dll .\mRemoteNG\bin\x64\Release\Assemblies\
Copy-Item .\mRemoteNG.Plugins.PortScan\bin\x64\Release\net10.0-windows10.0.26100.0\mRp.PortScan.dll .\mRemoteNG\bin\x64\Release\Plugins\
Copy-Item .\mRemoteNG.Plugins.SshTransfer\bin\x64\Release\net10.0-windows10.0.26100.0\mRp.SshTransfer.dll .\mRemoteNG\bin\x64\Release\Plugins\
Copy-Item .\mRemoteNG.Plugins.AWS\bin\x64\Release\net10.0-windows10.0.26100.0\mRp.AWS.dll .\mRemoteNG\bin\x64\Release\Plugins\
Copy-Item .\mRemoteNG.Plugins.MultiAddress\bin\x64\Release\net10.0-windows10.0.26100.0\mRp.MultiAddress.dll .\mRemoteNG\bin\x64\Release\Plugins\
```

The app looks for plugin DLLs in:

```text
<app output folder>\Plugins
```

If the app is configured with a custom plugin folder in settings, that folder is used instead.

## Install into the cross-platform app

Copy the plugin's `net10.0` DLL (and any dependencies it ships) into one of these folders:

- `Plugins` next to the app executable;
- `Plugins` in the settings folder (Options ▸ Plugins ▸ Open plugin folder);
- any folder listed in Options ▸ Plugins ▸ Additional plugin folders (separate folders with `;`).

The app provides `mRp.Contracts.dll`, so don't copy it next to the plugin. Each plugin DLL loads in its own load context; its other dependencies are found through its `.deps.json` or next to it.

Options ▸ Plugins lists what was found and why a plugin didn't load (disabled, needs a newer version of the app, duplicate id, Windows app only, failed). Plugins can be switched off there. Plugins load once when the app starts, so changes apply after a restart. Plugin messages go to the log panel, and warnings and errors also show as notifications.

In the cross-platform app:

- connection properties appear on a Plugins page of the connection editor, for connections (not folders or the default connection) using one of the plugin's protocols, and are saved with the connection file;
- address resolvers run just before connecting and change only the address and credentials used for that connection; the saved hostname stays as it is;
- tree context actions appear in the connection tree's context menu below Wake-on-LAN;
- connections a plugin imports are added at the current position in the tree.

## Important notes

- Keep `mRp.Contracts.dll` in the Windows app's `Assemblies` directory so it resolves like the other runtime dependencies.
- Do not keep plugin projects inside the main `mRemoteNG.sln` unless you intentionally want the app solution to build them too.
- The app project does not reference the plugin projects directly anymore; it loads them from the plugin folder at runtime.
