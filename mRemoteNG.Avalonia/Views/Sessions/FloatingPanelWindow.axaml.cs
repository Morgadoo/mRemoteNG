using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// A session panel undocked into its own window. Closing the window docks the panel back
/// (its sessions stay open); the session area owns these windows.
/// </summary>
public partial class FloatingPanelWindow : Window
{
    private readonly SessionPanelViewModel? _panel;
    private bool _closingFromArea;

    public FloatingPanelWindow() => InitializeComponent();

    public FloatingPanelWindow(SessionPanelViewModel panel, SessionPanelView view) : this()
    {
        _panel = panel;
        PanelView = view;
        PanelHost.Child = view;
        view.ShowHeader = true;
        UpdateTitle();
        panel.PropertyChanged += OnPanelPropertyChanged;

        if (panel.FloatingBounds is { } bounds)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)bounds.X, (int)bounds.Y);
            Width = Math.Max(MinWidth, bounds.Width);
            Height = Math.Max(MinHeight, bounds.Height);
        }

        PositionChanged += (_, _) => RememberBounds();
        Resized += (_, _) => RememberBounds();
        Activated += (_, _) =>
        {
            if (panel.Owner.Panels.Contains(panel))
                panel.Owner.ActivePanel = panel;
        };
        AddHandler(KeyDownEvent, OnKeyDownTunnel, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    public SessionPanelViewModel? Panel => _panel;

    public SessionPanelView? PanelView { get; private set; }

    /// <summary>Takes the panel view out of this window (so it can be docked) and closes it.</summary>
    public SessionPanelView? DetachAndClose()
    {
        var view = PanelView;
        PanelHost.Child = null;
        PanelView = null;
        LayoutFlush.Run(this);
        if (_panel is not null)
            _panel.PropertyChanged -= OnPanelPropertyChanged;
        _closingFromArea = true;
        Close();
        return view;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingFromArea || _panel is null) return;
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            // The user closed the window: dock the panel back instead of closing its sessions.
            e.Cancel = true;
            Dispatcher.UIThread.Post(() => _panel.IsFloating = false);
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionPanelViewModel.Name) or nameof(SessionPanelViewModel.SessionCount))
            UpdateTitle();
    }

    private void UpdateTitle()
    {
        if (_panel is null) return;
        Title = Localizer.Format("FloatingPanelTitleFormat", _panel.Name, _panel.SessionCount);
    }

    private void RememberBounds()
    {
        if (_panel is null || WindowState != WindowState.Normal) return;
        _panel.FloatingBounds = new FloatingBounds(Position.X, Position.Y, ClientSize.Width, ClientSize.Height);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_panel is not null && SessionKeyboard.Handle(_panel.Owner, e))
            e.Handled = true;
    }
}
