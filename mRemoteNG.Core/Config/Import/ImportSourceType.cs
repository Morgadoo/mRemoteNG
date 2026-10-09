namespace mRemoteNG.Core.Config.Import
{
    public enum ImportSourceType
    {
        MRemoteNGXml,
        MRemoteNGCsv,
        PuttySessions,
        OpenSshConfig,
        RemoteDesktopConnectionManager,
        RemoteDesktopConnectionFile,
        RemoteDesktopManager,
        SecureCrt,
        ActiveDirectory
    }

    /// <summary>Describes an import source for the user interface.</summary>
    /// <param name="FilePatterns">File picker patterns, e.g. "*.xml".</param>
    /// <param name="SourceIsFolder">The source is a folder (PuTTY session files), not a file.</param>
    /// <param name="SourceIsDirectory">The source is a directory service (an LDAP URL chosen by browsing), not a file.</param>
    public sealed record ImportSourceDescriptor(
        ImportSourceType Type,
        string DisplayName,
        IReadOnlyList<string> FilePatterns,
        bool SourceIsFolder = false,
        bool SourceIsDirectory = false)
    {
        public static IReadOnlyList<ImportSourceDescriptor> All { get; } =
        [
            new(ImportSourceType.MRemoteNGXml, "mRemoteNG XML (.xml)", ["*.xml"]),
            new(ImportSourceType.MRemoteNGCsv, "mRemoteNG CSV (.csv)", ["*.csv"]),
            new(ImportSourceType.PuttySessions, "PuTTY saved sessions", [], SourceIsFolder: true),
            new(ImportSourceType.OpenSshConfig, "OpenSSH config (~/.ssh/config)", ["config", "*.conf", "*"]),
            new(ImportSourceType.RemoteDesktopConnectionManager, "Remote Desktop Connection Manager (.rdg)", ["*.rdg"]),
            new(ImportSourceType.RemoteDesktopConnectionFile, "Remote Desktop connection file (.rdp)", ["*.rdp"]),
            new(ImportSourceType.RemoteDesktopManager, "Remote Desktop Manager CSV export (.csv)", ["*.csv"]),
            new(ImportSourceType.SecureCrt, "SecureCRT XML export (.xml)", ["*.xml"]),
            new(ImportSourceType.ActiveDirectory, "Active Directory (LDAP)", [], SourceIsDirectory: true),
        ];

        public static ImportSourceDescriptor For(ImportSourceType type) => All.First(d => d.Type == type);
    }
}
