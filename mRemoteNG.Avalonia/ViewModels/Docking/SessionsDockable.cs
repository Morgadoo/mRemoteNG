using System.Collections.ObjectModel;
using Dock.Model.Mvvm.Controls;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>
/// Owns every open session: the flat <see cref="Sessions"/> list and the named <see cref="Panels"/>
/// (tab groups) they are shown in. Implements the session commands (reconnect, duplicate, close
/// others…), auto-reconnect, the PleaseConnect ("reopen at startup") bookkeeping and the panel layout.
/// </summary>
public sealed class SessionsDockable : Document
{
    public const string DefaultPanelName = "General";
    public const string NewPanelBaseName = "New Panel";

    private readonly LogPanelDockable? _log;
    private readonly AppSettingsService? _settings;
    private readonly CloseConfirmationService? _closeConfirmation;
    private readonly ConnectionPreparer? _preparer;
    private readonly Dictionary<SessionTabViewModel, CancellationTokenSource> _reconnectLoops = [];
    private SessionPanelViewModel? _activePanel;
    private PanelArrangement _arrangement = PanelArrangement.Tabbed;

    public SessionsDockable()
    {
        Id = "Sessions";
        Title = "Sessions";
    }

    public SessionsDockable(
        LogPanelDockable log,
        AppSettingsService? settings = null,
        CloseConfirmationService? closeConfirmation = null,
        ConnectionPreparer? preparer = null) : this()
    {
        _log = log;
        _settings = settings;
        _closeConfirmation = closeConfirmation;
        _preparer = preparer;
        if (_settings is not null)
            _settings.Changed += (_, e) =>
            {
                if (e.Changed(s => s.ShowProtocolOnTabs) || e.Changed(s => s.ShowLogonInfoOnTabs)
                    || e.Changed(s => s.IdentifyQuickConnectTabs))
                {
                    foreach (var session in Sessions)
                        session.ApplyTitleSettings(_settings.Current);
                }
            };
    }

    /// <summary>Every open session, in all panels.</summary>
    public ObservableCollection<SessionTabViewModel> Sessions { get; } = [];

    /// <summary>The named session panels, in display order.</summary>
    public ObservableCollection<SessionPanelViewModel> Panels { get; } = [];

    /// <summary>The settings in effect (null in design-time/unit setups without a settings service).</summary>
    public AppSettings? Settings => _settings?.Current;

    /// <summary>The focused panel: Ctrl+Tab, Ctrl+1…9 and ad-hoc sessions use it.</summary>
    public SessionPanelViewModel? ActivePanel
    {
        get => _activePanel;
        set
        {
            if (value is not null && !Panels.Contains(value)) return;
            if (ReferenceEquals(_activePanel, value)) return;
            if (_activePanel is not null) _activePanel.IsActive = false;
            _activePanel = value;
            if (_activePanel is not null) _activePanel.IsActive = true;
            OnPropertyChanged(nameof(ActivePanel));
            OnPropertyChanged(nameof(ActiveSession));
        }
    }

    /// <summary>The selected session of the active panel. Setting it activates the session's panel.</summary>
    public SessionTabViewModel? ActiveSession
    {
        get => _activePanel?.ActiveSession;
        set
        {
            if (value?.Panel is not { } panel || !Sessions.Contains(value)) return;
            panel.ActiveSession = value;
            ActivePanel = panel;
            OnPropertyChanged(nameof(ActiveSession));
        }
    }

    public PanelArrangement Arrangement
    {
        get => _arrangement;
        set
        {
            if (_arrangement == value) return;
            _arrangement = value;
            OnPropertyChanged(nameof(Arrangement));
        }
    }

    /// <summary>First wait before an automatic reconnect; doubles per attempt up to <see cref="AutoReconnectMaxDelay"/>.</summary>
    public TimeSpan AutoReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan AutoReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asks the user for a panel (legacy frmChoosePanel): receives the existing panel names and a suggestion,
    /// returns the chosen name or null to cancel. Set by the main window; without it the suggestion is used.
    /// </summary>
    public Func<IReadOnlyList<string>, string, Task<string?>>? PanelChooser { get; set; }

    public IReadOnlyList<string> PanelNames => Panels.Select(p => p.Name).ToList();

    /// <summary>Writes a connection error to the log panel.</summary>
    public void ReportError(string message) => _log?.Log(message, LogLevel.Error);

    private void Log(string message, LogLevel level = LogLevel.Info) => _log?.Log(message, level);

    // ── Panels ────────────────────────────────────────────────────────────

    public SessionPanelViewModel? FindPanel(string name) =>
        Panels.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the panel with this name, creating it (docked, at the end) when needed.</summary>
    public SessionPanelViewModel GetOrCreatePanel(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? DefaultPanelName : name.Trim();
        if (FindPanel(name) is { } existing)
            return existing;

        var panel = new SessionPanelViewModel(this, name);
        panel.ActiveSessionChanged += OnPanelActiveSessionChanged;
        Panels.Add(panel);
        ActivePanel ??= panel;
        return panel;
    }

    /// <summary>View ▸ New Panel: adds an empty panel ("New Panel", "New Panel 2", …) and activates it.</summary>
    public SessionPanelViewModel NewPanel(string? name = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? UniquePanelName(NewPanelBaseName) : name.Trim();
        var panel = GetOrCreatePanel(name);
        ActivePanel = panel;
        return panel;
    }

    public string UniquePanelName(string baseName)
    {
        if (FindPanel(baseName) is null) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (FindPanel(candidate) is null) return candidate;
        }
    }

    /// <summary>Renames a panel; false when the name is empty or used by another panel.</summary>
    public bool RenamePanel(SessionPanelViewModel panel, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        newName = newName.Trim();
        if (FindPanel(newName) is { } other && !ReferenceEquals(other, panel)) return false;
        panel.Name = newName;
        return true;
    }

    /// <summary>Closes every session of the panel and removes it.</summary>
    public async Task ClosePanelAsync(SessionPanelViewModel panel)
    {
        foreach (var session in panel.Sessions.ToList())
            await CloseSessionAsync(session);
        panel.ActiveSessionChanged -= OnPanelActiveSessionChanged;
        Panels.Remove(panel);
        if (ReferenceEquals(ActivePanel, panel))
            ActivePanel = Panels.FirstOrDefault(p => !p.IsFloating) ?? Panels.FirstOrDefault();
    }

    /// <summary>Moves a session to another panel (created when missing) and selects it there.</summary>
    public void MoveSession(SessionTabViewModel session, string panelName)
    {
        if (session.Panel is not { } from || !Sessions.Contains(session)) return;
        var to = GetOrCreatePanel(panelName);
        if (ReferenceEquals(from, to)) return;

        RemoveFromPanel(session, from);
        session.Panel = to;
        to.Sessions.Add(session);
        to.RaiseSessionCountChanged();
        to.ActiveSession = session;
        ActivePanel = to;
    }

    private void OnPanelActiveSessionChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _activePanel))
            OnPropertyChanged(nameof(ActiveSession));
    }

    // ── Opening sessions ──────────────────────────────────────────────────

    /// <summary>Adds a session to a panel (the active panel, else "General") and selects it.</summary>
    public void AddSession(SessionTabViewModel session, string? panelName = null)
    {
        var panel = GetOrCreatePanel(panelName ?? ActivePanel?.Name ?? DefaultPanelName);
        session.ApplyTitleSettings(Settings);
        session.CloseRequested += OnCloseRequested;
        session.ProtocolStateChanged += OnSessionStateChanged;
        session.Panel = panel;
        Sessions.Add(session);
        panel.Sessions.Add(session);
        panel.RaiseSessionCountChanged();
        panel.ActiveSession = session;
        ActivePanel = panel;
        OnPropertyChanged(nameof(ActiveSession));
    }

    /// <summary>
    /// Creates and adds a new session from connection parameters.
    /// The protocol implementation is resolved from <paramref name="factory"/>.
    /// </summary>
    public Task OpenConnectionAsync(
        ConnectionParameters parameters,
        IProtocolFactory factory,
        CancellationToken ct = default) =>
        OpenAsync(parameters, factory, connection: null, options: null, prepared: null, panelName: null, ct);

    /// <summary>
    /// Opens a session for a connection-tree node: runs the connection preparation steps
    /// (credential/address providers, tunnels, pre-connect apps…) and keeps the node, the options
    /// and the prepared resources with the tab so it can be reconnected or duplicated.
    /// The session opens in <see cref="ConnectOptions.Panel"/>, else the node's Panel property
    /// (asking first when "always show panel selection" is on). Returns null when cancelled or
    /// preparation failed.
    /// </summary>
    public async Task<SessionTabViewModel?> OpenConnectionAsync(
        ConnectionInfo connection,
        IProtocolFactory factory,
        ConnectOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        options ??= ConnectOptions.Default;

        var panelName = await ResolvePanelNameAsync(connection, options);
        if (panelName is null)
        {
            Log($"Opening \"{connection.Name}\" cancelled (no panel chosen).", LogLevel.Warning);
            return null;
        }

        PreparedConnection prepared;
        try
        {
            prepared = await PrepareAsync(connection, options, ct);
        }
        catch (Exception ex)
        {
            Log($"Could not prepare \"{connection.Name}\": {ex.Message}", LogLevel.Error);
            return null;
        }

        return await OpenAsync(prepared.Parameters, factory, connection, options, prepared, panelName, ct);
    }

    private async Task<string?> ResolvePanelNameAsync(ConnectionInfo connection, ConnectOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Panel))
            return options.Panel.Trim();

        var configured = connection.Panel;
        var ask = string.IsNullOrWhiteSpace(configured) || Settings?.AlwaysShowPanelSelectionDlg == true;
        var suggestion = string.IsNullOrWhiteSpace(configured) ? ActivePanel?.Name ?? DefaultPanelName : configured.Trim();
        if (!ask || PanelChooser is null)
            return suggestion;
        return await PanelChooser(PanelNames, suggestion);
    }

    private Task<PreparedConnection> PrepareAsync(ConnectionInfo connection, ConnectOptions options, CancellationToken ct) =>
        _preparer is not null
            ? _preparer.PrepareAsync(connection, options, ct)
            : Task.FromResult(new PreparedConnection(
                ConnectionPreparer.ApplyOptions(ConnectionParametersFactory.FromConnectionInfo(connection), options), []));

    private async Task<SessionTabViewModel> OpenAsync(
        ConnectionParameters parameters,
        IProtocolFactory factory,
        ConnectionInfo? connection,
        ConnectOptions? options,
        PreparedConnection? prepared,
        string? panelName,
        CancellationToken ct)
    {
        var source = parameters;
        // Global defaults (port, username, SSH key, timeout, keep-alive) for anything left unset.
        if (_settings is not null)
            parameters = _settings.Current.WithDefaults(parameters);

        IProtocol protocol;
        try
        {
            protocol = factory.Create(parameters.Protocol);
        }
        catch
        {
            if (prepared is not null)
                await prepared.DisposeAsync();
            throw;
        }

        var tab = new SessionTabViewModel(protocol, parameters)
        {
            SourceParameters = source,
            Connection = connection,
            Options = options ?? ConnectOptions.Default,
            Prepared = prepared,
            Factory = factory,
        };
        AddSession(tab, panelName);

        // Legacy writes Connected="true" for nodes with open sessions; "reconnect at startup" reads it back.
        if (connection is { IsQuickConnect: false })
            connection.PleaseConnect = true;

        try
        {
            await tab.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            tab.SetError(ex.Message);
            Log($"Connection to {parameters.Hostname}:{parameters.Port} failed: {ex.Message}", LogLevel.Error);
        }
        return tab;
    }

    /// <summary>
    /// Opens every connection below <paramref name="root"/> marked PleaseConnect (open when the file
    /// was saved) that is not open already. Returns the number of sessions started.
    /// </summary>
    public int OpenPreviousSessions(ConnectionInfo root, IProtocolFactory factory)
    {
        var candidates = PreviousSessions(root)
            .Where(c => !Sessions.Any(s => ReferenceEquals(s.Connection, c)))
            .ToList();
        foreach (var connection in candidates)
        {
            _ = OpenPreviousAsync(connection);
        }
        return candidates.Count;

        async Task OpenPreviousAsync(ConnectionInfo connection)
        {
            try
            {
                await OpenConnectionAsync(connection, factory);
            }
            catch (Exception ex)
            {
                ReportError($"Could not reopen \"{connection.Name}\": {ex.Message}");
            }
        }
    }

    /// <summary>The connections below <paramref name="root"/> flagged to reopen (legacy PreviousSessionOpener).</summary>
    public static IEnumerable<ConnectionInfo> PreviousSessions(ConnectionInfo root) =>
        root is Core.Container.ContainerInfo container
            ? container.GetRecursiveChildList().Where(c => c is not Core.Container.ContainerInfo && c.PleaseConnect)
            : [];

    // ── Session commands ──────────────────────────────────────────────────

    /// <summary>Disconnects, removes and disposes a session tab.</summary>
    public async Task CloseSessionAsync(SessionTabViewModel session)
    {
        session.CloseRequested -= OnCloseRequested;
        session.ProtocolStateChanged -= OnSessionStateChanged;
        CancelAutoReconnect(session);
        if (!Sessions.Remove(session)) return;
        if (session.Panel is { } panel)
        {
            RemoveFromPanel(session, panel);
            if (ReferenceEquals(ActivePanel, panel))
                OnPropertyChanged(nameof(ActiveSession));
        }
        session.Panel = null;

        if (session.Connection is { } connection && !Sessions.Any(s => ReferenceEquals(s.Connection, connection)))
            connection.PleaseConnect = false;

        session.SuppressAutoReconnect = true;
        await session.DisconnectAsync();
        session.Dispose();
        if (session.Prepared is { } prepared)
            await prepared.DisposeAsync();
    }

    private void RemoveFromPanel(SessionTabViewModel session, SessionPanelViewModel panel)
    {
        var index = panel.Sessions.IndexOf(session);
        if (index < 0) return;
        var wasActive = ReferenceEquals(panel.ActiveSession, session);
        panel.Sessions.RemoveAt(index);
        panel.RaiseSessionCountChanged();
        if (wasActive)
            panel.ActiveSession = panel.Sessions.Count == 0 ? null : panel.Sessions[Math.Min(index, panel.Sessions.Count - 1)];
    }

    /// <summary>Tab menu ▸ Close other tabs (in the same panel).</summary>
    public async Task CloseOtherSessionsAsync(SessionTabViewModel keep)
    {
        if (keep.Panel is not { } panel) return;
        foreach (var session in panel.Sessions.Where(s => !ReferenceEquals(s, keep)).ToList())
            await CloseSessionAsync(session);
        panel.ActiveSession = keep;
    }

    /// <summary>Tab menu ▸ Close tabs to the right (in the same panel).</summary>
    public async Task CloseSessionsToTheRightAsync(SessionTabViewModel anchor)
    {
        if (anchor.Panel is not { } panel) return;
        var index = panel.Sessions.IndexOf(anchor);
        if (index < 0) return;
        foreach (var session in panel.Sessions.Skip(index + 1).ToList())
            await CloseSessionAsync(session);
    }

    public async Task CloseAllSessionsAsync()
    {
        foreach (var session in Sessions.ToList())
            await CloseSessionAsync(session);
    }

    /// <summary>Opens another session with the same connection, options and panel.</summary>
    public async Task<SessionTabViewModel?> DuplicateSessionAsync(SessionTabViewModel session, CancellationToken ct = default)
    {
        if (session.Factory is not { } factory)
            return null;
        var panelName = session.Panel?.Name;
        if (session.Connection is { } connection)
            return await OpenConnectionAsync(connection, factory, session.Options with { Panel = panelName }, ct);
        return await OpenAsync(session.SourceParameters, factory, null, session.Options, null, panelName, ct);
    }

    /// <summary>Whether <see cref="ReconnectSessionAsync"/> can recreate the session's protocol.</summary>
    public static bool CanReconnect(SessionTabViewModel session) => session.Factory is not null;

    /// <summary>Tab menu ▸ Reconnect: closes the protocol and connects a fresh instance in the same tab.</summary>
    public Task<bool> ReconnectSessionAsync(SessionTabViewModel session, CancellationToken ct = default)
    {
        CancelAutoReconnect(session);
        session.ReconnectStatus = null;
        return RestartAsync(session, ct);
    }

    /// <summary>Sessions ▸ Reconnect all.</summary>
    public async Task ReconnectAllAsync()
    {
        var results = await Task.WhenAll(Sessions.Where(CanReconnect).ToList().Select(s => ReconnectSessionAsync(s)));
        Log($"Reconnected {results.Count(r => r)} of {results.Length} session(s).");
    }

    /// <summary>Sessions ▸ Disconnect all: disconnects every session but keeps the tabs (Reconnect brings them back).</summary>
    public async Task DisconnectAllAsync()
    {
        foreach (var session in Sessions.ToList())
        {
            CancelAutoReconnect(session);
            session.ReconnectStatus = null;
            session.SuppressAutoReconnect = true;
            await session.DisconnectAsync();
        }
        Log("Disconnected all sessions.");
    }

    /// <summary>
    /// Disconnects the current protocol, re-runs connection preparation and connects a new protocol
    /// instance in the same tab. Returns true when the new instance reached Connected.
    /// </summary>
    private async Task<bool> RestartAsync(SessionTabViewModel session, CancellationToken ct)
    {
        if (session.Factory is not { } factory || !Sessions.Contains(session))
            return false;

        session.SuppressAutoReconnect = true;
        await session.DisconnectAsync(ct);
        if (session.Prepared is { } oldPrepared)
        {
            session.Prepared = null;
            await oldPrepared.DisposeAsync();
        }
        ct.ThrowIfCancellationRequested();

        ConnectionParameters parameters;
        PreparedConnection? prepared = null;
        try
        {
            if (session.Connection is { } connection)
            {
                prepared = await PrepareAsync(connection, session.Options, ct);
                parameters = prepared.Parameters;
            }
            else
            {
                parameters = session.SourceParameters;
            }
            if (_settings is not null)
                parameters = _settings.Current.WithDefaults(parameters);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.SetError(ex.Message);
            Log($"Could not prepare \"{session.DisplayTitle}\": {ex.Message}", LogLevel.Error);
            return false;
        }

        if (!Sessions.Contains(session))
        {
            if (prepared is not null) await prepared.DisposeAsync();
            return false;
        }

        IProtocol protocol;
        try
        {
            protocol = factory.Create(parameters.Protocol);
        }
        catch (Exception ex)
        {
            if (prepared is not null) await prepared.DisposeAsync();
            session.SetError(ex.Message);
            return false;
        }

        var old = session.ReplaceProtocol(protocol, parameters);
        session.Prepared = prepared;
        old.Dispose();
        session.SuppressAutoReconnect = false;

        try
        {
            await session.ConnectAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            session.SetError(ex.Message);
            Log($"Reconnecting {parameters.Hostname}:{parameters.Port} failed: {ex.Message}", LogLevel.Warning);
            return false;
        }

        return await WaitForSettledStateAsync(session, ct) == ConnectionState.Connected;
    }

    /// <summary>Waits (bounded by the connect timeout) while the protocol is still connecting.</summary>
    private async Task<ConnectionState> WaitForSettledStateAsync(SessionTabViewModel session, CancellationToken ct)
    {
        if (session.Protocol.State is not (ConnectionState.Connecting or ConnectionState.Reconnecting))
            return session.Protocol.State;

        var settled = new TaskCompletionSource<ConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, ConnectionState state)
        {
            if (state is not (ConnectionState.Connecting or ConnectionState.Reconnecting))
                settled.TrySetResult(state);
        }

        session.ProtocolStateChanged += Handler;
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(1, Settings?.ConnectTimeoutSeconds ?? 30) + 5);
            var finished = await Task.WhenAny(settled.Task, Task.Delay(timeout, ct));
            ct.ThrowIfCancellationRequested();
            return finished == settled.Task ? settled.Task.Result : session.Protocol.State;
        }
        finally
        {
            session.ProtocolStateChanged -= Handler;
        }
    }

    // ── Auto-reconnect ────────────────────────────────────────────────────

    private void OnSessionStateChanged(object? sender, ConnectionState state)
    {
        if (sender is not SessionTabViewModel session) return;
        if (state is not (ConnectionState.Error or ConnectionState.Disconnected)) return;
        if (!session.HasBeenConnected || session.SuppressAutoReconnect || !Sessions.Contains(session)) return;
        if (Settings?.ReconnectOnDisconnect != true || session.Factory is null) return;
        if (_reconnectLoops.ContainsKey(session)) return;

        _ = AutoReconnectAsync(session);
    }

    /// <summary>Retries with exponential back-off (1 s, 2 s, 4 s … capped) up to the configured attempts.</summary>
    private async Task AutoReconnectAsync(SessionTabViewModel session)
    {
        var cts = new CancellationTokenSource();
        _reconnectLoops[session] = cts;
        var attempts = Math.Clamp(Settings?.ReconnectAttempts ?? AppSettings.DefaultReconnectAttempts,
            AppSettings.MinReconnectAttempts, AppSettings.MaxReconnectAttempts);
        Log($"\"{session.DisplayTitle}\" disconnected unexpectedly; reconnecting automatically.", LogLevel.Warning);

        try
        {
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var delay = AutoReconnectDelay(attempt);
                session.ReconnectStatus = Localizer.Format("ReconnectingInFormat", Math.Ceiling(delay.TotalSeconds), attempt, attempts);
                await Task.Delay(delay, cts.Token);

                session.ReconnectStatus = Localizer.Format("ReconnectingAttemptFormat", attempt, attempts);
                if (await RestartAsync(session, cts.Token))
                {
                    session.ReconnectStatus = null;
                    Log($"\"{session.DisplayTitle}\" reconnected (attempt {attempt}).");
                    return;
                }
            }

            session.ReconnectStatus = Localizer.Format(attempts == 1 ? "CouldNotReconnectOneAttempt" : "CouldNotReconnectFormat", attempts);
            Log($"Giving up reconnecting \"{session.DisplayTitle}\" after {attempts} attempts.", LogLevel.Error);
        }
        catch (OperationCanceledException)
        {
            // Closed, reconnected or disconnected by the user meanwhile.
        }
        finally
        {
            if (_reconnectLoops.TryGetValue(session, out var current) && ReferenceEquals(current, cts))
                _reconnectLoops.Remove(session);
            cts.Dispose();
        }
    }

    public TimeSpan AutoReconnectDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(AutoReconnectBaseDelay.Ticks * factor, AutoReconnectMaxDelay.Ticks));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public bool IsAutoReconnecting(SessionTabViewModel session) => _reconnectLoops.ContainsKey(session);

    private void CancelAutoReconnect(SessionTabViewModel session)
    {
        if (_reconnectLoops.Remove(session, out var cts))
            cts.Cancel();
    }

    private async void OnCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not SessionTabViewModel session) return;

        // Only asks when "Confirm closing connections" is set to every connection.
        if (_closeConfirmation is not null && !await _closeConfirmation.ConfirmCloseConnectionAsync(session.DisplayTitle))
            return;
        await CloseSessionAsync(session);
    }

    // ── Navigation ────────────────────────────────────────────────────────

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: next or previous tab in the active panel (wraps around).</summary>
    public void SelectAdjacentSession(int direction)
    {
        var panel = ActivePanel;
        if (panel is null || panel.Sessions.Count == 0) return;
        var index = panel.ActiveSession is { } current ? panel.Sessions.IndexOf(current) : -1;
        var next = ((index + direction) % panel.Sessions.Count + panel.Sessions.Count) % panel.Sessions.Count;
        ActiveSession = panel.Sessions[next];
    }

    /// <summary>Ctrl+1…9: selects the n-th (1-based) tab of the active panel; false when there is none.</summary>
    public bool SelectSessionNumber(int number)
    {
        var panel = ActivePanel;
        if (panel is null || number < 1 || number > panel.Sessions.Count) return false;
        ActiveSession = panel.Sessions[number - 1];
        return true;
    }

    // ── Layout ────────────────────────────────────────────────────────────

    /// <summary>Writes the panel part of the layout into <paramref name="state"/>.</summary>
    public void CaptureLayout(WindowLayoutState state)
    {
        state.Arrangement = Arrangement;
        state.ActivePanel = ActivePanel?.Name;
        state.Panels = Panels.Select(p => new PanelLayoutState
        {
            Name = p.Name,
            IsFloating = p.IsFloating,
            Weight = p.SizeWeight,
            Floating = p.FloatingBounds,
        }).ToList();
    }

    /// <summary>Restores panels (creating the named ones), their order, docking state and weights.</summary>
    public void ApplyLayout(WindowLayoutState state)
    {
        Arrangement = state.Arrangement;
        var order = new List<SessionPanelViewModel>();
        foreach (var saved in state.Panels)
        {
            var panel = GetOrCreatePanel(saved.Name);
            panel.SizeWeight = saved.Weight;
            panel.FloatingBounds = saved.Floating;
            panel.IsFloating = saved.IsFloating;
            order.Add(panel);
        }

        // Saved panels first, in their saved order; panels created meanwhile keep theirs after them.
        var target = order.Concat(Panels.Except(order)).ToList();
        for (var i = 0; i < target.Count; i++)
        {
            var current = Panels.IndexOf(target[i]);
            if (current != i)
                Panels.Move(current, i);
        }

        if (state.ActivePanel is { } active && FindPanel(active) is { } activePanel)
            ActivePanel = activePanel;
    }

    /// <summary>View ▸ Reset Layout: every panel docked, tabbed, equal sizes.</summary>
    public void ResetLayout()
    {
        Arrangement = PanelArrangement.Tabbed;
        foreach (var panel in Panels)
        {
            panel.IsFloating = false;
            panel.SizeWeight = 1;
            panel.FloatingBounds = null;
        }
    }
}
