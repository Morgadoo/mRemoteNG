using System.Globalization;
using System.Text;
using Avalonia;
using mRemoteNG.Protocols.Abstractions;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>Options for <see cref="RdpProtocol.BuildFreeRdpArgs"/>.</summary>
internal sealed record FreeRdpLaunchOptions
{
    /// <summary>Installed FreeRDP major version (2 or 3); decides the syntax of a few options.</summary>
    public int MajorVersion { get; init; } = 3;

    /// <summary>Native window (XID / HWND) FreeRDP should create its desktop window in, or null for a top-level window.</summary>
    public nint? ParentWindow { get; init; }

    /// <summary>Size of the tab FreeRDP is embedded in, in device pixels.</summary>
    public PixelSize? Size { get; init; }

    /// <summary>Window title (only meaningful for a top-level window).</summary>
    public string? Title { get; init; }

    /// <summary>FreeRDP runs on Windows (wfreerdp): drive letters instead of the home folder.</summary>
    public bool WindowsClient { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Local ports and drives for port and "local drives" redirection.</summary>
    public RdpLocalDevices LocalDevices { get; init; } = RdpLocalDevices.Empty;

    /// <summary>Expands a leading "~" in custom drive paths.</summary>
    public string? HomeDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Receives settings that had to be skipped (logged by the caller).</summary>
    public Action<string>? Warn { get; init; }
}

public sealed partial class RdpProtocol
{
    /// <summary>
    /// Hyper-V's console service authenticates as this SPN class; FreeRDP's /vmconnect does not set it itself
    /// (the legacy client set AuthenticationServiceClass to the same value).
    /// </summary>
    internal const string HyperVSpnClass = "Microsoft Virtual Console Service";

    /// <summary>Reads the certificate policy from <see cref="CertPolicyKey"/>; unknown values fall back to TOFU.</summary>
    internal static RdpCertificatePolicy GetCertificatePolicy(ConnectionParameters p) =>
        p.Extras.TryGetValue(CertPolicyKey, out string? value) && Enum.TryParse(value, ignoreCase: true, out RdpCertificatePolicy policy)
            && Enum.IsDefined(policy)
            ? policy
            : RdpCertificatePolicy.Tofu;

    /// <summary>
    /// Builds the FreeRDP 2.x/3.x argument list. Each element is one argument, so values are
    /// never re-parsed by a shell and cannot inject additional options.
    ///
    /// Settings missing from <see cref="ConnectionParameters.Extras"/> keep FreeRDP's own defaults, so callers
    /// that only set a few keys get a working session.
    /// </summary>
    internal static IReadOnlyList<string> BuildFreeRdpArgs(ConnectionParameters p, FreeRdpLaunchOptions? options = null)
    {
        options ??= new FreeRdpLaunchOptions();
        bool v3 = options.MajorVersion >= 3;
        var args = new List<string> { $"/v:{p.Hostname}:{p.Port}" };
        var extras = p.Extras;
        bool Flag(string key, bool defaultValue) =>
            extras.TryGetValue(key, out string? v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;
        bool? OptionalFlag(string key) =>
            extras.TryGetValue(key, out string? v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : null;
        string? Text(string key) =>
            extras.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        // Credentials
        if (!string.IsNullOrEmpty(p.Username)) args.Add($"/u:{p.Username}");
        if (!string.IsNullOrEmpty(p.Domain)) args.Add($"/d:{p.Domain}");
        // Without a password FreeRDP would ask for credentials on its terminal (there is none, so it gives up with
        // "cancelled"). An explicit empty password makes it connect anyway: servers without NLA (xrdp, Windows with
        // NLA disabled) then show their own login screen in the session; NLA servers report an authentication error.
        args.Add(string.IsNullOrEmpty(p.Password) ? "/p:" : $"/p:{p.Password}");

        // Window and desktop size (see RdpDisplaySettings).
        var display = RdpDisplaySettings.From(p);
        bool embedded = options.ParentWindow is not null && display.CanEmbed;
        if (embedded)
        {
            args.Add(string.Create(CultureInfo.InvariantCulture, $"/parent-window:{(ulong)options.ParentWindow!.Value}"));
            if (options.Size is { } tab)
                args.Add(SizeArg(display.EmbeddedDesktopSize(tab)));
            else if (display.FixedSize is { } fixedSize)
                args.Add(SizeArg(fixedSize));
        }
        else if (display.Mode == RdpResolutionMode.Fullscreen)
        {
            args.Add("/f");
        }
        else if (display.FixedSize is { } fixedSize)
        {
            args.Add(SizeArg(fixedSize));
        }
        if (!embedded && !string.IsNullOrEmpty(options.Title))
            args.Add($"/title:{options.Title}");
        if (display.DynamicResolution)
            args.Add("/dynamic-resolution");
        else if (display.UsesSmartSizing(embedded))
            args.Add("/smart-sizing");

        args.Add("+auto-reconnect");

        // Colour depth. FreeRDP 3 applies its "auto-detect" connection defaults (graphics pipeline, codecs,
        // performance flags) only when none of /network, /gfx, /rfx and /bpp is given; it parses options in a
        // fixed order in which /network:auto would override the performance options below. So 32 bpp (FreeRDP's
        // default) is not passed at all and the other depths go without the graphics pipeline, like mstsc.
        string depth = Text(Keys.RdpColorDepth) ?? "32";
        if (!v3)
        {
            args.Add("/network:auto");
            args.Add($"/bpp:{depth}");
        }
        else if (depth != "32")
        {
            args.Add($"/bpp:{depth}");
        }

        if (Flag(Keys.RdpConsole, false)) args.Add("/admin");
        if (Text(Keys.RdpLoadBalanceInfo) is { } lb) args.Add($"/load-balance-info:{lb}");

        // Experience (performance) options; absent keys keep FreeRDP's defaults.
        AddToggle(args, "wallpaper", OptionalFlag(Keys.RdpWallpaper));
        AddToggle(args, "themes", OptionalFlag(Keys.RdpThemes));
        AddToggle(args, "fonts", OptionalFlag(Keys.RdpFontSmoothing));
        AddToggle(args, "aero", OptionalFlag(Keys.RdpDesktopComposition));
        AddToggle(args, "window-drag", OptionalFlag(Keys.RdpFullWindowDrag));
        AddToggle(args, "menu-anims", OptionalFlag(Keys.RdpMenuAnimations));
        if (Flag(Keys.RdpPersistentBitmapCache, false))
            args.Add(v3 ? "/cache:persist" : "+persist-cache");

        // Keyboard. FreeRDP grabs the keyboard while the pointer is over its window, sending Alt+Tab, the
        // Windows key… to the server (mstsc's "apply Windows key combinations on the remote computer").
        // Without "redirect keys" those stay local, except in full screen (mstsc's default).
        if (OptionalFlag(Keys.RdpRedirectKeys) == false && display.Mode != RdpResolutionMode.Fullscreen)
            args.Add("-grab-keyboard");

        // Redirection — opt-in per connection.
        if (Flag(Keys.RdpClipboard, true)) args.Add("/clipboard");
        AddDriveRedirection(args, extras, options);
        if (Flag(Keys.RdpRedirectPorts, false))
        {
            foreach (var port in options.LocalDevices.SerialPorts)
                args.Add($"/serial:{port.Name},{port.Path}");
            foreach (var port in options.LocalDevices.ParallelPorts)
                args.Add($"/parallel:{port.Name},{port.Path}");
            if (options.LocalDevices.SerialPorts.Count + options.LocalDevices.ParallelPorts.Count == 0)
                options.Warn?.Invoke("Port redirection is enabled but no local serial or parallel port was found.");
        }
        if (Flag(Keys.RdpRedirectPrinters, false)) args.Add("/printer");
        if (Flag(Keys.RdpRedirectSmartCards, false)) args.Add("/smartcard");

        // Audio backends are auto-detected: forcing one (e.g. sys:pulse) makes FreeRDP abort in post-connect
        // when that sound server is not running. The quality mode is negotiated with the server.
        if (Flag(Keys.RdpMicrophone, false)) args.Add("/microphone");
        switch (extras.GetValueOrDefault(Keys.RdpSound, "local"))
        {
            case "local":
                args.Add(Text(Keys.RdpSoundQuality) is { } quality && quality is "dynamic" or "medium" or "high"
                    ? $"/sound:quality:{quality}"
                    : "/sound");
                break;
            case "remote": args.Add("/audio-mode:1"); break;
            default: args.Add("/audio-mode:2"); break;
        }

        // Remote program (alternate shell)
        if (Text(Keys.RdpStartProgram) is { } program)
        {
            args.Add($"/shell:{program}");
            if (Text(Keys.RdpStartProgramWorkDir) is { } workDir)
                args.Add($"/shell-dir:{workDir}");
        }

        AddGateway(args, extras);

        // Hyper-V VM console: the host's VMConnect service on port 2179 (FreeRDP switches 3389 to 2179 itself)
        // with the VM id as preconnection blob, NLA without negotiation and without delegating credentials.
        string? vmId = Text(Keys.RdpVmId);
        if (vmId is not null)
        {
            args.Add(Flag(Keys.RdpVmEnhancedMode, false) ? $"/vmconnect:{vmId};EnhancedMode=1" : $"/vmconnect:{vmId}");
            args.Add($"/spn-class:{HyperVSpnClass}");
            args.Add("/sec:nla");
            if (v3) args.Add("-credentials-delegation");
        }

        // Security. "Use CredSSP" (mstsc semantics) allows NLA rather than forcing it: FreeRDP negotiates
        // NLA > TLS > RDP by default. When disabled, NLA is switched off and TLS/RDP security remain.
        bool restrictedAdmin = Flag(Keys.RdpRestrictedAdmin, false);
        if (vmId is null && !restrictedAdmin && !Flag(Keys.RdpNla, true))
            args.Add(v3 ? "/sec:nla:off" : "-sec-nla");
        if (restrictedAdmin)
        {
            // Needs NLA; the server logs the user on without receiving reusable credentials.
            args.Add("+restricted-admin");
        }
        else if (Flag(Keys.RdpRemoteCredentialGuard, false) && vmId is null && v3)
        {
            // FreeRDP (3.32) cannot do Remote Credential Guard. The part that matters for security is kept:
            // the password is not delegated to the server, which then asks for it on its own logon screen.
            args.Add("-credentials-delegation");
            options.Warn?.Invoke("FreeRDP does not support Remote Credential Guard; credentials are not delegated, so the server will ask for them.");
        }

        // Certificate policy. FreeRDP 2 only understands the older /cert-* spellings reliably.
        string policy = GetCertificatePolicy(p) switch
        {
            RdpCertificatePolicy.Ignore => "ignore",
            RdpCertificatePolicy.Deny => "deny",
            _ => "tofu",
        };
        args.Add(v3 ? $"/cert:{policy}" : $"/cert-{policy}");

        return args;
    }

    private static string SizeArg(PixelSize size) =>
        string.Create(CultureInfo.InvariantCulture, $"/size:{size.Width}x{size.Height}");

    private static void AddToggle(List<string> args, string option, bool? enabled)
    {
        if (enabled is { } on)
            args.Add((on ? "+" : "-") + option);
    }

    private static void AddDriveRedirection(List<string> args, IReadOnlyDictionary<string, string> extras, FreeRdpLaunchOptions options)
    {
        string mode = extras.TryGetValue(Keys.RdpDrives, out string? drives)
            ? drives.Trim().ToLowerInvariant()
            : extras.GetValueOrDefault(Keys.RdpHomeDrive, "false").Equals("true", StringComparison.OrdinalIgnoreCase) ? "local" : "none";

        switch (mode)
        {
            case "local":
                // Legacy: the local fixed disks. On Linux/macOS the counterpart is the user's home folder.
                if (options.WindowsClient)
                {
                    foreach (var drive in options.LocalDevices.FixedDrives)
                        args.Add($"/drive:{drive.Name},{drive.Path}");
                }
                else
                {
                    args.Add("+home-drive");
                }
                break;
            case "all":
                // Every drive letter / mount point.
                args.Add("+drives");
                break;
            case "custom":
                foreach (var (name, path) in ParseCustomDrives(extras.GetValueOrDefault(Keys.RdpDrivesCustom, ""), options))
                    args.Add($"/drive:{name},{path}");
                break;
        }
    }

    /// <summary>
    /// Parses the legacy "custom drives" list ("C,D,X") extended with paths: entries are separated by ',' or ';'
    /// and are a drive letter (Windows), a path, or "Name=path". FreeRDP splits /drive values at commas and
    /// rejects quotes inside them, so such paths are skipped.
    /// </summary>
    internal static IEnumerable<(string Name, string Path)> ParseCustomDrives(string list, FreeRdpLaunchOptions options)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in list.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? name = null;
            string path = raw;
            int eq = raw.IndexOf('=');
            if (eq > 0)
            {
                name = SanitizeShareName(raw[..eq]);
                path = raw[(eq + 1)..].Trim();
            }

            if (IsDriveLetter(path))
            {
                if (!options.WindowsClient)
                {
                    options.Warn?.Invoke($"Custom drive \"{raw}\" is a Windows drive letter; it is not redirected on this system.");
                    continue;
                }
                // "C:/" rather than "C:\": FreeRDP's list parser treats a trailing backslash as an escape.
                string letter = path[..1].ToUpperInvariant();
                name ??= letter;
                path = letter + ":/";
            }
            else
            {
                if (path.StartsWith('~') && options.HomeDirectory is { Length: > 0 } home)
                    path = home + path[1..];
                if (path.AsSpan().IndexOfAny('"', '\'') >= 0)
                {
                    options.Warn?.Invoke($"Custom drive \"{raw}\" contains a quote, which FreeRDP cannot accept; it is not redirected.");
                    continue;
                }
                name ??= SanitizeShareName(Path.GetFileName(path.TrimEnd('/', '\\')));
            }

            if (string.IsNullOrEmpty(name))
                name = "drive";
            string unique = name;
            for (int i = 2; !used.Add(unique); i++)
                unique = string.Create(CultureInfo.InvariantCulture, $"{name}{i}");
            yield return (unique, path);
        }
    }

    private static bool IsDriveLetter(string value) =>
        value.Length is 1 or 2 && char.IsAsciiLetter(value[0]) && (value.Length == 1 || value[1] == ':');

    private static string SanitizeShareName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return sb.ToString();
    }

    private static void AddGateway(List<string> args, IReadOnlyDictionary<string, string> extras)
    {
        if (!extras.TryGetValue(Keys.RdpGateway, out string? host) || string.IsNullOrWhiteSpace(host))
            return;

        // g: must come first: it makes FreeRDP reuse the connection's credentials until u:/d:/p: say otherwise.
        var parts = new List<string> { "g:" + EscapeGatewayValue(host.Trim()) };
        string? user = extras.GetValueOrDefault(Keys.RdpGatewayUsername);
        string credentials = extras.GetValueOrDefault(Keys.RdpGatewayCredentials)
                             ?? (string.IsNullOrEmpty(user) ? "connection" : "explicit");
        switch (credentials)
        {
            case "explicit":
                if (!string.IsNullOrEmpty(user)) parts.Add("u:" + EscapeGatewayValue(user));
                if (extras.GetValueOrDefault(Keys.RdpGatewayDomain) is { Length: > 0 } domain) parts.Add("d:" + EscapeGatewayValue(domain));
                if (extras.GetValueOrDefault(Keys.RdpGatewayPassword) is { Length: > 0 } password) parts.Add("p:" + EscapeGatewayValue(password));
                break;
            case "token":
                if (extras.GetValueOrDefault(Keys.RdpGatewayAccessToken) is { Length: > 0 } token) parts.Add("access-token:" + EscapeGatewayValue(token));
                break;
            // "connection" and "smartcard": FreeRDP authenticates to the gateway like to the server.
        }
        if (extras.GetValueOrDefault(Keys.RdpGatewayUsage) == "detect")
            parts.Add("usage-method:detect");

        args.Add("/gateway:" + string.Join(',', parts));
    }

    /// <summary>
    /// FreeRDP splits /gateway at unquoted commas and then removes one level of backslash escapes from each
    /// value (winpr CommandLineParseCommaSeparatedValues + unescape in cmdline.c).
    /// </summary>
    internal static string EscapeGatewayValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\\' or ',' or '"' or '\'')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The argument list as it may be logged: passwords and tokens replaced.</summary>
    internal static string RedactArgs(IEnumerable<string> args) => string.Join(' ', args.Select(Redact));

    private static string Redact(string arg)
    {
        if (arg.StartsWith("/p:", StringComparison.Ordinal) && arg.Length > 3)
            return "/p:********";
        if (arg.StartsWith("/gateway:", StringComparison.Ordinal))
            return GatewaySecretRegex().Replace(arg, "$1********");
        return arg;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"((?:^|,|/gateway:)(?:p|access-token):)(?:\\.|[^,\\])*")]
    private static partial System.Text.RegularExpressions.Regex GatewaySecretRegex();
}
