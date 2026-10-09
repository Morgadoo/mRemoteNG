using System.Diagnostics;
using System.Text;
using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Import.ActiveDirectory;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

/// <summary>
/// A private OpenLDAP <c>slapd</c> on port 1389 with Microsoft's AD schema (msuser.schema) and a small fake
/// domain: OU=Servers (DB01, OU=Web/WEB01), OU=Workstations (PC-ALICE, no DNS name), CN=Computers (LEGACY01).
/// </summary>
public sealed class SlapdFixture : IDisposable
{
    public const int Port = 1389;
    public const string Suffix = "DC=corp,DC=example";
    public const string AdminDn = "CN=admin,DC=corp,DC=example";
    public const string AdminPassword = "secret";

    private const string SchemaDirectory = "/etc/ldap/schema";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mremoteng-slapd-{Guid.NewGuid():N}");
    private readonly StringBuilder _log = new();
    private readonly Process? _slapd;

    public SlapdFixture()
    {
        var slapd = ExternalProcess.FindExecutable("slapd") ?? (File.Exists("/usr/sbin/slapd") ? "/usr/sbin/slapd" : null);
        var slapadd = ExternalProcess.FindExecutable("slapadd") ?? (File.Exists("/usr/sbin/slapadd") ? "/usr/sbin/slapadd" : null);
        if (slapd is null || slapadd is null || !File.Exists(Path.Combine(SchemaDirectory, "msuser.schema")))
        {
            SkipReason = "OpenLDAP slapd with msuser.schema is not installed";
            return;
        }
        if (ExternalProcess.IsListening(Port))
        {
            SkipReason = $"Port {Port} is already in use";
            return;
        }

        Directory.CreateDirectory(Path.Combine(_directory, "db"));
        var config = Path.Combine(_directory, "slapd.conf");
        File.WriteAllText(config, string.Join('\n',
        [
            $"include {SchemaDirectory}/core.schema",
            $"include {SchemaDirectory}/cosine.schema",
            $"include {SchemaDirectory}/inetorgperson.schema",
            $"include {SchemaDirectory}/nis.schema",
            $"include {SchemaDirectory}/msuser.schema",
            "modulepath /usr/lib/ldap",
            "moduleload back_mdb",
            $"pidfile {_directory}/slapd.pid",
            "database mdb",
            $"suffix \"{Suffix}\"",
            $"rootdn \"{AdminDn}\"",
            $"rootpw {AdminPassword}",
            $"directory {_directory}/db",
            "",
        ]));
        var seed = Path.Combine(_directory, "seed.ldif");
        File.WriteAllText(seed, SeedLdif);

        using (var add = Process.Start(new ProcessStartInfo(slapadd)
        {
            ArgumentList = { "-f", config, "-l", seed },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!)
        {
            var errors = add.StandardError.ReadToEnd();
            add.StandardOutput.ReadToEnd();
            add.WaitForExit();
            if (add.ExitCode != 0)
            {
                SkipReason = $"slapadd failed: {errors}";
                return;
            }
        }

        // -d keeps slapd in the foreground so the test owns the process.
        _slapd = ExternalProcess.Start(slapd, ["-f", config, "-h", $"ldap://127.0.0.1:{Port}/", "-d", "0"], _log);
        if (!ExternalProcess.WaitUntil(() => ExternalProcess.IsListening(Port), TimeSpan.FromSeconds(15), _slapd))
        {
            lock (_log) SkipReason = $"slapd did not start: {_log}";
        }
    }

    public string? SkipReason { get; }

    public static LdapServerSettings Admin(string? password = AdminPassword) => new()
    {
        Server = "127.0.0.1",
        Port = Port,
        BindMode = LdapBindMode.Simple,
        Username = AdminDn,
        Password = password,
    };

    public void Dispose()
    {
        ExternalProcess.Stop(_slapd);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static string Computer(string dn, string cn, string? dnsHostName, string? description = null, string? os = null)
    {
        var entry = new StringBuilder()
            .AppendLine($"dn: {dn}")
            .AppendLine("objectClass: computer")
            .AppendLine($"cn: {cn}")
            .AppendLine($"sn: {cn}")
            .AppendLine("instanceType: 4")
            .AppendLine("nTSecurityDescriptor: x")
            .AppendLine($"objectCategory: CN=Computer,CN=Schema,CN=Configuration,{Suffix}");
        if (dnsHostName is not null) entry.AppendLine($"dNSHostName: {dnsHostName}");
        if (description is not null) entry.AppendLine($"description: {description}");
        if (os is not null) entry.AppendLine($"operatingSystem: {os}");
        return entry.AppendLine().ToString();
    }

    private static string SeedLdif =>
        $"""
        dn: {Suffix}
        objectClass: domain
        dc: corp

        dn: OU=Servers,{Suffix}
        objectClass: organizationalUnit
        ou: Servers
        description: Production servers

        dn: OU=Web,OU=Servers,{Suffix}
        objectClass: organizationalUnit
        ou: Web

        dn: OU=Workstations,{Suffix}
        objectClass: organizationalUnit
        ou: Workstations

        dn: CN=Computers,{Suffix}
        objectClass: organizationalRole
        cn: Computers


        """
        + Computer($"CN=WEB01,OU=Web,OU=Servers,{Suffix}", "WEB01", "web01.corp.example", "Front-end web server", "Windows Server 2022")
        + Computer($"CN=WEB02,OU=Web,OU=Servers,{Suffix}", "WEB02", "web02.corp.example", null, "Windows Server 2022")
        + Computer($"CN=DB01,OU=Servers,{Suffix}", "DB01", "db01.corp.example", "SQL", "Windows Server 2019")
        + Computer($"CN=PC-ALICE,OU=Workstations,{Suffix}", "PC-ALICE", null, null, "Windows 11")
        + Computer($"CN=LEGACY01,CN=Computers,{Suffix}", "LEGACY01", "legacy01.corp.example");
}

/// <summary>Active Directory browsing and import against the slapd fixture.</summary>
[Trait("Category", "Integration")]
public sealed class ActiveDirectoryImporterTests : IClassFixture<SlapdFixture>
{
    private readonly SlapdFixture _ldap;
    private readonly ConnectionImportService _service = new(new CryptoProviderFactory());

    public ActiveDirectoryImporterTests(SlapdFixture ldap) => _ldap = ldap;

    private const string Servers = "OU=Servers," + SlapdFixture.Suffix;

    [SkippableFact]
    public void Browser_FindsNamingContext_OUs_AndComputers()
    {
        Skip.If(_ldap.SkipReason is not null, _ldap.SkipReason);
        using var browser = new ActiveDirectoryBrowser(SlapdFixture.Admin());

        browser.GetDefaultNamingContext().Should().BeEquivalentTo(SlapdFixture.Suffix);
        var containers = browser.GetChildContainers(SlapdFixture.Suffix);
        // CN=Computers is an organizationalRole here (OpenLDAP's AD schema lacks "container"), so it is not listed.
        containers.Select(c => c.Name).Should().Equal("Servers", "Workstations");
        containers.Single(c => c.Name == "Servers").IsOrganizationalUnit.Should().BeTrue();
        browser.GetChildContainers(Servers).Select(c => c.Name).Should().Equal("Web");

        browser.GetComputers(Servers, includeSubtree: false).Select(c => c.Name).Should().Equal("DB01");
        var all = browser.GetComputers(Servers, includeSubtree: true);
        all.Select(c => c.Name).Should().Equal("DB01", "WEB01", "WEB02");
        all.Single(c => c.Name == "WEB01").Should().BeEquivalentTo(new
        {
            DnsHostName = "web01.corp.example",
            Description = "Front-end web server",
            OperatingSystem = "Windows Server 2022",
        });
    }

    [SkippableFact]
    public void Import_SubOus_WithFolders_MirrorsTheOuTree()
    {
        Skip.If(_ldap.SkipReason is not null, _ldap.SkipReason);
        var request = new ActiveDirectoryImportRequest
        {
            Server = SlapdFixture.Admin(password: null),
            BaseDn = Servers,
            IncludeSubOus = true,
            CreateOuFolders = true,
        };
        var root = NewRoot();

        // Through the import service with the URL as source, as the import dialog does.
        var result = _service.Import(ImportSourceType.ActiveDirectory, request.ToUrl(), root, SlapdFixture.AdminPassword);

        result.ConnectionCount.Should().Be(3);
        var folder = Folder(root.Children, "Servers");
        Connection(folder.Children, "DB01").Hostname.Should().Be("db01.corp.example");
        var web = Folder(folder.Children, "Web");
        var web01 = Connection(web.Children, "WEB01");
        web01.Hostname.Should().Be("web01.corp.example");
        web01.Description.Should().Be("Front-end web server");
        web01.Protocol.Should().Be(ProtocolType.RDP, "the protocol is inherited from the imported folder");
        web01.Port.Should().Be(3389);
        web01.Inheritance.Description.Should().BeFalse();
        web01.Inheritance.Protocol.Should().BeTrue();
    }

    [SkippableFact]
    public void Import_OneLevel_OnlyTakesComputersDirectlyInTheOu()
    {
        Skip.If(_ldap.SkipReason is not null, _ldap.SkipReason);
        var root = NewRoot();

        new ActiveDirectoryImporter(new ActiveDirectoryImportRequest { Server = SlapdFixture.Admin(), BaseDn = Servers })
            .Import("", root);

        Folder(root.Children, "Servers").Children.Select(c => c.Name).Should().Equal("DB01");

        new ActiveDirectoryImporter(new ActiveDirectoryImportRequest { Server = SlapdFixture.Admin(), BaseDn = "CN=Computers," + SlapdFixture.Suffix })
            .Import("", root);
        Folder(root.Children, "Computers").Children.Select(c => c.Hostname).Should().Equal("legacy01.corp.example");
    }

    [SkippableFact]
    public void Import_WithoutFolders_AndSelection_ImportsOnlyTheChosenComputersFlat()
    {
        Skip.If(_ldap.SkipReason is not null, _ldap.SkipReason);
        var root = NewRoot();
        var request = new ActiveDirectoryImportRequest
        {
            Server = SlapdFixture.Admin(),
            BaseDn = SlapdFixture.Suffix,
            IncludeSubOus = true,
            CreateOuFolders = false,
            SelectedComputers = [$"cn=web02,ou=web,ou=servers,{SlapdFixture.Suffix}", $"CN=PC-ALICE,OU=Workstations,{SlapdFixture.Suffix}"],
        };

        var result = new ActiveDirectoryImporter(request).Import("", root);

        var folder = Folder(root.Children, ActiveDirectoryImporter.DomainFolderName);
        folder.Children.Select(c => c.Name).Should().Equal("PC-ALICE", "WEB02");
        Connection(folder.Children, "PC-ALICE").Hostname.Should().Be("PC-ALICE", "without a DNS name the computer name is used");
        result.Warnings.Should().BeEmpty();
    }

    [SkippableFact]
    public void Import_NeedsThePassword_AndReportsAWrongOne()
    {
        Skip.If(_ldap.SkipReason is not null, _ldap.SkipReason);
        var url = new ActiveDirectoryImportRequest { Server = SlapdFixture.Admin(password: null), BaseDn = Servers }.ToUrl();

        var missing = () => _service.Import(ImportSourceType.ActiveDirectory, url, NewRoot());
        missing.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeFalse();

        var wrong = () => _service.Import(ImportSourceType.ActiveDirectory, url, NewRoot(), "not-it");
        wrong.Should().Throw<ConnectionFilePasswordException>().Which.PasswordWasSupplied.Should().BeTrue();
    }

    [SkippableFact]
    public void Import_UnreachableServer_IsAnIoError()
    {
        Skip.If(_ldap.SkipReason is not null, _ldap.SkipReason);
        var request = new ActiveDirectoryImportRequest
        {
            Server = SlapdFixture.Admin() with { Port = 1 },
            BaseDn = Servers,
        };

        var act = () => new ActiveDirectoryImporter(request).Import("", NewRoot());

        act.Should().Throw<IOException>().WithMessage("*Active Directory*");
    }
}

/// <summary>Request URL format and folder building without a directory server.</summary>
public sealed class ActiveDirectoryImportRequestTests
{
    [Fact]
    public void Url_RoundTripsEverythingButThePassword()
    {
        var request = new ActiveDirectoryImportRequest
        {
            Server = new LdapServerSettings
            {
                Server = "dc1.corp.example", Port = 3268, UseSsl = true, BindMode = LdapBindMode.Simple,
                Username = @"CORP\alice", Password = "never in the url",
            },
            BaseDn = "OU=Sales\\, EMEA,DC=corp,DC=example",
            IncludeSubOus = true,
            CreateOuFolders = false,
            SelectedComputers = ["CN=A,OU=Sales\\, EMEA,DC=corp,DC=example", "CN=B,DC=corp,DC=example"],
        };

        var url = request.ToUrl();
        var parsed = ActiveDirectoryImportRequest.Parse(url, "pw");

        url.Should().StartWith("ldaps://dc1.corp.example:3268/").And.NotContain("never");
        parsed.Should().BeEquivalentTo(request with { Server = request.Server with { Password = "pw" } });
    }

    [Theory]
    [InlineData("OU=Servers,DC=corp,DC=example", "Servers")]
    [InlineData("CN=Computers,DC=corp,DC=example", "Computers")]
    [InlineData("DC=corp,DC=example", "Active Directory")]
    public void FolderName_IsTheOuName(string dn, string expected) =>
        ActiveDirectoryImporter.FolderName(dn).Should().Be(expected);

    [Fact]
    public void Parse_RejectsOtherSchemes()
    {
        var act = () => ActiveDirectoryImportRequest.Parse("http://dc1/");
        act.Should().Throw<FormatException>();
    }

    private sealed class FakeDirectory(IReadOnlyList<DirectoryComputer> computers) : IDirectoryComputerSource
    {
        public IReadOnlyList<DirectoryComputer> GetComputers(string dn, bool includeSubtree) => computers;
        public void Dispose() { }
    }

    [Fact]
    public void Import_WarnsAboutSelectedComputersThatAreGone()
    {
        var importer = new ActiveDirectoryImporter(
            new ActiveDirectoryImportRequest
            {
                BaseDn = "OU=X,DC=a",
                SelectedComputers = ["CN=GONE,OU=X,DC=a", "CN=HERE,OU=X,DC=a"],
            },
            sourceFactory: _ => new FakeDirectory([new DirectoryComputer("CN=HERE,OU=X,DC=a", "HERE", "here.a", null, null)]));
        var root = NewRoot();

        var result = importer.Import("", root);

        Folder(root.Children, "X").Children.Should().ContainSingle().Which.Hostname.Should().Be("here.a");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("CN=GONE");
    }
}
