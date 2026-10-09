using System.Runtime.InteropServices;

namespace mRemoteNG.Core;

/// <summary>
/// Lightweight runtime metadata used during bootstrap and diagnostics.
/// </summary>
public sealed class CoreRuntimeDescriptor
{
    public string FrameworkDescription => RuntimeInformation.FrameworkDescription;
    public string OsDescription => RuntimeInformation.OSDescription;
    public string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();
}
