using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace mRemoteNG.Core.Tools;

/// <summary>Operating systems an external tool is meant for (<see cref="ExternalTool.Platform"/>).</summary>
public enum ExternalToolPlatform
{
    /// <summary>Offered on every operating system (the default, and the value for legacy tools).</summary>
    Any,
    Windows,
    Linux,
    MacOS,
}

/// <summary>
/// A user-defined external tool ("External Tools" in mRemoteNG): a program started for a connection, with
/// <c>%VARIABLE%</c> placeholders in its file name, arguments and working directory
/// (see <see cref="ExternalToolArgumentParser"/>). Stored in <c>extApps.xml</c> (see <see cref="ExternalToolsRepository"/>).
/// Mirrors the legacy <c>mRemoteNG.Tools.ExternalTool</c>, including its WaitForExit/TryIntegrate interplay.
/// </summary>
public sealed class ExternalTool : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private string _fileName = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDir = string.Empty;
    private bool _waitForExit;
    private bool _tryIntegrate;
    private bool _showOnToolbar = true;
    private bool _runElevated;
    private ExternalToolPlatform _platform;
    private string _iconPath = string.Empty;

    public ExternalTool(string displayName = "", string fileName = "", string arguments = "", string workingDir = "", bool runElevated = false)
    {
        _displayName = displayName;
        _fileName = fileName;
        _arguments = arguments;
        _workingDir = workingDir;
        _runElevated = runElevated;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Name shown in menus and on the toolbar; connections refer to tools by this name.</summary>
    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value ?? string.Empty);
    }

    /// <summary>Program to start (may contain variables).</summary>
    public string FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value ?? string.Empty);
    }

    public string Arguments
    {
        get => _arguments;
        set => SetField(ref _arguments, value ?? string.Empty);
    }

    public string WorkingDir
    {
        get => _workingDir;
        set => SetField(ref _workingDir, value ?? string.Empty);
    }

    /// <summary>Wait until the program exits (e.g. before connecting). Cannot be set while <see cref="TryIntegrate"/> is on.</summary>
    public bool WaitForExit
    {
        get => _waitForExit;
        set
        {
            if (TryIntegrate)
                return;
            SetField(ref _waitForExit, value);
        }
    }

    /// <summary>Embed the program's window in a session tab (the IntApp protocol). Turns <see cref="WaitForExit"/> off.</summary>
    public bool TryIntegrate
    {
        get => _tryIntegrate;
        set
        {
            if (value)
                WaitForExit = false;
            SetField(ref _tryIntegrate, value);
        }
    }

    public bool ShowOnToolbar
    {
        get => _showOnToolbar;
        set => SetField(ref _showOnToolbar, value);
    }

    /// <summary>Start with administrator rights (Windows UAC, pkexec on Linux, an authorization prompt on macOS).</summary>
    public bool RunElevated
    {
        get => _runElevated;
        set => SetField(ref _runElevated, value);
    }

    /// <summary>Operating system the tool is meant for; tools for another OS are listed but not offered to run.</summary>
    public ExternalToolPlatform Platform
    {
        get => _platform;
        set => SetField(ref _platform, value);
    }

    /// <summary>Optional image file (PNG, ICO, …) shown in menus and on the toolbar.</summary>
    public string IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value ?? string.Empty);
    }

    /// <summary>True when <see cref="Platform"/> matches the operating system we run on.</summary>
    public bool IsAvailableOnCurrentPlatform => IsAvailableOn(CurrentPlatform);

    public bool IsAvailableOn(ExternalToolPlatform platform) =>
        Platform == ExternalToolPlatform.Any || Platform == platform;

    public static ExternalToolPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? ExternalToolPlatform.Windows
        : OperatingSystem.IsMacOS() ? ExternalToolPlatform.MacOS
        : OperatingSystem.IsLinux() ? ExternalToolPlatform.Linux
        : ExternalToolPlatform.Any;

    public ExternalTool Clone() => new()
    {
        _displayName = _displayName,
        _fileName = _fileName,
        _arguments = _arguments,
        _workingDir = _workingDir,
        _waitForExit = _waitForExit,
        _tryIntegrate = _tryIntegrate,
        _showOnToolbar = _showOnToolbar,
        _runElevated = _runElevated,
        _platform = _platform,
        _iconPath = _iconPath,
    };

    /// <summary>Copies every setting of <paramref name="other"/> into this instance (raising change notifications).</summary>
    public void CopyFrom(ExternalTool other)
    {
        ArgumentNullException.ThrowIfNull(other);
        DisplayName = other.DisplayName;
        FileName = other.FileName;
        Arguments = other.Arguments;
        WorkingDir = other.WorkingDir;
        // Order matters: TryIntegrate=true blocks WaitForExit, so clear it first when copying a non-integrated tool.
        TryIntegrate = false;
        WaitForExit = other.WaitForExit;
        TryIntegrate = other.TryIntegrate;
        ShowOnToolbar = other.ShowOnToolbar;
        RunElevated = other.RunElevated;
        Platform = other.Platform;
        IconPath = other.IconPath;
    }

    public override string ToString() => DisplayName;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
