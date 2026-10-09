using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Runtime features of a running RDP session: smart sizing on/off, special key combinations and the
/// idle timeout. All of them work on the FreeRDP window embedded in the tab (FreeRDP has no remote-control
/// interface): smart sizing by resizing that window, keys by sending it key events, and the idle timeout by
/// watching the user's input while that window has the focus or the pointer.
/// </summary>
public sealed partial class RdpProtocol : ISpecialKeysProtocol, IDisplayOptionsProtocol
{
    private static readonly IReadOnlyList<SpecialKey> AllSpecialKeys = [SpecialKey.CtrlAltDel, SpecialKey.CtrlEsc];

    private RdpDisplaySettings _display = RdpDisplaySettings.Default;
    private PixelSize? _desktopSize;
    private bool _smartSizingCapable;
    private bool _smartSize;
    private Timer? _idleTimer;
    private RdpIdleTimeout? _idleTimeout;
    private bool _idleAlert;

    private static bool IsTrue(IReadOnlyDictionary<string, string> extras, string key) =>
        extras.TryGetValue(key, out string? value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    // ── Smart sizing (IDisplayOptionsProtocol) ─────────────────────────────

    /// <summary>
    /// True while an embedded session that does not resize the remote desktop is running (fixed resolution,
    /// smart size, or fit-to-window without automatic resize). FreeRDP cannot combine smart sizing with
    /// dynamic resolution, so sessions that follow the tab size cannot be switched to smart sizing.
    /// </summary>
    public bool SupportsSmartSize => _embed is not null && _smartSizingCapable;

    /// <summary>Scale the remote desktop to the tab (true) or show it 1:1 at its own size (false).</summary>
    public bool SmartSize
    {
        get => _smartSize;
        set
        {
            if (!SupportsSmartSize || value == _smartSize)
                return;
            _smartSize = value;
            ApplyRemoteSize();
            RaiseStatus(value ? "Smart sizing on: the remote desktop is scaled to the tab." : "Smart sizing off: the remote desktop is shown at its own size.");
        }
    }

    /// <summary>FreeRDP has no view-only mode for RDP sessions.</summary>
    public bool SupportsViewOnly => false;

    public bool ViewOnly
    {
        get => false;
        set { }
    }

    private void ConfigureDisplay(RdpDisplaySettings display, PixelSize? tabSize)
    {
        _display = display;
        bool embedded = tabSize is not null;
        _smartSizingCapable = embedded && display.UsesSmartSizing(embedded: true);
        _desktopSize = tabSize is { } tab ? display.EmbeddedDesktopSize(tab) : display.FixedSize;
        _smartSize = display.StartsScaled;
    }

    /// <summary>Keeps FreeRDP's window at the desktop size (1:1) or lets it follow the tab (resize or scale).</summary>
    private void ApplyRemoteSize()
    {
        var embed = _embed;
        if (embed is null) return;
        embed.SetFixedRemoteSize(_smartSizingCapable && !_smartSize ? _desktopSize : null);
    }

    // ── Special keys (ISpecialKeysProtocol) ────────────────────────────────

    /// <summary>
    /// Ctrl+Alt+Del and Ctrl+Esc, sent as key events to FreeRDP's window, which forwards them to the server.
    /// Only for sessions shown in a tab (Linux/X11 and Windows); a session in FreeRDP's own window gets them
    /// when the user presses them there.
    /// </summary>
    public IReadOnlyList<SpecialKey> SupportedSpecialKeys =>
        OperatingSystem.IsLinux() || OperatingSystem.IsWindows() ? AllSpecialKeys : [];

    public async Task SendSpecialKeyAsync(SpecialKey key, CancellationToken ct = default)
    {
        ChordKey[] chord = key switch
        {
            SpecialKey.CtrlAltDel => [ChordKey.Control, ChordKey.Alt, ChordKey.Delete],
            SpecialKey.CtrlEsc => [ChordKey.Control, ChordKey.Escape],
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };

        var embed = _embed;
        if (embed is null || State != ConnectionState.Connected)
        {
            RaiseStatus("Key combinations can only be sent to a connected RDP session shown in its tab.");
            return;
        }

        // Windows: focus changes must be made on the thread that owns the focus (the UI thread).
        bool sent = Dispatcher.UIThread.CheckAccess()
            ? embed.SendKeyChord(chord)
            : await Dispatcher.UIThread.InvokeAsync(() => embed.SendKeyChord(chord));
        if (sent)
        {
            _remoteFocusWanted = true;
            _logger.LogDebug("Sent {Key} to the RDP session", key);
        }
        else
        {
            RaiseStatus($"Could not send {Describe(key)} to the RDP session.");
        }
    }

    private static string Describe(SpecialKey key) => key switch
    {
        SpecialKey.CtrlAltDel => "Ctrl+Alt+Del",
        SpecialKey.CtrlEsc => "Ctrl+Esc",
        _ => key.ToString(),
    };

    // ── Idle timeout ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts the idle timeout (legacy MinutesToIdleTimeout) once connected. FreeRDP has no such option, so
    /// the user's input is watched here: the desktop's input idle time (XScreenSaver / GetLastInputInfo)
    /// counts as session activity while the session's window has the keyboard focus or the pointer.
    /// </summary>
    private void StartIdleTimeout()
    {
        StopIdleTimeout();
        var extras = _parameters?.Extras;
        if (extras is null
            || !int.TryParse(extras.GetValueOrDefault(ConnectionParametersFactory.Keys.RdpIdleTimeoutMinutes), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || minutes <= 0)
            return;

        var embed = _embed;
        if (embed is null)
        {
            _logger.LogInformation("The idle timeout is only enforced for RDP sessions shown in a tab; FreeRDP's own window has none.");
            return;
        }
        if (embed.UserIdleTime is null)
        {
            _logger.LogWarning("The user's idle time cannot be read on this system (XScreenSaver extension missing?); the RDP idle timeout is not enforced.");
            return;
        }

        _idleAlert = IsTrue(extras, ConnectionParametersFactory.Keys.RdpIdleTimeoutAlert);
        var timeout = new RdpIdleTimeout(TimeSpan.FromMinutes(minutes), DateTime.UtcNow);
        lock (_sync)
        {
            _idleTimeout = timeout;
            _idleTimer = new Timer(_ => CheckIdle(timeout), null, RdpIdleTimeout.CheckInterval, RdpIdleTimeout.CheckInterval);
        }
    }

    private void StopIdleTimeout()
    {
        Timer? timer;
        lock (_sync)
        {
            timer = _idleTimer;
            _idleTimer = null;
            _idleTimeout = null;
        }
        timer?.Dispose();
    }

    private void CheckIdle(RdpIdleTimeout timeout)
    {
        var embed = _embed;
        if (embed is null || State != ConnectionState.Connected || !ReferenceEquals(timeout, _idleTimeout))
            return;

        bool sessionHasInput = embed.RemoteHasFocus || embed.PointerOverRemote;
        if (!timeout.Observe(DateTime.UtcNow, embed.UserIdleTime, sessionHasInput))
            return;

        StopIdleTimeout();
        _ = DisconnectForIdleAsync(timeout.Limit);
    }

    private async Task DisconnectForIdleAsync(TimeSpan limit)
    {
        int minutes = (int)limit.TotalMinutes;
        string message = string.Create(CultureInfo.InvariantCulture,
            $"The session to {_parameters?.Hostname} was disconnected after {minutes} minute{(minutes == 1 ? "" : "s")} without input.");
        _logger.LogInformation("{Message}", message);
        await DisconnectAsync(CancellationToken.None);
        RaiseStatus(message);
        OnUi(v => v.ShowMessage(_idleAlert ? message : "Disconnected (idle timeout)."));
    }
}

/// <summary>Decides when a session has been without user input for its idle timeout.</summary>
internal sealed class RdpIdleTimeout(TimeSpan limit, DateTime start)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    private DateTime _lastInput = start;

    public TimeSpan Limit { get; } = limit;

    /// <summary>
    /// Records one observation and returns true once the limit is reached. <paramref name="userIdle"/> is the
    /// time since the last input anywhere on the desktop (null when unknown, which never times out); it counts
    /// for the session only while <paramref name="sessionHasInput"/> (focus or pointer in the session).
    /// </summary>
    public bool Observe(DateTime now, TimeSpan? userIdle, bool sessionHasInput)
    {
        if (userIdle is null)
        {
            _lastInput = now;
            return false;
        }
        if (sessionHasInput)
        {
            var lastInput = now - userIdle.Value;
            if (lastInput > _lastInput)
                _lastInput = lastInput;
        }
        return now - _lastInput >= Limit;
    }
}
