using mRemoteNG.Core.Settings;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Options ▸ Tabs &amp; Panels (legacy TabsPanelsPage, plus the session reconnect options).</summary>
public sealed class TabsPanelsSettingsViewModel(AppSettings working) : SettingsPageViewModel(working)
{
    public bool ShowProtocolOnTabs
    {
        get => Working.ShowProtocolOnTabs;
        set => Set(Working.ShowProtocolOnTabs, value, v => Working.ShowProtocolOnTabs = v);
    }

    public bool ShowLogonInfoOnTabs
    {
        get => Working.ShowLogonInfoOnTabs;
        set => Set(Working.ShowLogonInfoOnTabs, value, v => Working.ShowLogonInfoOnTabs = v);
    }

    public bool IdentifyQuickConnectTabs
    {
        get => Working.IdentifyQuickConnectTabs;
        set => Set(Working.IdentifyQuickConnectTabs, value, v => Working.IdentifyQuickConnectTabs = v);
    }

    public bool DoubleClickOnTabClosesIt
    {
        get => Working.DoubleClickOnTabClosesIt;
        set => Set(Working.DoubleClickOnTabClosesIt, value, v => Working.DoubleClickOnTabClosesIt = v);
    }

    public bool AlwaysShowPanelTabs
    {
        get => Working.AlwaysShowPanelTabs;
        set => Set(Working.AlwaysShowPanelTabs, value, v => Working.AlwaysShowPanelTabs = v);
    }

    public bool AlwaysShowPanelSelectionDlg
    {
        get => Working.AlwaysShowPanelSelectionDlg;
        set => Set(Working.AlwaysShowPanelSelectionDlg, value, v => Working.AlwaysShowPanelSelectionDlg = v);
    }

    public bool CreateEmptyPanelOnStartUp
    {
        get => Working.CreateEmptyPanelOnStartUp;
        set => Set(Working.CreateEmptyPanelOnStartUp, value, v => Working.CreateEmptyPanelOnStartUp = v);
    }

    public string StartUpPanelName
    {
        get => Working.StartUpPanelName;
        set => Set(Working.StartUpPanelName, value?.Trim() ?? string.Empty, v => Working.StartUpPanelName = v);
    }

    public bool OpenConnectionsFromLastSession
    {
        get => Working.OpenConnectionsFromLastSession;
        set => Set(Working.OpenConnectionsFromLastSession, value, v => Working.OpenConnectionsFromLastSession = v);
    }

    public bool ReconnectOnDisconnect
    {
        get => Working.ReconnectOnDisconnect;
        set => Set(Working.ReconnectOnDisconnect, value, v => Working.ReconnectOnDisconnect = v);
    }

    public decimal? ReconnectAttempts
    {
        get => Working.ReconnectAttempts;
        set
        {
            if (value is null) return;
            Set(Working.ReconnectAttempts, (int)value.Value, v => Working.ReconnectAttempts = v);
        }
    }

    public int MinReconnectAttempts => AppSettings.MinReconnectAttempts;

    public int MaxReconnectAttempts => AppSettings.MaxReconnectAttempts;
}
