// Detects first run after migration from registry (Windows only)
// Imports registry settings to XML on first run
using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using mRemoteNG.Platform;

namespace mRemoteNG.Config.Settings;

public static class SettingsMigrationHelper
{
    private const string MigratedFlagSection = "Migration";
    private const string MigratedFlagKey = "RegistryMigrationCompleted";

    public static void MigrateFromRegistryIfNeeded(ISettingsProvider provider)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (provider.GetValue<bool>(MigratedFlagSection, MigratedFlagKey, false)) return;
        ImportFromRegistry(provider);
        provider.SetValue<bool>(MigratedFlagSection, MigratedFlagKey, true);
        provider.Save();
    }

    [SupportedOSPlatform("windows")]
    private static void ImportFromRegistry(ISettingsProvider provider)
    {
        const string regPath = @"Software\mRemoteNG";
        using var key = Registry.CurrentUser.OpenSubKey(regPath);
        if (key == null) return;
        foreach (var valueName in key.GetValueNames())
        {
            var value = key.GetValue(valueName);
            if (value != null)
                provider.SetValue<string>("Registry", valueName, value.ToString() ?? string.Empty);
        }
    }
}
