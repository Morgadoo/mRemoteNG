namespace mRemoteNG.Core.Connection.Protocol
{
    /// <summary>
    /// Which terminal opens an SSH connection (legacy <c>TerminalBackend</c>, connection file 2.9).
    /// The names are the WinForms app's: there <see cref="PuttyNg"/> is the embedded PuTTYNG window and
    /// <see cref="WindowsTerminal"/> the Windows console running the system OpenSSH client. This app shows
    /// <see cref="PuttyNg"/> in its built-in terminal and runs the system <c>ssh</c> client for
    /// <see cref="WindowsTerminal"/>.
    /// </summary>
    public enum TerminalBackend
    {
        PuttyNg = 0,
        WindowsTerminal = 1
    }
}
