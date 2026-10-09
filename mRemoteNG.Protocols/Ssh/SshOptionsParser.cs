using System.Globalization;
using System.Text;
using mRemoteNG.Core.Config.Putty;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>The result of parsing a connection's SSH options (PuTTY command-line arguments).</summary>
public sealed record SshCommandLineOptions
{
    /// <summary><c>-P port</c>.</summary>
    public int? Port { get; init; }

    /// <summary><c>-l user</c>.</summary>
    public string? Username { get; init; }

    /// <summary><c>-pw password</c> or the first line of <c>-pwfile file</c>.</summary>
    public string? Password { get; init; }

    /// <summary><c>-i keyfile</c> (OpenSSH or PuTTY .ppk private key).</summary>
    public string? IdentityFile { get; init; }

    /// <summary><c>-C</c>.</summary>
    public bool Compression { get; init; }

    /// <summary><c>-N</c>: no shell, forwardings only.</summary>
    public bool NoShell { get; init; }

    /// <summary><c>-load session</c>: another PuTTY saved session to apply.</summary>
    public string? LoadSession { get; init; }

    /// <summary><c>-L</c>, <c>-R</c> and <c>-D</c> forwardings, in order.</summary>
    public IReadOnlyList<PortForwardSpec> Forwards { get; init; } = [];

    /// <summary>Unsupported, ignored or malformed options, as messages for the user.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];
}

/// <summary>
/// Parses the legacy "SSH options" connection property: extra PuTTY command-line arguments that the
/// WinForms app appended to the PuTTY command line, e.g. <c>-L 8080:intranet:80 -C -i ~/.ssh/key.ppk</c>.
///
/// Supported: <c>-L</c>, <c>-R</c>, <c>-D</c> (also attached, as in <c>-L8080:host:80</c>), <c>-C</c>,
/// <c>-N</c>, <c>-P</c>/<c>-p</c>, <c>-l</c>, <c>-pw</c>, <c>-pwfile</c>, <c>-i</c>, <c>-load</c>.
/// Accepted without effect: <c>-2</c>, <c>-ssh</c>, <c>-a</c>, <c>-x</c>, <c>-t</c>, <c>-noagent</c>,
/// <c>-batch</c>, <c>-v</c> and similar switches that only restate the defaults.
/// Everything else — notably agent forwarding (<c>-A</c>) and X11 forwarding (<c>-X</c>), which SSH.NET
/// does not implement — produces a notice instead of being silently dropped.
/// </summary>
public static class SshOptionsParser
{
    /// <summary>Switches that state what mRemoteNG does anyway.</summary>
    private static readonly HashSet<string> NoOpSwitches = new(StringComparer.Ordinal)
    {
        "-2", "-ssh", "-a", "-x", "-t", "-noagent", "-batch", "-v", "-no-sanitise-stderr", "-sanitise-stderr",
        "-no-sanitise-stdout", "-sanitise-stdout", "-noshare", "-no-antispoof", "-restrict-acl", "-restrict_acl",
        "-legacy-stdio-prompts", "-no-trivial-auth",
    };

    /// <summary>Unsupported options that take an argument (the argument is skipped).</summary>
    private static readonly Dictionary<string, string> UnsupportedWithArgument = new(StringComparer.Ordinal)
    {
        ["-m"] = "reading the remote command from a file",
        ["-nc"] = "netcat-style forwarding (-nc); use an SSH tunnel connection instead",
        ["-s"] = "subsystems",
        ["-hostkey"] = "pinning host keys on the command line; host keys are checked against known_hosts",
        ["-proxycmd"] = "proxy commands",
        ["-J"] = "jump hosts on the command line; set the connection's SSH tunnel instead",
        ["-proxy-jump"] = "jump hosts on the command line; set the connection's SSH tunnel instead",
        ["-cert"] = "certificates",
        ["-sercfg"] = "serial settings",
        ["-sshlog"] = "SSH packet logs",
        ["-sshrawlog"] = "SSH packet logs",
        ["-o"] = "OpenSSH -o options",
        ["-F"] = "OpenSSH configuration files",
        ["-b"] = "binding to a local address",
        ["-c"] = "choosing ciphers",
        ["-e"] = "escape characters",
    };

    /// <summary>Unsupported switches without an argument.</summary>
    private static readonly Dictionary<string, string> UnsupportedSwitches = new(StringComparer.Ordinal)
    {
        ["-A"] = "Agent forwarding (-A) is not supported by the built-in SSH client and was ignored.",
        ["-X"] = "X11 forwarding (-X) is not supported by the built-in SSH client and was ignored.",
        ["-Y"] = "X11 forwarding (-Y) is not supported by the built-in SSH client and was ignored.",
        ["-agent"] = "Pageant/ssh-agent authentication (-agent) is not supported; keys are read from files.",
        ["-1"] = "SSH protocol version 1 (-1) is not supported; SSH-2 is used.",
        ["-T"] = "Disabling the pseudo-terminal (-T) is not supported; a terminal is always allocated.",
        ["-4"] = "Forcing IPv4 (-4) is not supported and was ignored.",
        ["-6"] = "Forcing IPv6 (-6) is not supported and was ignored.",
        ["-share"] = "Connection sharing (-share) is not supported and was ignored.",
        ["-shareexists"] = "Connection sharing (-shareexists) is not supported and was ignored.",
        ["-telnet"] = "-telnet was ignored: the protocol is set on the connection.",
        ["-rlogin"] = "-rlogin was ignored: the protocol is set on the connection.",
        ["-raw"] = "-raw was ignored: the protocol is set on the connection.",
        ["-serial"] = "-serial was ignored: the protocol is set on the connection.",
    };

    public static SshCommandLineOptions Parse(string? commandLine)
    {
        var tokens = Tokenize(commandLine ?? string.Empty);
        var forwards = new List<PortForwardSpec>();
        var notices = new List<string>();
        var options = new SshCommandLineOptions();

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            string? NextArgument()
            {
                if (i + 1 < tokens.Count)
                    return tokens[++i];
                notices.Add($"SSH option {token} needs an argument and was ignored.");
                return null;
            }

            // Forwardings, separate (-L spec) or attached (-Lspec) as OpenSSH users write them.
            if (token.Length >= 2 && token[0] == '-' && token[1] is 'L' or 'R' or 'D'
                && (token.Length == 2 || char.IsDigit(token[2]) || token[2] == '['))
            {
                var kind = token[1] switch { 'L' => PortForwardKind.Local, 'R' => PortForwardKind.Remote, _ => PortForwardKind.Dynamic };
                var argument = token.Length > 2 ? token[2..] : NextArgument();
                if (argument is null)
                    continue;
                try
                {
                    forwards.Add(PortForwardSpec.ParseCommandLine(kind, argument));
                }
                catch (FormatException ex)
                {
                    notices.Add(ex.Message + " The forwarding was ignored.");
                }
                continue;
            }

            switch (token)
            {
                case "-C":
                    options = options with { Compression = true };
                    break;
                case "-N":
                    options = options with { NoShell = true };
                    break;
                case "-P" or "-p":
                    if (NextArgument() is { } portText)
                    {
                        if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
                            options = options with { Port = port };
                        else
                            notices.Add($"Invalid port \"{portText}\" in SSH options was ignored.");
                    }
                    break;
                case "-l":
                    if (NextArgument() is { Length: > 0 } user)
                        options = options with { Username = user };
                    break;
                case "-pw":
                    if (NextArgument() is { } password)
                        options = options with { Password = password };
                    break;
                case "-pwfile":
                    if (NextArgument() is { } passwordFile)
                    {
                        var filePassword = ReadPasswordFile(passwordFile);
                        if (filePassword is null)
                            notices.Add($"Password file \"{passwordFile}\" could not be read.");
                        else
                            options = options with { Password = filePassword };
                    }
                    break;
                case "-i":
                    if (NextArgument() is { Length: > 0 } keyFile)
                        options = options with { IdentityFile = ExpandHome(keyFile) };
                    break;
                case "-load":
                    if (NextArgument() is { Length: > 0 } session)
                        options = options with { LoadSession = session };
                    break;
                default:
                    if (NoOpSwitches.Contains(token))
                        break;
                    if (UnsupportedSwitches.TryGetValue(token, out var notice))
                    {
                        notices.Add(notice);
                        break;
                    }
                    if (UnsupportedWithArgument.TryGetValue(token, out var feature))
                    {
                        var argument = NextArgument();
                        notices.Add($"SSH option {token}{(argument is null ? "" : " " + argument)} was ignored: {feature} is not supported.");
                        break;
                    }
                    notices.Add($"Unknown SSH option \"{token}\" was ignored.");
                    break;
            }
        }

        return options with { Forwards = forwards, Notices = notices };
    }

    /// <summary>
    /// Splits a command line into arguments: whitespace separates them, double or single quotes group,
    /// and a backslash escapes a following quote.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        for (int i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\'')
            {
                current.Append(commandLine[++i]);
                inToken = true;
            }
            else if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        if (inToken)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static string? ReadPasswordFile(string path)
    {
        try
        {
            using var reader = new StreamReader(ExpandHome(path));
            return reader.ReadLine() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : string.Empty)
            : path;
}
