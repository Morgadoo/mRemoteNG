namespace mRemoteNG.Core.App;

/// <summary>
/// Command-line switches understood at startup. Compatible with the legacy app
/// (<c>StartupArgumentsInterpreter</c>): a switch starts with <c>/</c>, <c>-</c> or <c>--</c>, and a value
/// follows after <c>:</c>, <c>=</c> or a space, optionally quoted:
/// <list type="bullet">
///   <item><c>/cons:&lt;file&gt;</c>, <c>/c &lt;file&gt;</c> or a bare file name: connection file to open.</item>
///   <item><c>/noreconnect</c>, <c>/norc</c>: do not reconnect the sessions that were open at last exit.</item>
///   <item><c>/resetpos</c>, <c>/rp</c>: reset the main window position and size.</item>
///   <item><c>/resetpanels</c>, <c>/rpnl</c>: reset the docked panel layout.</item>
///   <item><c>/resettoolbar</c>, <c>/rtbr</c>: show the toolbar and status bar again.</item>
///   <item><c>/reset</c>: all three of the above.</item>
///   <item><c>/resetsettings</c>: restore the default options (the old settings file is kept as a backup).</item>
///   <item><c>--portable</c>: keep settings, credentials and connections next to the executable.</item>
///   <item><c>/minimized</c>, <c>/min</c>: start minimised (to the tray when minimise-to-tray is on).</item>
/// </list>
/// A token starting with <c>/</c> that is not a known switch is a (Unix) file path, not a switch.
/// </summary>
public sealed record StartupArguments
{
    private static readonly string[] ValueSwitches = ["cons", "c"];

    private static readonly string[] FlagSwitches =
    [
        "noreconnect", "norc", "resetpos", "rp", "resetpanels", "rpnl", "resettoolbar", "rtbr",
        "reset", "resetsettings", "portable", "minimized", "minimised", "min",
    ];

    public static StartupArguments Empty { get; } = new();

    /// <summary>Connection file named by <c>/cons</c> or as a bare argument (as given, not resolved).</summary>
    public string? ConnectionFile { get; init; }

    public bool NoReconnect { get; init; }

    public bool ResetWindowPosition { get; init; }

    public bool ResetPanels { get; init; }

    public bool ResetToolbar { get; init; }

    public bool ResetSettings { get; init; }

    public bool Portable { get; init; }

    public bool StartMinimized { get; init; }

    /// <summary>Switches that were not recognised (without their prefix).</summary>
    public IReadOnlyList<string> UnknownSwitches { get; init; } = [];

    public static StartupArguments Parse(IEnumerable<string>? args)
    {
        if (args is null)
            return Empty;

        string? connectionFile = null;
        bool noReconnect = false, resetPos = false, resetPanels = false, resetToolbar = false;
        bool resetSettings = false, portable = false, minimized = false;
        var unknown = new List<string>();
        string? pendingValueSwitch = null;
        var endOfOptions = false;

        foreach (var raw in args)
        {
            if (raw is null)
                continue;

            if (pendingValueSwitch is not null)
            {
                connectionFile = Unquote(raw);
                pendingValueSwitch = null;
                continue;
            }

            if (!endOfOptions && raw == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (endOfOptions || !TrySplitSwitch(raw, out var name, out var value))
            {
                connectionFile ??= Unquote(raw);
                continue;
            }

            switch (name)
            {
                case "cons" or "c":
                    if (value is null)
                        pendingValueSwitch = name;
                    else
                        connectionFile = Unquote(value);
                    break;
                case "noreconnect" or "norc":
                    noReconnect = IsTrue(value);
                    break;
                case "resetpos" or "rp":
                    resetPos = IsTrue(value);
                    break;
                case "resetpanels" or "rpnl":
                    resetPanels = IsTrue(value);
                    break;
                case "resettoolbar" or "rtbr":
                    resetToolbar = IsTrue(value);
                    break;
                case "reset":
                    if (IsTrue(value))
                        resetPos = resetPanels = resetToolbar = true;
                    break;
                case "resetsettings":
                    resetSettings = IsTrue(value);
                    break;
                case "portable":
                    portable = IsTrue(value);
                    break;
                case "minimized" or "minimised" or "min":
                    minimized = IsTrue(value);
                    break;
                default:
                    unknown.Add(name);
                    break;
            }
        }

        return new StartupArguments
        {
            ConnectionFile = string.IsNullOrWhiteSpace(connectionFile) ? null : connectionFile,
            NoReconnect = noReconnect,
            ResetWindowPosition = resetPos,
            ResetPanels = resetPanels,
            ResetToolbar = resetToolbar,
            ResetSettings = resetSettings,
            Portable = portable,
            StartMinimized = minimized,
            UnknownSwitches = unknown,
        };
    }

    /// <summary>
    /// Resolves <see cref="ConnectionFile"/> like the legacy app: the path as given when it exists,
    /// otherwise the same name inside <paramref name="dataDirectory"/> when that exists; otherwise the
    /// full path as given (so the caller can report it as missing). Null when no file was given.
    /// </summary>
    public string? ResolveConnectionFile(string dataDirectory, Func<string, bool>? fileExists = null)
    {
        if (ConnectionFile is null)
            return null;

        fileExists ??= File.Exists;
        var given = Environment.ExpandEnvironmentVariables(ConnectionFile);
        if (fileExists(given))
            return Path.GetFullPath(given);

        if (!Path.IsPathRooted(given) && !string.IsNullOrWhiteSpace(dataDirectory))
        {
            var inDataDirectory = Path.Combine(dataDirectory, given);
            if (fileExists(inDataDirectory))
                return Path.GetFullPath(inDataDirectory);
        }

        return Path.GetFullPath(given);
    }

    /// <summary>
    /// Splits "/name", "-name", "--name", "/name:value", "--name=value". A token is only a switch when
    /// the name is a known switch, or it starts with "-" (so "/home/me/file.xml" stays a path).
    /// </summary>
    private static bool TrySplitSwitch(string token, out string name, out string? value)
    {
        name = string.Empty;
        value = null;

        string body;
        bool dashed;
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            body = token[2..];
            dashed = true;
        }
        else if (token.StartsWith('-') && token.Length > 1)
        {
            body = token[1..];
            dashed = true;
        }
        else if (token.StartsWith('/') && token.Length > 1)
        {
            body = token[1..];
            dashed = false;
        }
        else
        {
            return false;
        }

        var separator = body.IndexOfAny([':', '=']);
        var candidate = (separator >= 0 ? body[..separator] : body).ToLowerInvariant();
        var known = ValueSwitches.Contains(candidate) || FlagSwitches.Contains(candidate);
        if (!known && !dashed)
            return false;
        if (candidate.Length == 0)
            return false;

        name = candidate;
        value = separator >= 0 ? body[(separator + 1)..] : null;
        return true;
    }

    private static bool IsTrue(string? value) =>
        value is null
        || !(value.Equals("false", StringComparison.OrdinalIgnoreCase)
             || value.Equals("no", StringComparison.OrdinalIgnoreCase)
             || value == "0");

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
