using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using mRemoteNG.ExternalProviders.Plugins;
using mRemoteNG.PluginContracts;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders.Plugins;

/// <summary>
/// The cross-platform plugin host: the real multi-address plugin (built for net10.0) loaded from a plugin folder in
/// its own load context, plus test plugins (in this assembly) for the other plugin kinds and failure cases.
/// </summary>
public sealed class PluginHostTests : IDisposable
{
    private const string MultiAddressId = "mRp.MultiAddress";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mremoteng-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();

    public PluginHostTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private PluginHost CreateHost(Version? hostVersion = null) =>
        new(() => _settings, hostVersion: hostVersion ?? new Version(1, 78, 2), defaultDirectories: () => [_folder]);

    private void CopyMultiAddressPlugin(string? folder = null)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "mRp.MultiAddress.dll");
        File.Exists(source).Should().BeTrue("the test project references the plugin");
        File.Copy(source, Path.Combine(folder ?? _folder, "mRp.MultiAddress.dll"));
    }

    private static ConnectionParameters Parameters(ConnectionInfo node) => new()
    {
        Hostname = node.Hostname,
        Port = node.Port,
        Protocol = mRemoteNG.Protocols.Abstractions.ProtocolType.Ssh,
        Username = node.Username,
    };

    [Fact]
    public void Load_FindsTheMultiAddressPlugin_InItsOwnLoadContext()
    {
        CopyMultiAddressPlugin();
        var host = CreateHost();

        host.Load();

        var entry = host.Catalog.Should().ContainSingle(e => e.Id == MultiAddressId).Subject;
        entry.State.Should().Be(PluginState.Loaded);
        entry.Version.Should().Be("1.0.0");
        entry.Kinds.Should().BeEquivalentTo("Address resolver", "Connection properties");
        var plugin = host.Plugins.Should().ContainSingle().Subject;
        System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly)
            .Should().BeOfType<PluginLoadContext>();
        plugin.Should().BeAssignableTo<IConnectionAddressResolverPlugin>("the plugin and the host share the app's contracts assembly");
    }

    [Fact]
    public void Load_IgnoresDllsThatAreNotPlugins_AndRunsOnce()
    {
        CopyMultiAddressPlugin();
        File.Copy(typeof(FluentActions).Assembly.Location, Path.Combine(_folder, "FluentAssertions.dll"));
        File.WriteAllBytes(Path.Combine(_folder, "native.dll"), [0x7F, 0x45, 0x4C, 0x46, 0, 1, 2, 3]);
        var host = CreateHost();

        host.Load();
        host.Load();

        host.Catalog.Should().ContainSingle().Which.Id.Should().Be(MultiAddressId);
        host.Plugins.Should().ContainSingle();
    }

    [Fact]
    public void Load_SkipsDisabledPlugins_AndSearchesTheConfiguredFolders()
    {
        var extra = Path.Combine(_folder, "extra");
        Directory.CreateDirectory(extra);
        CopyMultiAddressPlugin(extra);
        _settings.PluginFolderPaths = " ; " + extra + " ;";
        _settings.DisabledPlugins = "other;MRP.MULTIADDRESS";
        var host = CreateHost();

        host.SearchDirectories().Should().Equal(_folder, extra);
        host.Load();

        host.Plugins.Should().BeEmpty();
        host.Catalog.Should().ContainSingle().Which.State.Should().Be(PluginState.Disabled);
    }

    [Fact]
    public void GetConnectionProperties_MapsTheDefinitions_ToPluginDescriptors()
    {
        CopyMultiAddressPlugin();
        var host = CreateHost();
        host.Load();

        var properties = host.GetConnectionProperties();

        properties.Select(p => p.Name).Should().Equal(
            "mRp.MultiAddress.Enabled", "mRp.MultiAddress.Hostname", "mRp.MultiAddress.IpAddress",
            "mRp.MultiAddress.UseIpAddressAsPrimary", "mRp.MultiAddress.VerifyHostnameMatchesIp");
        properties.Should().OnlyContain(p => p.IsPluginProperty && p.Category == ConnectionPropertyCategories.Plugins && !p.SupportsInheritance);
        properties[0].Editor.Should().Be(ConnectionPropertyEditor.Boolean);
        properties[2].Editor.Should().Be(ConnectionPropertyEditor.Text);
        properties[2].DisplayName.Should().Be("IP", "the plugin's text comes from the app's resources (legacy key IP)");
    }

    [Fact]
    public async Task ResolveAddress_UsesTheResolversChoice_WithoutChangingTheSavedHostName()
    {
        CopyMultiAddressPlugin();
        var host = CreateHost();
        host.Load();
        var node = new ConnectionInfo { Name = "web", Hostname = "web.example", Protocol = CoreProtocolType.SSH2, Port = 22 };
        node.SetPluginProperty("mRp.MultiAddress.Enabled", "True");
        node.SetPluginProperty("mRp.MultiAddress.Hostname", "web.example");
        node.SetPluginProperty("mRp.MultiAddress.IpAddress", "10.1.2.3");
        node.SetPluginProperty("mRp.MultiAddress.UseIpAddressAsPrimary", "True");

        var resolved = await new PluginAddressPreparationStep(host)
            .PrepareAsync(new PreparationContext(node, ConnectOptions.Default), Parameters(node), CancellationToken.None);

        resolved.Hostname.Should().Be("10.1.2.3");
        resolved.Port.Should().Be(22);
        node.Hostname.Should().Be("web.example");
    }

    [Fact]
    public async Task ResolveAddress_LeavesConnectionsWithoutPluginSettingsAlone()
    {
        CopyMultiAddressPlugin();
        var host = CreateHost();
        host.Load();
        var node = new ConnectionInfo { Name = "plain", Hostname = "plain.example", Protocol = CoreProtocolType.SSH2 };
        var parameters = Parameters(node);

        var resolved = await host.ResolveAddressAsync(node, parameters);

        resolved.Should().BeSameAs(parameters);
    }

    // ── Test plugins in this assembly ────────────────────────────────────

    private PluginHost LoadTestPlugins(Version? hostVersion = null, string disabled = "")
    {
        var host = CreateHost(hostVersion);
        host.LoadPlugins(typeof(PluginHostTests).Assembly, "tests.dll", PluginHost.ParseIds(disabled));
        return host;
    }

    [Fact]
    public void LoadPlugins_RecordsIncompatibleDuplicateAndFailingPlugins()
    {
        var host = LoadTestPlugins();

        host.Catalog.Single(e => e.Id == NewerHostPlugin.PluginId).State.Should().Be(PluginState.Incompatible);
        host.Catalog.Single(e => e.Id == FailingPlugin.PluginId).Should().Match<PluginCatalogEntry>(e =>
            e.State == PluginState.Failed && e.Message == "boom");
        host.Catalog.Where(e => e.Id == TreeActionPlugin.PluginId).Select(e => e.State)
            .Should().BeEquivalentTo([PluginState.Loaded, PluginState.Duplicate]);
        host.Plugins.Select(p => p.Id).Should().Contain([TreeActionPlugin.PluginId, ImportingPlugin.PluginId])
            .And.NotContain([NewerHostPlugin.PluginId, FailingPlugin.PluginId]);
    }

    [Fact]
    public void LoadPlugins_DoesNotInitializeDisabledPlugins()
    {
        var host = LoadTestPlugins(disabled: ImportingPlugin.PluginId);

        host.Catalog.Single(e => e.Id == ImportingPlugin.PluginId).State.Should().Be(PluginState.Disabled);
        host.Plugins.Should().NotContain(p => p.Id == ImportingPlugin.PluginId);
    }

    [Fact]
    public void TreeActions_ApplyToMatchingNodes_AndWriteToTheNode()
    {
        var host = LoadTestPlugins();
        var ssh = new ConnectionInfo { Name = "ssh", Hostname = "a", Protocol = CoreProtocolType.SSH2 };
        var rdp = new ConnectionInfo { Name = "rdp", Hostname = "b", Protocol = CoreProtocolType.RDP };

        var actions = host.GetTreeActions(ssh);

        actions.Should().ContainSingle().Which.TreeContextMenuAction.MenuText.Should().Be("Tag it");
        host.GetTreeActions(rdp).Should().BeEmpty();
        host.ExecuteTreeAction(actions[0], ssh).Should().BeTrue();
        ssh.GetPluginProperty("test.tag").Should().Be("ssh2");
    }

    [Fact]
    public void ExecuteTreeAction_ReportsAFailure_AsAPluginError()
    {
        var host = LoadTestPlugins();
        var messages = new List<PluginMessage>();
        host.MessageWritten += (_, m) => messages.Add(m);
        var node = new ConnectionInfo { Name = "boom", Hostname = "a", Protocol = CoreProtocolType.SSH2 };

        host.ExecuteTreeAction(host.GetTreeActions(node)[0], node).Should().BeFalse();

        messages.Should().ContainSingle().Which.Should().Match<PluginMessage>(m =>
            m.Level == PluginMessageLevel.Error && m.Text.Contains("cannot tag boom") && !m.LogOnly && m.Exception != null);
    }

    [Fact]
    public void ImportConnections_HandsTheNewNodesToTheApp()
    {
        var host = LoadTestPlugins();
        IReadOnlyList<ConnectionInfo>? imported = null;
        host.ImportHandler = nodes => imported = nodes;

        ImportingPlugin.Context!.Connections.ImportConnections(
        [
            new PluginConnectionRequest { Name = "db", Hostname = "10.0.0.5", ProtocolId = PluginProtocolIds.Ssh2 },
            new PluginConnectionRequest { Hostname = "web", ProtocolId = PluginProtocolIds.Rdp, Port = 3390 },
            new PluginConnectionRequest { Hostname = " ", ProtocolId = PluginProtocolIds.Vnc },
        ]);

        imported.Should().NotBeNull();
        imported!.Select(n => (n.Name, n.Hostname, n.Protocol, n.Port)).Should().Equal(
            ("db", "10.0.0.5", CoreProtocolType.SSH2, 22),
            ("web", "web", CoreProtocolType.RDP, 3390));
    }

    [Fact]
    public void ImportConnections_WithoutAnOpenFile_WarnsInsteadOfThrowing()
    {
        var host = LoadTestPlugins();
        var messages = new List<PluginMessage>();
        host.MessageWritten += (_, m) => messages.Add(m);

        ImportingPlugin.Context!.Connections.ImportConnections([new PluginConnectionRequest { Hostname = "x", ProtocolId = "ssh2" }]);

        messages.Should().ContainSingle().Which.Level.Should().Be(PluginMessageLevel.Warning);
        FluentActions.Invoking(() => ImportingPlugin.Context.Connections.ImportConnections(
                [new PluginConnectionRequest { Hostname = "x", ProtocolId = "gopher" }]))
            .Should().Throw<ArgumentException>().WithMessage("*gopher*");
    }

    [Fact]
    public void Resources_UseTheAppsStrings_AndHaveNoImages()
    {
        var host = LoadTestPlugins();
        var resources = ImportingPlugin.Context!.Resources;

        resources.GetString("Plugins", "fallback").Should().Be("Plugins");
        resources.GetString("NoSuchKey-" + Guid.NewGuid(), "fallback").Should().Be("fallback");
        resources.GetImage("Help").Should().BeNull();
    }

    [Fact]
    public async Task ResolveAddress_WrapsAResolverFailure()
    {
        var host = LoadTestPlugins();
        var node = new ConnectionInfo { Name = "x", Hostname = "x", Protocol = CoreProtocolType.SSH2 };
        node.SetPluginProperty("test.fail", "yes");

        var act = () => host.ResolveAddressAsync(node, Parameters(node));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Test resolver: unreachable");
    }

    [Theory]
    [InlineData(CoreProtocolType.SSH2, "ssh2")]
    [InlineData(CoreProtocolType.VNC, "vnc")]
    [InlineData(CoreProtocolType.PowerShell, "powershell")]
    public void ProtocolIds_MatchTheWinFormsApp(CoreProtocolType protocol, string id)
    {
        PluginProtocols.ToPluginId(protocol).Should().Be(id);
        PluginProtocols.TryToCore(id, out var back).Should().BeTrue();
        back.Should().Be(protocol);
    }

    public sealed class TreeActionPlugin : ITreeContextActionPlugin
    {
        public const string PluginId = "test.tree";
        public string Id => PluginId;
        public string DisplayName => "Tree action";
        public Version Version => new(2, 0);
        public Version MinimumHostVersion => new(1, 0);
        public TreeContextMenuAction TreeContextMenuAction { get; } = new() { MenuText = "Tag it", SortOrder = 5 };
        public void Initialize(IPluginContext context) { }
        public bool CanExecute(IPluginConnection connection) => !connection.IsContainer && connection.ProtocolId == PluginProtocolIds.Ssh2;

        public void Execute(IPluginConnection connection)
        {
            if (connection.Name == "boom")
                throw new InvalidOperationException("cannot tag boom");
            connection.SetPluginProperty("test.tag", connection.ProtocolId);
        }
    }

    /// <summary>Same id as <see cref="TreeActionPlugin"/>: loaded second, so it is a duplicate.</summary>
    public sealed class ZDuplicatePlugin : IPlugin
    {
        public string Id => TreeActionPlugin.PluginId;
        public string DisplayName => "Duplicate";
        public Version Version => new(1, 0);
        public Version MinimumHostVersion => new(1, 0);
        public void Initialize(IPluginContext context) => throw new InvalidOperationException("must not be initialized");
    }

    public sealed class NewerHostPlugin : IPlugin
    {
        public const string PluginId = "test.newer";
        public string Id => PluginId;
        public string DisplayName => "Newer";
        public Version Version => new(1, 0);
        public Version MinimumHostVersion => new(99, 0);
        public void Initialize(IPluginContext context) => throw new InvalidOperationException("must not be initialized");
    }

    public sealed class FailingPlugin : IPlugin
    {
        public const string PluginId = "test.failing";
        public string Id => PluginId;
        public string DisplayName => "Failing";
        public Version Version => new(1, 0);
        public Version MinimumHostVersion => new(1, 0);
        public void Initialize(IPluginContext context) => throw new InvalidOperationException("boom");
    }

    public sealed class ImportingPlugin : IConnectionAddressResolverPlugin
    {
        public const string PluginId = "test.importing";
        public static IPluginContext? Context { get; private set; }
        public string Id => PluginId;
        public string DisplayName => "Test resolver";
        public Version Version => new(1, 0);
        public Version MinimumHostVersion => new(1, 0);
        public void Initialize(IPluginContext context) => Context = context;
        public bool CanResolve(IPluginConnection connection) => connection.TryGetPluginProperty("test.fail", out _);
        public Task ResolveAsync(IPluginConnection connection, CancellationToken cancellationToken) =>
            throw new IOException("unreachable");
    }
}
