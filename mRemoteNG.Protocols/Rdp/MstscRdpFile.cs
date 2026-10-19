using System.Globalization;
using System.Text;
using Avalonia;
using mRemoteNG.Protocols.Abstractions;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Writes a Microsoft Remote Desktop Connection file (<c>.rdp</c>) from the same connection settings
/// FreeRDP consumes. Used on Windows when <c>wfreerdp</c> is not installed: <c>mstsc.exe</c> cannot be
/// parented into the session tab, so it opens its own window.
///
/// Passwords are stored as DPAPI blobs (<c>password 51:b:</c>), readable only by the user who wrote the
/// file. Settings FreeRDP understands but <c>mstsc</c> does not (sound quality, the exact custom-drive
/// list, Hyper-V VM connect) are reported through <see cref="MstscLaunchOptions.Warnings"/>.
/// </summary>
internal static class MstscRdpFile
{
    /// <summary>Builds the .rdp file contents (UTF-16, one "name:type:value" line per setting).</summary>
    public static string Build(ConnectionParameters p, MstscLaunchOptions? options = null)
    {
        options ??= new MstscLaunchOptions();
        var extras = p.Extras;
        var lines = new List<string>();
        var warnings = new List<string>();

        bool Flag(string key, bool defaultValue) =>
            extras.TryGetValue(key, out string? v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : defaultValue;
        string? Text(string key) =>
            extras.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        string address = p.Port is > 0 and not 3389 ? $"{p.Hostname}:{p.Port}" : p.Hostname;
        String(lines, "full address", address);
        Int(lines, "server port", p.Port > 0 ? p.Port : 3389);

        if (!string.IsNullOrEmpty(p.Username)) String(lines, "username", p.Username);
        if (!string.IsNullOrEmpty(p.Domain)) String(lines, "domain", p.Domain);
        bool restrictedAdmin = Flag(Keys.RdpRestrictedAdmin, false);
        bool remoteGuard = Flag(Keys.RdpRemoteCredentialGuard, false);
        // Both modes authenticate as the interactive user and must not receive a reusable password.
        if (!string.IsNullOrEmpty(p.Password) && !restrictedAdmin && !remoteGuard)
        {
            if (options.ProtectPassword?.Invoke(p.Password) is { } blob)
                Blob(lines, "password 51", blob);
            else
                warnings.Add("The password could not be stored in the Remote Desktop file; Windows will ask for it.");
        }

        var display = RdpDisplaySettings.From(p);
        switch (display.Mode)
        {
            case RdpResolutionMode.Fullscreen:
                Int(lines, "screen mode id", 2);
                Int(lines, "smart sizing", 0);
                Int(lines, "dynamic resolution", display.AutoResize ? 1 : 0);
                break;
            case RdpResolutionMode.Fixed when display.FixedSize is { } size:
                Int(lines, "screen mode id", 1);
                Int(lines, "desktopwidth", size.Width);
                Int(lines, "desktopheight", size.Height);
                Int(lines, "smart sizing", 0);
                Int(lines, "dynamic resolution", 0);
                break;
            case RdpResolutionMode.SmartSize:
                // Scaled to the window at a fixed desktop size (the work area stands in for the tab).
                Int(lines, "screen mode id", 1);
                Int(lines, "desktopwidth", options.WorkArea.Width);
                Int(lines, "desktopheight", options.WorkArea.Height);
                Int(lines, "smart sizing", 1);
                Int(lines, "dynamic resolution", 0);
                break;
            default:
                // Fit to window: the desktop follows the Remote Desktop window.
                Int(lines, "screen mode id", 1);
                Int(lines, "desktopwidth", options.WorkArea.Width);
                Int(lines, "desktopheight", options.WorkArea.Height);
                Int(lines, "smart sizing", 0);
                Int(lines, "dynamic resolution", display.AutoResize ? 1 : 0);
                break;
        }

        string depth = Text(Keys.RdpColorDepth) ?? "32";
        if (int.TryParse(depth, NumberStyles.None, CultureInfo.InvariantCulture, out int bpp) && bpp is 15 or 16 or 24 or 32)
            Int(lines, "session bpp", bpp);

        Int(lines, "bitmapcachepersistenable", Flag(Keys.RdpPersistentBitmapCache, false) ? 1 : 0);
        Int(lines, "disable wallpaper", Flag(Keys.RdpWallpaper, true) ? 0 : 1);
        Int(lines, "allow font smoothing", Flag(Keys.RdpFontSmoothing, false) ? 1 : 0);
        Int(lines, "allow desktop composition", Flag(Keys.RdpDesktopComposition, false) ? 1 : 0);
        Int(lines, "disable full window drag", Flag(Keys.RdpFullWindowDrag, true) ? 0 : 1);
        Int(lines, "disable menu anims", Flag(Keys.RdpMenuAnimations, true) ? 0 : 1);
        Int(lines, "disable themes", Flag(Keys.RdpThemes, true) ? 0 : 1);
        Int(lines, "disable cursor setting", Flag(Keys.RdpDisableCursorShadow, false) || Flag(Keys.RdpDisableCursorBlinking, false) ? 1 : 0);

        // 0 = local, 1 = remote while the session is focused, 2 = remote in full screen only (mstsc's default).
        Int(lines, "keyboardhook", Flag(Keys.RdpRedirectKeys, false) ? 1
            : display.Mode == RdpResolutionMode.Fullscreen ? 2 : 0);

        Int(lines, "redirectclipboard", Flag(Keys.RdpClipboard, true) ? 1 : 0);
        Int(lines, "redirectprinters", Flag(Keys.RdpRedirectPrinters, false) ? 1 : 0);
        Int(lines, "redirectcomports", Flag(Keys.RdpRedirectPorts, false) ? 1 : 0);
        Int(lines, "redirectsmartcards", Flag(Keys.RdpRedirectSmartCards, false) ? 1 : 0);
        AddDrives(lines, extras, warnings);
        Int(lines, "audiocapturemode", Flag(Keys.RdpMicrophone, false) ? 1 : 0);
        Int(lines, "audiomode", extras.GetValueOrDefault(Keys.RdpSound, "local") switch
        {
            "local" => 0,
            "remote" => 1,
            _ => 2,
        });
        if (Text(Keys.RdpSoundQuality) is { } quality && quality != "dynamic")
            warnings.Add("Windows Remote Desktop has no sound-quality setting; the default quality is used.");

        if (Flag(Keys.RdpConsole, false)) Int(lines, "administrative session", 1);
        if (Text(Keys.RdpLoadBalanceInfo) is { } lb) String(lines, "loadbalanceinfo", lb);
        if (Flag(Keys.RdpUseRedirectionServerName, false)) Int(lines, "use redirection server name", 1);

        if (Text(Keys.RdpStartProgram) is { } program)
        {
            String(lines, "alternate shell", program);
            if (Text(Keys.RdpStartProgramWorkDir) is { } workDir)
                String(lines, "shell working directory", workDir);
        }

        AddSecurity(lines, extras, warnings);
        AddGateway(lines, extras, options, warnings);

        if (Text(Keys.RdpVmId) is not null)
            warnings.Add("Windows Remote Desktop cannot open a Hyper-V console (VMConnect); the host is connected as a normal RDP session.");

        options.Warnings = warnings;
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static void AddDrives(List<string> lines, IReadOnlyDictionary<string, string> extras, List<string> warnings)
    {
        string mode = extras.TryGetValue(Keys.RdpDrives, out string? drives)
            ? drives.Trim().ToLowerInvariant()
            : extras.GetValueOrDefault(Keys.RdpHomeDrive, "false").Equals("true", StringComparison.OrdinalIgnoreCase) ? "local" : "none";

        switch (mode)
        {
            case "local":
            case "all":
                // mstsc redirects every drive; it has no "fixed disks only" switch.
                String(lines, "drivestoredirect", "*");
                if (mode == "local")
                    warnings.Add("Windows Remote Desktop redirects every local drive, not only the fixed disks.");
                break;
            case "custom":
                // mstsc wants "C:\;D:\;" — a drive letter, a colon, a backslash, separated by semicolons.
                string list = extras.GetValueOrDefault(Keys.RdpDrivesCustom, "");
                var letters = new List<string>();
                bool skipped = false;
                foreach (string raw in list.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string path = raw;
                    int eq = raw.IndexOf('=');
                    if (eq > 0) path = raw[(eq + 1)..].Trim();
                    if (path.Length is 1 or 2 && char.IsAsciiLetter(path[0]) && (path.Length == 1 || path[1] == ':'))
                        letters.Add(char.ToUpperInvariant(path[0]) + @":\");
                    else
                        skipped = true;
                }
                if (letters.Count > 0)
                    String(lines, "drivestoredirect", string.Join(';', letters.Distinct(StringComparer.OrdinalIgnoreCase)) + ";");
                if (skipped)
                    warnings.Add("Windows Remote Desktop only redirects drive letters; folder paths in the custom drive list were skipped.");
                break;
        }
    }

    private static void AddSecurity(List<string> lines, IReadOnlyDictionary<string, string> extras, List<string> warnings)
    {
        bool nla = !extras.TryGetValue(Keys.RdpNla, out string? v) || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        bool restricted = extras.TryGetValue(Keys.RdpRestrictedAdmin, out string? r) && r.Equals("true", StringComparison.OrdinalIgnoreCase);
        bool rcg = extras.TryGetValue(Keys.RdpRemoteCredentialGuard, out string? g) && g.Equals("true", StringComparison.OrdinalIgnoreCase);

        // CredSSP has to stay on for NLA, Restricted Admin and Remote Credential Guard. Off is what mstsc
        // writes when the server is reached with TLS or classic RDP security instead.
        Int(lines, "enablecredsspsupport", nla || restricted || rcg ? 1 : 0);
        // Restricted Admin and Remote Credential Guard are not .rdp settings: mstsc only honours them as
        // /restrictedAdmin and /remoteGuard (see MstscArguments). A password in the file would defeat both,
        // so it is left out and mstsc uses the signed-in user's credentials.
        if (restricted)
            warnings.Add("Restricted Admin uses the signed-in user's credentials; the stored password is not sent.");
        else if (rcg)
            warnings.Add("Remote Credential Guard uses the signed-in user's credentials; the stored password is not sent.");

        // Authentication level is mstsc's own: 0 connect anyway, 1 refuse, 2 warn (the .rdp default).
        // Trust-on-first-use has no equivalent: mstsc can only warn, which is also its default.
        string policy = extras.GetValueOrDefault(RdpProtocol.CertPolicyKey, "tofu");
        Int(lines, "authentication level", policy.Equals("deny", StringComparison.OrdinalIgnoreCase) ? 1
            : policy.Equals("ignore", StringComparison.OrdinalIgnoreCase) ? 0 : 2);
    }

    private static void AddGateway(List<string> lines, IReadOnlyDictionary<string, string> extras, MstscLaunchOptions options, List<string> warnings)
    {
        if (!extras.TryGetValue(Keys.RdpGateway, out string? host) || string.IsNullOrWhiteSpace(host))
            return;

        String(lines, "gatewayhostname", host.Trim());
        Int(lines, "gatewayusagemethod", extras.GetValueOrDefault(Keys.RdpGatewayUsage) == "detect" ? 2 : 1);
        Int(lines, "gatewayprofileusagemethod", 1);

        string? user = extras.GetValueOrDefault(Keys.RdpGatewayUsername);
        string credentials = extras.GetValueOrDefault(Keys.RdpGatewayCredentials)
                             ?? (string.IsNullOrEmpty(user) ? "connection" : "explicit");
        switch (credentials)
        {
            case "explicit":
                Int(lines, "promptcredentialonce", 0);
                Int(lines, "gatewaycredentialssource", 4);
                if (!string.IsNullOrEmpty(user)) String(lines, "gatewayusername", user);
                if (extras.GetValueOrDefault(Keys.RdpGatewayDomain) is { Length: > 0 } domain)
                    String(lines, "gatewaydomain", domain);
                if (extras.GetValueOrDefault(Keys.RdpGatewayPassword) is { Length: > 0 } password)
                {
                    if (options.ProtectPassword?.Invoke(password) is { } blob)
                        Blob(lines, "gatewaypassword 51", blob);
                    else
                        warnings.Add("The gateway password could not be stored; Windows will ask for it.");
                }
                break;
            case "smartcard":
                Int(lines, "gatewaycredentialssource", 1);
                break;
            case "token":
                Int(lines, "gatewaycredentialssource", 5);
                if (extras.GetValueOrDefault(Keys.RdpGatewayAccessToken) is { Length: > 0 } token)
                    String(lines, "gatewayaccesstoken", token);
                break;
            default:
                // Same credentials as the session.
                Int(lines, "promptcredentialonce", 1);
                Int(lines, "gatewaycredentialssource", 0);
                break;
        }
    }

    private static void String(List<string> lines, string name, string value) =>
        lines.Add(name + ":s:" + value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));

    private static void Int(List<string> lines, string name, int value) =>
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name}:i:{value}"));

    private static void Blob(List<string> lines, string name, byte[] protectedBytes) =>
        lines.Add(name + ":b:" + Convert.ToHexString(protectedBytes));

    /// <summary>
    /// Command-line switches mstsc needs on top of the .rdp file. <c>/admin</c> is also a file setting, but
    /// Restricted Admin and Remote Credential Guard exist only as switches.
    /// </summary>
    public static IReadOnlyList<string> Arguments(ConnectionParameters p)
    {
        var args = new List<string>();
        var extras = p.Extras;
        bool Flag(string key) =>
            extras.TryGetValue(key, out string? v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (Flag(Keys.RdpConsole)) args.Add("/admin");
        if (Flag(Keys.RdpRestrictedAdmin)) args.Add("/restrictedAdmin");
        else if (Flag(Keys.RdpRemoteCredentialGuard)) args.Add("/remoteGuard");
        return args;
    }
}

/// <summary>Options for <see cref="MstscRdpFile.Build"/>.</summary>
internal sealed class MstscLaunchOptions
{
    /// <summary>Work area used when the desktop should fill the window (fit / smart size).</summary>
    public PixelSize WorkArea { get; init; } = new(1920, 1080);

    /// <summary>
    /// DPAPI-protects a password for <c>password 51</c>. Returning null (protection unavailable) stores no
    /// password and Windows asks for it instead.
    /// </summary>
    public Func<string, byte[]?>? ProtectPassword { get; init; }

    /// <summary>Settings that were approximated or skipped, filled by <see cref="MstscRdpFile.Build"/>.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
