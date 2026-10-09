namespace mRemoteNG.Core.Tools;

/// <summary>
/// The tools offered when no <c>extApps.xml</c> exists yet: ping, traceroute, SSH key installation and a local
/// terminal, written for the operating system we run on (Linux tools open in the first terminal emulator found).
/// Connection values are passed to shells as positional parameters, never pasted into shell code.
/// </summary>
public static class ExternalToolDefaults
{
    private const string PauseScript = "printf \"\\nPress Enter to close. \"; read -r _";

    /// <summary>Terminal emulators tried in order, with the option that introduces the command to run.</summary>
    private static readonly (string Program, string ExecuteOption)[] LinuxTerminals =
    [
        ("x-terminal-emulator", "-e"),
        ("xterm", "-e"),
        ("konsole", "-e"),
        ("gnome-terminal", "--"),
        ("xfce4-terminal", "-x"),
    ];

    public static List<ExternalTool> Create() => Create(ExternalTool.CurrentPlatform, IsOnPath);

    /// <param name="platform">Operating system to create the tools for.</param>
    /// <param name="isInstalled">Tells whether a program name can be found on the PATH (Linux terminal detection).</param>
    public static List<ExternalTool> Create(ExternalToolPlatform platform, Func<string, bool> isInstalled)
    {
        ArgumentNullException.ThrowIfNull(isInstalled);
        return platform switch
        {
            ExternalToolPlatform.Windows => CreateWindows(),
            ExternalToolPlatform.MacOS => CreateMac(),
            ExternalToolPlatform.Linux => CreateLinux(isInstalled),
            _ => [],
        };
    }

    private static List<ExternalTool> CreateWindows() =>
    [
        new("Ping", "cmd", "/K ping -t %HOSTNAME%"),
        new("Traceroute", "cmd", "/K tracert %HOSTNAME%"),
        new("Command Prompt", "cmd", ""),
        new("PowerShell", "powershell", "-NoExit"),
    ];

    private static List<ExternalTool> CreateMac()
    {
        // osascript passes the trailing arguments to "on run argv"; "quoted form of" makes them shell-safe.
        static ExternalTool InTerminal(string name, string shellCommand, string arguments) =>
            new(name, "osascript",
                $"-e 'on run argv' -e 'tell application \"Terminal\" to do script {shellCommand}' -e 'end run' {arguments}");

        return
        [
            InTerminal("Ping", "\"ping \" & quoted form of item 1 of argv", "\"%HOSTNAME%\""),
            InTerminal("Traceroute", "\"traceroute \" & quoted form of item 1 of argv", "\"%HOSTNAME%\""),
            InTerminal("SSH (Terminal)", "\"ssh -p \" & quoted form of item 2 of argv & \" \" & quoted form of item 1 of argv",
                "\"%HOSTNAME%\" \"%PORT%\""),
            new("Terminal", "open", "-a Terminal"),
        ];
    }

    private static List<ExternalTool> CreateLinux(Func<string, bool> isInstalled)
    {
        var tools = new List<ExternalTool>();
        var terminal = LinuxTerminals.FirstOrDefault(t => isInstalled(t.Program));
        if (terminal.Program is null)
            return tools;

        string Run(string script, string arguments) =>
            $"{terminal.ExecuteOption} sh -c '{script}; {PauseScript}' sh {arguments}";

        tools.Add(new ExternalTool("Ping", terminal.Program, $"{terminal.ExecuteOption} ping \"%HOSTNAME%\""));
        tools.Add(new ExternalTool("Traceroute", terminal.Program,
            Run(isInstalled("traceroute") || !isInstalled("tracepath") ? "traceroute \"$1\"" : "tracepath \"$1\"", "\"%HOSTNAME%\"")));
        tools.Add(new ExternalTool("Copy SSH key (ssh-copy-id)", terminal.Program,
            Run("if [ -n \"$1\" ]; then t=\"$1@$2\"; else t=\"$2\"; fi; p=\"$3\"; [ \"$p\" -gt 0 ] 2>/dev/null || p=22; ssh-copy-id -p \"$p\" \"$t\"",
                "\"%USERNAME%\" \"%HOSTNAME%\" \"%PORT%\"")));

        // xterm is a single process that owns its window, so it can be embedded in a session tab.
        if (isInstalled("xterm"))
            tools.Add(new ExternalTool("Terminal", "xterm", "") { TryIntegrate = true, ShowOnToolbar = false });
        else
            tools.Add(new ExternalTool("Terminal", terminal.Program, "") { ShowOnToolbar = false });
        return tools;
    }

    /// <summary>True when <paramref name="program"/> is an existing file in one of the PATH directories.</summary>
    public static bool IsOnPath(string program)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, program)))
                    return true;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return false;
    }
}
