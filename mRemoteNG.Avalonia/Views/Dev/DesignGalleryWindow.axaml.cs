using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using ProtocolKind = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Avalonia.Views.Dev;

/// <summary>
/// Developer-only showcase of docs/design-system.md (every colour token, the type ramp and each component
/// state), opened with <c>--design-gallery</c>. The theme picker previews the built-in themes without saving.
/// </summary>
public partial class DesignGalleryWindow : Window
{
    public DesignGalleryWindow()
    {
        AvaloniaXamlLoader.Load(this);
        BuildSwatches();
        BuildSamples();
        BuildThemePicker();
    }

    private void BuildThemePicker()
    {
        var picker = this.FindControl<ComboBox>("ThemePicker")!;
        var themes = ThemeCatalog.BuiltInThemes.ToList();
        picker.ItemsSource = themes.Select(t => t.Name).ToList();
        var current = ThemeService.Instance.CurrentThemeName
                      ?? (ThemeService.Instance.EffectiveVariant == global::Avalonia.Styling.ThemeVariant.Light ? ThemeCatalog.LightName : ThemeCatalog.DarkName);
        picker.SelectedIndex = Math.Max(0, themes.FindIndex(t => t.Name == current));
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex >= 0)
                ThemeService.Instance.ApplyTheme(themes[picker.SelectedIndex]);
        };
    }

    /// <summary>One swatch per palette key, bound to the live resources (theme switches and editor previews show).</summary>
    private void BuildSwatches()
    {
        var panel = this.FindControl<WrapPanel>("SwatchPanel")!;
        foreach (var (key, description) in ThemeDefinition.PaletteKeys)
        {
            var chip = new Border
            {
                Height = 44,
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable(key + "Brush"));
            chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("Border0Brush"));

            var hex = new TextBlock { Classes = { "caption", "mono" }, FontSize = 11 };
            hex.Bind(TextBlock.TextProperty, hex.GetResourceObservable(key, v => v is Color c ? ThemeCatalog.ToHex(c) : "—"));

            var card = new Border
            {
                Classes = { "card" },
                Padding = new Thickness(0),
                Width = 158,
                Margin = new Thickness(0, 0, 10, 10),
                ClipToBounds = true,
                Child = new StackPanel
                {
                    Children =
                    {
                        chip,
                        new StackPanel
                        {
                            Margin = new Thickness(10, 8, 10, 10),
                            Spacing = 1,
                            Children =
                            {
                                new TextBlock { Text = key, FontWeight = FontWeight.SemiBold, FontSize = 12 },
                                hex,
                                new TextBlock
                                {
                                    Text = description, Classes = { "caption" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                                    MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Height = 30,
                                },
                            },
                        },
                    },
                },
            };
            ToolTip.SetTip(card, $"{key}Brush — {description}");
            panel.Children.Add(card);
        }
    }

    private void BuildSamples()
    {
        // Tree: real Core models, so the connection icon converters are exercised.
        var root = new RootNodeInfo(RootNodeType.Connection) { Name = "Connections" };
        var linux = Folder("Linux lab", true,
            Connection("web-01", CoreProtocol.SSH2, "Linux", "10.0.12.21"),
            Connection("db-01", CoreProtocol.SSH2, "Database", "10.0.12.40"),
            Connection("core-router", CoreProtocol.Telnet, "Router", "10.0.0.1"));
        var windows = Folder("Windows", true,
            Connection("dc-01", CoreProtocol.RDP, "Domain Controller", "dc-01.corp.local"),
            Connection("rdp-gateway", CoreProtocol.RDP, "mRemoteNG", "gw.corp.local"),
            Connection("kiosk", CoreProtocol.VNC, "mRemoteNG", "10.0.30.5"));
        var archive = Folder("Archive", false, Connection("old-web", CoreProtocol.HTTP, "Web Server", "intranet.old"));
        var rootNode = new GalleryNode(root, true) { Children = { linux, windows, archive, Connection("intranet", CoreProtocol.HTTPS, "mRemoteNG", "intranet.corp.local") } };
        var putty = new GalleryNode(new RootPuttySessionsNodeInfo { Name = "PuTTY Sessions" }, false);
        var tree = this.FindControl<TreeView>("SampleTree")!;
        tree.ItemsSource = new[] { rootNode, putty };
        tree.SelectedItem = windows.Children[1];

        var list = new[]
        {
            Connection("web-01", CoreProtocol.SSH2, "Linux", "10.0.12.21"),
            Connection("dc-01", CoreProtocol.RDP, "Windows", "dc-01.corp.local"),
            Connection("kiosk", CoreProtocol.VNC, "mRemoteNG", "10.0.30.5"),
            Connection("intranet", CoreProtocol.HTTPS, "mRemoteNG", "intranet.corp.local"),
        };
        var sampleList = this.FindControl<ListBox>("SampleList")!;
        sampleList.ItemsSource = list;
        sampleList.SelectedIndex = 1;
        this.FindControl<DataGrid>("SampleGrid")!.ItemsSource = list.Concat([Connection("switch-02", CoreProtocol.Telnet, "Switch", "10.0.0.2")]).ToList();

        var protocols = new object[]
        {
            CoreProtocol.SSH2, CoreProtocol.Telnet, CoreProtocol.Rlogin, CoreProtocol.RAW, CoreProtocol.RDP, CoreProtocol.VNC,
            CoreProtocol.ARD, CoreProtocol.HTTP, CoreProtocol.HTTPS, CoreProtocol.PowerShell, CoreProtocol.Terminal,
            CoreProtocol.WSL, ProtocolKind.Serial, CoreProtocol.IntApp, CoreProtocol.AnyDesk, ProtocolKind.SshSftp,
        };
        this.FindControl<ItemsControl>("ProtocolList")!.ItemsSource = protocols
            .Select(p => new GalleryNode(new ConnectionInfo(), false) { Name = p.ToString()!, Protocol = p })
            .ToList();

        string[] legacy =
        [
            "Linux", "Windows", "Apple", "Database", "Domain Controller", "Firewall", "Web Server", "Mail Server", "Router",
            "Switch", "ESX", "Virtual Machine", "File Server", "Backup", "Workstation", "Terminal Server", "Build Server",
            "Test Server", "Staging", "Production", "Admin", "Anti Virus", "WiFi", "RaspberryPi", "Log", "Fax", "PuTTY",
            "mRemoteNG",
        ];
        this.FindControl<ItemsControl>("LegacyIconList")!.ItemsSource = legacy
            .Select(name => new GalleryNode(new ConnectionInfo { Icon = name, Protocol = CoreProtocol.SSH2 }, false) { Name = name })
            .ToList();

        string[] protocolNames = ["SSH", "RDP", "VNC", "Telnet", "HTTPS", "PowerShell"];
        foreach (var (name, index) in new[] { ("SampleCombo", 1), ("ProtocolCombo", 0), ("DialogCombo", 0) })
        {
            var combo = this.FindControl<ComboBox>(name)!;
            combo.ItemsSource = protocolNames;
            combo.SelectedIndex = index;
        }
    }

    private static GalleryNode Folder(string name, bool expanded, params GalleryNode[] children)
    {
        var node = new GalleryNode(new ContainerInfo { Name = name }, expanded);
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }

    private static GalleryNode Connection(string name, CoreProtocol protocol, string icon, string host) =>
        new(new ConnectionInfo { Name = name, Protocol = protocol, Icon = icon, Hostname = host }, false) { Host = host };
}

/// <summary>Sample row for the gallery (wraps a Core model so the real converters are used).</summary>
public sealed class GalleryNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public GalleryNode(ConnectionInfo model, bool expanded)
    {
        Model = model;
        Name = model.Name;
        Protocol = model.Protocol;
        _isExpanded = expanded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionInfo Model { get; }

    public string Name { get; init; }

    public string Host { get; init; } = string.Empty;

    public object Protocol { get; init; }

    public bool IsFolder => Model is ContainerInfo;

    public List<GalleryNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
