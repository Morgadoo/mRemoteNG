using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Web;

/// <summary>
/// HTTP/HTTPS connections, opened in the system's default web browser.
///
/// Why not an embedded browser: the only cross-platform Avalonia package (WebView.Avalonia 11.0.0.1)
/// binds on Linux to WebKitGTK 4.0 (libwebkit2gtk-4.0.so.37 via WebkitGtkSharp 3.24.24.95), which current
/// distributions (e.g. Ubuntu 24.04, which ships only libwebkit2gtk-4.1) no longer provide, and it needs a GTK
/// main loop inside the Avalonia process. It could not be shown to render under Xvfb, so instead of a fake
/// browser surface the URL is handed to the desktop (xdg-open / open / ShellExecute). The session tab says
/// where the page went and offers Reopen and Copy URL; the protocol does not pretend to stay connected.
/// </summary>
public sealed class ExternalBrowserProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<ExternalBrowserProtocol> _logger;
    private readonly Func<string, bool> _launch;
    private BrowserLaunchView? _view;

    public ExternalBrowserProtocol(ILogger<ExternalBrowserProtocol> logger)
        : this(logger, LaunchWithShell)
    {
    }

    /// <param name="launch">Opens a URL; returns false or throws when it could not be opened.</param>
    internal ExternalBrowserProtocol(ILogger<ExternalBrowserProtocol> logger, Func<string, bool> launch)
    {
        _logger = logger;
        _launch = launch;
    }

    /// <summary>The URL of the last connect attempt.</summary>
    public string? Url { get; private set; }

    public Control CreateView()
    {
        _view = new BrowserLaunchView(this);
        return _view;
    }

    public override Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        Url = WebUrlBuilder.Build(parameters);
        Open();
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        // The browser window is not ours to close.
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <summary>Hands <see cref="Url"/> to the default browser again.</summary>
    internal void Open()
    {
        if (Url is null) return;

        State = ConnectionState.Connecting;
        RaiseStatus($"Opening {Url} in the default browser…");

        string? error = null;
        try
        {
            if (!_launch(Url))
                error = "no program to open web links was found";
        }
        catch (System.ComponentModel.Win32Exception ex) when (!OperatingSystem.IsWindows())
        {
            error = "no program to open web links was found (is xdg-open installed?)";
            _logger.LogWarning(ex, "Opening {Url} in the browser failed", Url);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Opening {Url} in the browser failed", Url);
        }

        if (error is null)
        {
            // The page now lives in the browser; this session holds no connection.
            State = ConnectionState.Connected;
            RaiseStatus($"Opened {Url} in your browser.");
            State = ConnectionState.Disconnected;
            _view?.ShowOpened(Url);
        }
        else
        {
            State = ConnectionState.Error;
            RaiseStatus($"Could not open {Url}: {error}");
            _view?.ShowFailed(Url, error);
        }
    }

    private static bool LaunchWithShell(string url)
    {
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }
}

/// <summary>Builds the URL an HTTP/HTTPS connection points at.</summary>
public static class WebUrlBuilder
{
    /// <summary>
    /// Builds the URL from hostname, port and protocol. A hostname that already is a URL
    /// (e.g. "https://host/path") is kept, gaining the configured port when it names none.
    /// Default ports (80 for http, 443 for https) are omitted.
    /// </summary>
    public static string Build(ConnectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var scheme = parameters.Protocol == ProtocolType.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var defaultPort = scheme == Uri.UriSchemeHttps ? 443 : 80;
        var host = parameters.Hostname.Trim();

        if (host.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(host, UriKind.Absolute, out var given))
        {
            var builder = new UriBuilder(given);
            // A port written in the URL wins; otherwise use the connection's port unless it is just the default.
            if (given.IsDefaultPort && parameters.Port > 0 && parameters.Port != defaultPort)
                builder.Port = parameters.Port;
            if (builder.Uri.IsDefaultPort)
                builder.Port = -1;
            return builder.Uri.AbsoluteUri;
        }

        var hostPart = host;
        var path = "/";
        var slashIndex = hostPart.IndexOf('/');
        if (slashIndex >= 0)
        {
            path = hostPart[slashIndex..];
            hostPart = hostPart[..slashIndex];
        }
        var port = parameters.Port;
        var colons = hostPart.Count(c => c == ':');
        if (colons > 1 && !hostPart.StartsWith('['))
        {
            hostPart = $"[{hostPart}]"; // bare IPv6 address
        }
        else if (colons == 1 && !hostPart.StartsWith('[')
                 && int.TryParse(hostPart[(hostPart.IndexOf(':') + 1)..], System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out var hostPort))
        {
            // "host:port" typed into the hostname wins over the connection's port.
            port = hostPort;
            hostPart = hostPart[..hostPart.IndexOf(':')];
        }

        var portPart = port > 0 && port != defaultPort ? $":{port}" : "";
        return $"{scheme}://{hostPart}{portPart}{path}";
    }
}

/// <summary>Session tab content: where the page was opened, with Reopen / Copy URL actions.</summary>
internal sealed class BrowserLaunchView : UserControl
{
    private readonly ExternalBrowserProtocol _protocol;
    private readonly TextBlock _heading;
    private readonly TextBlock _url;
    private readonly TextBlock _detail;

    public BrowserLaunchView(ExternalBrowserProtocol protocol)
    {
        _protocol = protocol;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));

        _heading = new TextBlock
        {
            Text = "Opening in your browser…",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _url = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x56, 0x9c, 0xd6)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _detail = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        };

        var reopen = new Button { Content = "Reopen in Browser" };
        reopen.Click += (_, _) => _protocol.Open();
        var copy = new Button { Content = "Copy URL" };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null && _protocol.Url is not null)
            {
                await clipboard.SetTextAsync(_protocol.Url);
                _detail.Text = "URL copied to the clipboard.";
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            Children = { reopen, copy },
        };

        Content = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _heading, _url, _detail, buttons },
        };
    }

    public void ShowOpened(string url) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _heading.Text = "Opened in your browser";
        _url.Text = url;
        _detail.Text = "mRemoteNG cannot embed web pages on this platform, so the page was handed to your default browser.";
    });

    public void ShowFailed(string url, string error) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _heading.Text = "Could not open the browser";
        _url.Text = url;
        _detail.Text = $"{error}. Copy the URL and open it manually.";
    });
}
