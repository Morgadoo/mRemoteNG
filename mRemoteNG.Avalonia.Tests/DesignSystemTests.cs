using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Material.Icons;
using mRemoteNG.Avalonia.Controls;
using mRemoteNG.Avalonia.Converters;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.Views.Dev;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tree.Root;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using ProtocolKind = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>The design-system foundation (docs/design-system.md): palettes, control styles, protocol visuals.</summary>
public class DesignSystemTests
{
    private static Color Resource(string key)
    {
        TestHost.MainWindow.TryFindResource(key, out var value).Should().BeTrue(key);
        return value switch
        {
            Color color => color,
            ISolidColorBrush brush => brush.Color,
            _ => throw new InvalidOperationException($"{key} is {value}"),
        };
    }

    private static Window Show(Control content)
    {
        _ = TestHost.MainWindow;
        var window = new Window { Content = content, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Palettes_DefineEveryEditableKey_WithTheCatalogValues()
    {
        _ = TestHost.MainWindow;
        try
        {
            foreach (var theme in new[] { ThemeCatalog.Dark, ThemeCatalog.Light })
            {
                ThemeService.Instance.Apply(theme.IsDark ? ThemeMode.Dark : ThemeMode.Light);
                Dispatcher.UIThread.RunJobs();
                foreach (var (key, _) in ThemeDefinition.PaletteKeys)
                {
                    var expected = Color.Parse(theme.Colors[key]);
                    Resource(key).Should().Be(expected, $"{theme.Name} {key}");
                    Resource(key + "Brush").Should().Be(expected, $"{theme.Name} {key}Brush");
                }
            }
        }
        finally
        {
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void FluentStateBrushes_AreThePaletteBrushes_SoLivePreviewRecoloursThem()
    {
        _ = TestHost.MainWindow;
        try
        {
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
            TestHost.MainWindow.TryFindResource("AppBg4Brush", out var hover).Should().BeTrue();
            TestHost.MainWindow.TryFindResource("ButtonBackgroundPointerOver", out var fluentHover).Should().BeTrue();
            fluentHover.Should().BeSameAs(hover);

            // The theme editor's preview recolours in place: the Fluent state, the accent colour key and the focus ring follow.
            ThemeService.Instance.PreviewColor("AppBg4", "#123456").Should().BeTrue();
            ThemeService.Instance.PreviewColor("Accent", "#FF8800").Should().BeTrue();
            Dispatcher.UIThread.RunJobs();
            Resource("ButtonBackgroundPointerOver").Should().Be(Color.Parse("#123456"));
            Resource("TreeViewItemBackgroundPointerOver").Should().Be(Color.Parse("#123456"));
            Resource("SystemAccentColor").Should().Be(Color.Parse("#FF8800"));
            TestHost.MainWindow.TryFindResource("FocusRingShadow", out var ring).Should().BeTrue();
            ((BoxShadows)ring!)[0].Color.Should().Be(Color.FromArgb(0x59, 0xFF, 0x88, 0x00));
        }
        finally
        {
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }

        Resource("AppBg4Brush").Should().Be(Color.Parse(ThemeCatalog.Dark.Colors["AppBg4"]), "the default palette is untouched");
    }

    [AvaloniaFact]
    public void UserThemeFromAnOlderVersion_Loads_AndFillsTheNewKeys()
    {
        _ = TestHost.MainWindow;
        var catalog = ThemeService.Instance.Catalog;
        Directory.CreateDirectory(catalog.UserThemesDirectory);
        var file = Path.Combine(catalog.UserThemesDirectory, "legacy_orange_test.json");
        // Written by the previous version: only the original twelve keys.
        File.WriteAllText(file, """
            {
              "name": "Legacy orange test",
              "isDark": true,
              "colors": {
                "AppBg0": "#101010", "AppBg1": "#181818", "AppBg2": "#202020", "AppBg3": "#303030", "AppBg4": "#404040",
                "TextPrimary": "#eeeeee", "TextSecondary": "#bbbbbb", "TextMuted": "#777777", "Accent": "#ff8800",
                "Border0": "#333333", "TextLink": "#66aaff", "Warning": "#ddaa00"
              }
            }
            """);
        try
        {
            var theme = catalog.Find("Legacy orange test");
            theme.Should().NotBeNull();
            var resolved = ThemeCatalog.ResolveColors(theme!);
            resolved.Keys.Should().Contain(ThemeDefinition.PaletteKeys.Select(k => k.Key));
            resolved["Border1"].Should().Be(ThemeCatalog.Dark.Colors["Border1"], "missing keys fall back to the base palette");
            resolved["AccentSubtle"].Should().Be("#2EFF8800", "accent variants are derived from the theme's accent");
            resolved["AccentHover"].Should().NotBe(ThemeCatalog.Dark.Colors["AccentHover"]);

            ThemeService.Instance.ApplyTheme(theme!);
            Dispatcher.UIThread.RunJobs();
            Resource("AppBg0Brush").Should().Be(Color.Parse("#101010"));
            Resource("AccentSubtleBrush").Should().Be(Color.Parse("#2EFF8800"));
            Resource("SuccessBrush").Should().Be(Color.Parse(ThemeCatalog.Dark.Colors["Success"]));
            Resource("WarningBrush").Should().Be(Color.Parse("#ddaa00"));
        }
        finally
        {
            File.Delete(file);
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ButtonVariants_UsePaletteColours()
    {
        var accent = new Button { Classes = { "accent" }, Content = "OK" };
        var destructive = new Button { Classes = { "accent", "danger" }, Content = "Delete" };
        var secondary = new Button { Content = "Cancel" };
        Show(new StackPanel { Children = { accent, destructive, secondary } });

        static Color Presenter(Button button) =>
            ((ISolidColorBrush)button.GetVisualDescendants().OfType<ContentPresenter>().First().Background!).Color;

        Presenter(accent).Should().Be(Resource("AccentBrush"));
        Presenter(destructive).Should().Be(Resource("DangerBrush"), "the filled danger variant overrides Fluent's accent button");
        Presenter(secondary).Should().Be(Resource("AppBg2Brush"));
    }

    [AvaloniaFact]
    public void SearchBox_ClearButton_EmptiesTheText()
    {
        var box = new TextBox { Classes = { "search" }, Text = "linux" };
        Show(box);
        var clear = box.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("search-clear"));
        clear.IsVisible.Should().BeTrue();
        clear.Command!.Execute(clear.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        box.Text.Should().BeEmpty();
        clear.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public void ProtocolVisuals_MapProtocolsAndConnections()
    {
        ProtocolVisuals.IconFor(CoreProtocol.SSH2).Should().Be(MaterialIconKind.Console);
        ProtocolVisuals.IconFor(ProtocolKind.Rdp).Should().Be(MaterialIconKind.MonitorScreenshot);
        ProtocolVisuals.IconFor("vnc").Should().Be(MaterialIconKind.MonitorEye);
        ProtocolVisuals.IconFor(ProtocolKind.Serial).Should().Be(MaterialIconKind.SerialPort);
        ProtocolVisuals.LabelFor(CoreProtocol.SSH2).Should().Be("SSH");
        ProtocolVisuals.FamilyOf(CoreProtocol.WSL).Should().Be(ProtocolFamily.Terminal);
        ProtocolVisuals.FamilyOf("Rlogin").Should().Be(ProtocolFamily.Telnet);

        ProtocolVisuals.IconForConnection(new ConnectionInfo { Icon = "Linux", Protocol = CoreProtocol.SSH2 }).Should().Be(MaterialIconKind.Linux);
        ProtocolVisuals.IconForConnection(new ConnectionInfo { Icon = "mRemoteNG", Protocol = CoreProtocol.RDP })
            .Should().Be(MaterialIconKind.MonitorScreenshot, "the default icon uses the protocol glyph");
        ProtocolVisuals.IconForConnection(new ContainerInfo(), expanded: true).Should().Be(MaterialIconKind.FolderOpen);
        ProtocolVisuals.IconForConnection(new ContainerInfo(), expanded: false).Should().Be(MaterialIconKind.Folder);
        ProtocolVisuals.IconForConnection(new RootNodeInfo(RootNodeType.Connection)).Should().Be(MaterialIconKind.Database);
        ProtocolVisuals.IconForConnection(new RootPuttySessionsNodeInfo()).Should().Be(MaterialIconKind.ConsoleNetworkOutline);
        ProtocolVisuals.BrushForConnection(new ContainerInfo()).Should().BeNull();
    }

    [AvaloniaFact]
    public void ProtocolBrushes_FollowThePalette()
    {
        _ = TestHost.MainWindow;
        var terminal = (ISolidColorBrush)ProtocolConverters.Brush.Convert(CoreProtocol.Terminal, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!;
        try
        {
            ThemeService.Instance.Apply(ThemeMode.Light);
            Dispatcher.UIThread.RunJobs();
            terminal.Color.Should().Be(Color.Parse(ThemeCatalog.Light.Colors["ProtoTerminal"]));

            var chip = new ProtocolChip { Protocol = CoreProtocol.HTTPS };
            Show(chip);
            chip.Label.Should().Be("HTTPS");
            ((ISolidColorBrush)chip.ProtocolBrush).Color.Should().Be(Color.Parse(ThemeCatalog.Light.Colors["ProtoHttp"]));
        }
        finally
        {
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }

        terminal.Color.Should().Be(Color.Parse(ThemeCatalog.Dark.Colors["ProtoTerminal"]));
    }

    [AvaloniaFact]
    public void FontSettings_ChangeTheUiFont_ButNotTheTypeRamp()
    {
        var window = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        var custom = settings.Current.Clone();
        custom.FontFamily = "DejaVu Sans";
        custom.FontSize = 15;
        var h1 = new TextBlock { Classes = { "h1" }, Text = "Title" };
        var mono = new TextBlock { Classes = { "mono" }, Text = "host" };
        var body = new TextBlock { Text = "Body" };
        var test = Show(new StackPanel { Children = { h1, mono, body } });
        try
        {
            ThemeService.Instance.ApplySettings(custom);
            Dispatcher.UIThread.RunJobs();
            window.FontSize.Should().Be(15);
            body.FontSize.Should().Be(15);
            body.FontFamily.Name.Should().StartWith("DejaVu Sans");
            h1.FontSize.Should().Be(20, "the type ramp keeps its sizes");
            mono.FontFamily.Name.Should().NotStartWith("DejaVu Sans", "mono keeps the monospace family");

            ThemeService.Instance.ApplySettings(settings.Current);
            Dispatcher.UIThread.RunJobs();
            window.FontSize.Should().Be(13, "the default settings restore the design's size");
            body.FontFamily.Name.Should().Contain("Inter");
        }
        finally
        {
            ThemeService.Instance.ApplySettings(settings.Current);
            Dispatcher.UIThread.RunJobs();
            test.Close();
        }
    }

    [AvaloniaFact]
    public void DesignGallery_Opens()
    {
        _ = TestHost.MainWindow;
        var gallery = new DesignGalleryWindow();
        gallery.Show();
        Dispatcher.UIThread.RunJobs();
        gallery.GetVisualDescendants().OfType<TreeViewItem>().Should().NotBeEmpty();
        gallery.Close();
    }
}
