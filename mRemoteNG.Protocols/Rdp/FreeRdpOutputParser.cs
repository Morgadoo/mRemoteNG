using System.Globalization;
using System.Text.RegularExpressions;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>What a single FreeRDP log line tells us about the session.</summary>
internal enum FreeRdpSignal
{
    None,

    /// <summary>The RDP connection sequence finished (FreeRDP 3: transition to CONNECTION_STATE_ACTIVE).</summary>
    Connected,

    /// <summary>
    /// The local framebuffer was initialised. FreeRDP 2 does not log connection state transitions, so this
    /// is the best marker it offers; it is NOT proof of a connection (post-connect can still fail).
    /// </summary>
    FramebufferReady,

    /// <summary>The connection dropped and FreeRDP's +auto-reconnect is retrying.</summary>
    Reconnecting,
}

/// <summary>
/// Interprets FreeRDP (2.x / 3.x) log output and exit codes.
///
/// How connection state is detected (see <see cref="RdpProtocol"/>):
///   FreeRDP is started with <c>WLOG_FILTER=com.freerdp.core.rdp:DEBUG</c> so that the core logs every
///   connection state transition ("CONNECTION_STATE_X --> CONNECTION_STATE_ACTIVE"). Only the transition into
///   CONNECTION_STATE_ACTIVE marks the session as connected; it happens after TLS/NLA authentication,
///   licensing, capability exchange and finalization, and after the desktop window was created.
///   (The <c>/log-filters</c> command-line switch does not take effect for this logger in FreeRDP 3.32, the
///   environment variable does.) Failures are classified from the process exit code, refined with the last
///   ERRCONNECT_* / ERRINFO_* code and certificate diagnostics seen in the output.
/// </summary>
internal sealed partial class FreeRdpOutputParser
{
    private string? _lastErrorCode;
    private string? _lastErrInfoDescription;
    private bool _certificateChanged;
    private bool _certificateRejected;
    private bool _certificateUnknownDenied;
    private bool _certificateNameMismatch;
    private bool _commandLineRejected;
    private bool _runFailed;
    private bool _credentialPromptFailed;
    private string? _knownHostsPath;

    /// <summary>The most recent ERRCONNECT_* or ERRINFO_* code seen, if any.</summary>
    public string? LastErrorCode => _lastErrorCode;

    /// <summary>Forgets diagnostics from an earlier connection attempt of the same process (after a reconnect).</summary>
    public void ResetDiagnostics()
    {
        _lastErrorCode = null;
        _lastErrInfoDescription = null;
        _certificateChanged = false;
        _certificateRejected = false;
        _certificateUnknownDenied = false;
        _certificateNameMismatch = false;
    }

    /// <summary>Classifies one line of output and records diagnostics for <see cref="DescribeFailure"/>.</summary>
    public FreeRdpSignal Process(string line)
    {
        if (string.IsNullOrEmpty(line))
            return FreeRdpSignal.None;

        // Fast path: the DEBUG filter produces a line per PDU for the whole session; only the state
        // transition matters among them.
        if (line.Contains("[DEBUG]", StringComparison.Ordinal))
        {
            return line.Contains("CONNECTION_STATE_ACTIVE", StringComparison.Ordinal) && ActiveTransitionRegex().IsMatch(line)
                ? FreeRdpSignal.Connected
                : FreeRdpSignal.None;
        }

        var errInfo = ErrInfoRegex().Match(line);
        if (errInfo.Success)
        {
            _lastErrorCode = errInfo.Groups["code"].Value;
            string description = errInfo.Groups["desc"].Value.Trim();
            _lastErrInfoDescription = description.Length > 0 ? description : null;
        }
        else
        {
            var err = ErrConnectRegex().Match(line);
            if (err.Success)
                _lastErrorCode = err.Groups["code"].Value;
        }

        // Certificate diagnostics (FreeRDP 3.32 wording). Note that FreeRDP prints "REMOTE HOST IDENTIFICATION HAS
        // CHANGED" for *unknown* certificates too, so only the messages printed when a pinned certificate differs
        // count as a change.
        if (line.Contains("NEW HOST IDENTIFICATION", StringComparison.Ordinal)
            || line.Contains("Old Certificate details", StringComparison.Ordinal)
            || (line.Contains("Certificate for", StringComparison.Ordinal) && line.Contains("has changed!", StringComparison.Ordinal)))
            _certificateChanged = true;
        if (line.Contains("No certificate stored, automatically denying", StringComparison.Ordinal))
            _certificateUnknownDenied = true;
        if (line.Contains("CERTIFICATE NAME MISMATCH", StringComparison.Ordinal))
            _certificateNameMismatch = true;
        if (line.Contains("certificate not trusted", StringComparison.OrdinalIgnoreCase))
            _certificateRejected = true;
        if (line.Contains("CommandLineParseArguments", StringComparison.Ordinal) || line.Contains("Unexpected keyword", StringComparison.Ordinal))
            _commandLineRejected = true;
        if (line.Contains("STATE_RUN_FAILED", StringComparison.Ordinal))
            _runFailed = true;
        // FreeRDP tried to prompt on its (absent) terminal, e.g. for gateway credentials.
        if (line.Contains("client_cli_read_string", StringComparison.Ordinal))
            _credentialPromptFailed = true;

        var hostKey = KnownHostsPathRegex().Match(line);
        if (hostKey.Success)
            _knownHostsPath = hostKey.Groups["path"].Value;

        if (ActiveTransitionRegex().IsMatch(line))
            return FreeRdpSignal.Connected;
        if (line.Contains("Local framebuffer format", StringComparison.Ordinal))
            return FreeRdpSignal.FramebufferReady;
        if (line.Contains("client_auto_reconnect", StringComparison.Ordinal) && line.Contains("Network disconnect", StringComparison.Ordinal))
            return FreeRdpSignal.Reconnecting;
        return FreeRdpSignal.None;
    }

    /// <summary>
    /// True when an exit (after the session was connected) is a normal end of session rather than a fault:
    /// the user logged off / disconnected, or the server ended the session administratively.
    /// </summary>
    public bool IsNormalSessionEnd(int exitCode) =>
        exitCode is 0 or 1 or 2 or 11
        || _lastErrorCode is "ERRINFO_LOGOFF_BY_USER" or "ERRINFO_RPC_INITIATED_DISCONNECT_BY_USER" or "ERRINFO_RPC_INITIATED_LOGOFF";

    /// <summary>Builds a user-facing explanation of why FreeRDP exited.</summary>
    public string DescribeFailure(int exitCode)
    {
        // FreeRDP prints certificate warnings even on some successful first-use connections, so they only
        // explain a failure that actually happened in the TLS phase.
        bool tlsFailure = _certificateRejected || exitCode == 143 || _lastErrorCode == "ERRCONNECT_TLS_CONNECT_FAILED";
        if (tlsFailure && _certificateChanged)
        {
            string where = _knownHostsPath is null ? string.Empty : $" If the change is expected, delete {_knownHostsPath} and connect again.";
            return "The server's TLS certificate has changed since the last connection and was rejected (possible man-in-the-middle)." + where;
        }

        if (tlsFailure && (_certificateRejected || _certificateUnknownDenied))
        {
            string reason = _certificateUnknownDenied
                ? "The server's TLS certificate is not trusted (not CA-signed for this host name and not pinned from an earlier connection) and the certificate policy rejects unknown certificates."
                : "The server's TLS certificate was not trusted and the connection was aborted.";
            if (_certificateNameMismatch)
                reason += " The certificate was issued for a different host name.";
            return reason + " If this certificate is expected, lower the connection's \"Server authentication\" setting (Authentication level).";
        }

        if (_credentialPromptFailed)
            return "FreeRDP asked for credentials interactively, which is not possible here. Store the user name, " +
                   "password and domain (and the gateway credentials, if a gateway is used) in the connection settings.";

        // FreeRDP aborts its own session ("cancelled") after failing to process server data, e.g. a codec error.
        if (_runFailed && (exitCode == 145 || _lastErrorCode == "ERRCONNECT_CONNECT_CANCELLED"))
            return "FreeRDP aborted the session after failing to process data from the server (FreeRDP's output is in the application log).";

        if (_commandLineRejected)
            return $"FreeRDP rejected its command line (exit code {exitCode}); the installed FreeRDP version may not support one of the options.";

        if (_lastErrorCode is not null)
        {
            string? text = DescribeErrorCode(_lastErrorCode);
            if (text is not null)
                return text;
            if (_lastErrInfoDescription is not null)
                return _lastErrInfoDescription;
        }

        string? byExit = DescribeExitCode(exitCode);
        if (byExit is not null)
            return byExit;

        return _lastErrorCode is not null
            ? $"FreeRDP exited with code {exitCode} ({_lastErrorCode})."
            : $"FreeRDP exited with code {exitCode}.";
    }

    /// <summary>Human-readable text for FreeRDP ERRCONNECT_* codes (shared by FreeRDP 2 and 3).</summary>
    internal static string? DescribeErrorCode(string code) => code switch
    {
        "ERRCONNECT_LOGON_FAILURE" or "ERRCONNECT_AUTHENTICATION_FAILED" or "ERRCONNECT_WRONG_PASSWORD"
            => "Authentication failed: the user name or password is incorrect.",
        "ERRCONNECT_NO_OR_MISSING_CREDENTIALS" => "Authentication failed: no credentials were provided.",
        "ERRCONNECT_ACCOUNT_LOCKED_OUT" => "Authentication failed: the account is locked out.",
        "ERRCONNECT_ACCOUNT_DISABLED" => "Authentication failed: the account is disabled.",
        "ERRCONNECT_ACCOUNT_EXPIRED" => "Authentication failed: the account has expired.",
        "ERRCONNECT_ACCOUNT_RESTRICTION" => "Authentication failed: an account restriction prevents this logon.",
        "ERRCONNECT_PASSWORD_EXPIRED" or "ERRCONNECT_PASSWORD_CERTAINLY_EXPIRED" => "Authentication failed: the password has expired.",
        "ERRCONNECT_PASSWORD_MUST_CHANGE" => "Authentication failed: the password must be changed before logging on.",
        "ERRCONNECT_LOGON_TYPE_NOT_GRANTED" => "Authentication failed: the user is not allowed to log on remotely.",
        "ERRCONNECT_ACCESS_DENIED" or "ERRCONNECT_INSUFFICIENT_PRIVILEGES" => "Access denied by the server.",
        "ERRCONNECT_DNS_NAME_NOT_FOUND" or "ERRCONNECT_DNS_ERROR" => "The host name could not be resolved.",
        "ERRCONNECT_CONNECT_FAILED" or "ERRCONNECT_CONNECT_TRANSPORT_FAILED"
            => "Could not connect to the host (refused, unreachable or timed out).",
        "ERRCONNECT_SECURITY_NEGO_CONNECT_FAILED"
            => "Security negotiation failed: the host is unreachable, or it does not support the requested security " +
               "(for example NLA/CredSSP is required on one side but not supported by the other).",
        "ERRCONNECT_HYBRID_REQUIRED_BY_SERVER" => "The server requires Network Level Authentication (NLA), which is disabled for this connection.",
        "ERRCONNECT_TLS_CONNECT_FAILED" => "The TLS connection failed (the certificate may have been rejected).",
        "ERRCONNECT_MCS_CONNECT_INITIAL_ERROR" => "The server rejected the connection during the MCS connect phase.",
        "ERRCONNECT_KDC_UNREACHABLE" => "Kerberos authentication failed: the KDC is unreachable.",
        "ERRCONNECT_POST_CONNECT_FAILED" => "FreeRDP failed to initialise the session after connecting (post-connect failed).",
        "ERRCONNECT_PRE_CONNECT_FAILED" => "FreeRDP failed before connecting (pre-connect failed).",
        "ERRCONNECT_CONNECT_CANCELLED" => "The connection was cancelled.",
        "ERRCONNECT_SERVER_DENIED_CONNECTION" => "The server denied the connection.",
        _ => null,
    };

    /// <summary>Human-readable text for xfreerdp exit codes (XF_EXIT_* in client/X11/xfreerdp.h).</summary>
    internal static string? DescribeExitCode(int exitCode) => exitCode switch
    {
        0 or 1 => "The RDP session was disconnected.",
        2 => "The remote user logged off.",
        3 => "The session was disconnected because it was idle.",
        4 => "The logon timed out.",
        5 => "The session was taken over by another connection.",
        7 => "The server denied the connection.",
        9 => "The user does not have permission to log on remotely.",
        10 => "The server requires fresh credentials.",
        11 => "The session was disconnected by the user.",
        >= 16 and <= 26 => "RDP licensing error (exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + ").",
        131 or 141 or 147 => DescribeErrorCode("ERRCONNECT_CONNECT_FAILED"),
        132 or 134 or 154 => DescribeErrorCode("ERRCONNECT_LOGON_FAILURE"),
        133 => DescribeErrorCode("ERRCONNECT_SECURITY_NEGO_CONNECT_FAILED"),
        135 => DescribeErrorCode("ERRCONNECT_ACCOUNT_LOCKED_OUT"),
        138 => DescribeErrorCode("ERRCONNECT_POST_CONNECT_FAILED"),
        139 or 140 => DescribeErrorCode("ERRCONNECT_DNS_NAME_NOT_FOUND"),
        143 => DescribeErrorCode("ERRCONNECT_TLS_CONNECT_FAILED"),
        145 => DescribeErrorCode("ERRCONNECT_CONNECT_CANCELLED"),
        148 or 152 => DescribeErrorCode("ERRCONNECT_PASSWORD_EXPIRED"),
        149 => DescribeErrorCode("ERRCONNECT_PASSWORD_MUST_CHANGE"),
        151 => DescribeErrorCode("ERRCONNECT_ACCOUNT_DISABLED"),
        155 => DescribeErrorCode("ERRCONNECT_ACCESS_DENIED"),
        157 => DescribeErrorCode("ERRCONNECT_ACCOUNT_EXPIRED"),
        159 => DescribeErrorCode("ERRCONNECT_NO_OR_MISSING_CREDENTIALS"),
        161 => DescribeErrorCode("ERRCONNECT_HYBRID_REQUIRED_BY_SERVER"),
        _ => null,
    };

    [GeneratedRegex(@"-->\s*CONNECTION_STATE_ACTIVE\b")]
    private static partial Regex ActiveTransitionRegex();

    [GeneratedRegex(@"\b(?<code>ERRCONNECT_[A-Z_]+)\b")]
    private static partial Regex ErrConnectRegex();

    [GeneratedRegex(@"\b(?<code>ERRINFO_[A-Z_]+)\b\s*(\(0x[0-9A-Fa-f]+\))?\s*:?(?<desc>.*)$")]
    private static partial Regex ErrInfoRegex();

    [GeneratedRegex(@"Add correct host key in (?<path>.+?) to get rid of this message")]
    private static partial Regex KnownHostsPathRegex();
}
