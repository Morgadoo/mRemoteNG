using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>A local device FreeRDP can redirect: the name the server shows and the local path or device.</summary>
internal sealed record RdpLocalDevice(string Name, string Path);

/// <summary>
/// Local serial/parallel ports and fixed drives, discovered when a session starts so that the
/// legacy "redirect ports" and "redirect local drives" options can name them to FreeRDP (FreeRDP itself
/// does not enumerate ports, and <c>+drives</c> means every mount point, not just the fixed disks).
/// </summary>
internal sealed record RdpLocalDevices
{
    public static RdpLocalDevices Empty { get; } = new();

    public IReadOnlyList<RdpLocalDevice> SerialPorts { get; init; } = [];

    public IReadOnlyList<RdpLocalDevice> ParallelPorts { get; init; } = [];

    /// <summary>Fixed (non-removable, non-network) drives; only used on Windows.</summary>
    public IReadOnlyList<RdpLocalDevice> FixedDrives { get; init; } = [];

    /// <summary>Enumerates what the connection asks for; failures leave the list empty.</summary>
    public static RdpLocalDevices Discover(bool ports, bool fixedDrives)
    {
        var devices = new RdpLocalDevices();
        if (ports)
        {
            devices = devices with
            {
                SerialPorts = Safe(DiscoverSerialPorts),
                ParallelPorts = Safe(DiscoverParallelPorts),
            };
        }
        if (fixedDrives && OperatingSystem.IsWindows())
            devices = devices with { FixedDrives = Safe(DiscoverFixedDrives) };
        return devices;
    }

    private static IReadOnlyList<RdpLocalDevice> Safe(Func<IReadOnlyList<RdpLocalDevice>> discover)
    {
        try
        {
            return discover();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    private static IReadOnlyList<RdpLocalDevice> DiscoverSerialPorts()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows device names double as paths for FreeRDP's serial driver.
            return SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => new RdpLocalDevice(name.ToUpperInvariant(), name)).ToList();
        }

        IEnumerable<string> paths;
        if (OperatingSystem.IsLinux())
        {
            // /dev/ttyS0…31 always exist; a legacy UART is only present when the kernel detected one
            // (/sys/class/tty/ttySn/type != 0). USB adapters (ttyUSB/ttyACM) only exist when plugged in.
            paths = Directory.EnumerateFileSystemEntries("/sys/class/tty")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.StartsWith("ttyUSB", StringComparison.Ordinal)
                               || name.StartsWith("ttyACM", StringComparison.Ordinal)
                               || (name.StartsWith("ttyS", StringComparison.Ordinal) && HasUart(name)))
                .Select(name => "/dev/" + name)
                .Where(File.Exists);
        }
        else
        {
            paths = Directory.EnumerateFiles("/dev", "cu.*");
        }

        return Number(paths, "COM");
    }

    private static bool HasUart(string ttyName)
    {
        string typeFile = $"/sys/class/tty/{ttyName}/type";
        return File.Exists(typeFile) && File.ReadAllText(typeFile).Trim() is { Length: > 0 } type && type != "0";
    }

    private static IReadOnlyList<RdpLocalDevice> DiscoverParallelPorts()
    {
        if (OperatingSystem.IsWindows())
            return DiscoverWindowsParallelPorts();
        if (!OperatingSystem.IsLinux())
            return [];
        return Number(Directory.EnumerateFiles("/dev", "lp*").Where(p => p.Length > "/dev/lp".Length && char.IsAsciiDigit(p[^1])), "LPT");
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RdpLocalDevice> DiscoverWindowsParallelPorts()
    {
        // Values look like "\Device\Parallel0" = "\DosDevices\LPT1".
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\PARALLEL PORTS");
        if (key is null) return [];
        return key.GetValueNames()
            .Select(n => key.GetValue(n) as string)
            .OfType<string>()
            .Select(v => v[(v.LastIndexOf('\\') + 1)..])
            .Where(n => n.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => new RdpLocalDevice(n.ToUpperInvariant(), n))
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RdpLocalDevice> DiscoverFixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d => d.Name[..1].ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .Select(letter => new RdpLocalDevice(letter, letter + ":/"))
            .ToList();

    /// <summary>Names Unix devices COM1, COM2… (or LPT1…) in path order.</summary>
    private static List<RdpLocalDevice> Number(IEnumerable<string> paths, string prefix) =>
        paths.Order(StringComparer.Ordinal)
            .Select((path, i) => new RdpLocalDevice($"{prefix}{i + 1}", path))
            .ToList();
}
