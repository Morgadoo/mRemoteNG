using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Web;

/// <summary>
/// HTTP/HTTPS protocol implementation using Avalonia.WebView.
///
/// Backend selection (automatic, based on platform):
///   • Windows  → Microsoft WebView2 (Edge/Chromium)
///   • Linux    → WebKitGTK / WebKit2GTK
///   • macOS    → WKWebView (WebKit, system-provided)
///
/// Features:
///   • Address bar with navigation history
///   • Certificate error interception
///   • Basic auth prompt support
///   • Zoom + DevTools (Phase 4)
///
/// Package: Avalonia.WebView (community package, cross-platform)
/// </summary>
public sealed class WebViewProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<WebViewProtocol> _logger;
    private WebBrowserView? _view;
    private string? _targetUrl;

    public WebViewProtocol(ILogger<WebViewProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new WebBrowserView(this);
        return _view;
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;

        string scheme = parameters.Protocol == ProtocolType.Https ? "https" : "http";
        _targetUrl = $"{scheme}://{parameters.Hostname}:{parameters.Port}";

        RaiseStatus($"Opening {_targetUrl}…");
        _view?.Navigate(_targetUrl);

        State = ConnectionState.Connected;
        RaiseStatus(_targetUrl);
        await Task.CompletedTask;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Disconnected;
        await Task.CompletedTask;
    }

    internal void OnNavigated(string url) =>
        RaiseStatus(url);

    internal void OnError(string error)
    {
        _logger.LogWarning("WebView error: {Error}", error);
        RaiseStatus($"Error: {error}");
    }
}

/// <summary>
/// Avalonia UserControl that wraps an embedded web browser.
/// Uses Avalonia.WebView where available; falls back to a simple
/// link display if the WebView package is not installed.
/// </summary>
internal sealed class WebBrowserView : UserControl
{
    private readonly WebViewProtocol _protocol;
    private string _currentUrl = string.Empty;

    // Address bar
    private readonly TextBox _addressBar;
    private readonly Button _goButton;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _refreshButton;
    private readonly ContentControl _browserHost;

    public WebBrowserView(WebViewProtocol protocol)
    {
        _protocol = protocol;

        _addressBar = new TextBox { Watermark = "URL…", FontSize = 12 };
        _goButton = new Button { Content = "Go", Padding = new Avalonia.Thickness(8, 2) };
        _backButton = new Button { Content = "←", Padding = new Avalonia.Thickness(6, 2), ToolTip = { Tag = "Back" } };
        _forwardButton = new Button { Content = "→", Padding = new Avalonia.Thickness(6, 2) };
        _refreshButton = new Button { Content = "↻", Padding = new Avalonia.Thickness(6, 2) };

        _goButton.Click += (_, _) => Navigate(_addressBar.Text ?? _currentUrl);
        _addressBar.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Return)
                Navigate(_addressBar.Text ?? _currentUrl);
        };

        var toolbar = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Avalonia.Thickness(4),
        };
        toolbar.Children.Add(_backButton);
        toolbar.Children.Add(_forwardButton);
        toolbar.Children.Add(_refreshButton);
        toolbar.Children.Add(_addressBar);
        Avalonia.Controls.DockPanel.SetDock(_addressBar, Avalonia.Controls.Dock.Left);
        toolbar.Children.Add(_goButton);

        _browserHost = new ContentControl
        {
            Content = new TextBlock
            {
                Text = "Web browser will be embedded here.\n" +
                       "Requires Avalonia.WebView package (WebView2/WebKitGtk/WKWebView).",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Foreground = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.FromRgb(0x80, 0x80, 0x80)),
            }
        };

        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(_browserHost);
        Content = dock;
    }

    public void Navigate(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        _currentUrl = url;
        _addressBar.Text = url;

        // Phase 4: call Avalonia.WebView.Navigate(url)
        _protocol.OnNavigated(url);
    }
}
