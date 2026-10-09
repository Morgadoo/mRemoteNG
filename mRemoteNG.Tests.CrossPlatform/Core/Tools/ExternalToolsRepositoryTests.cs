using System.Text;
using System.Xml;
using FluentAssertions;
using mRemoteNG.Core.Tools;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Tools;

public sealed class ExternalToolsRepositoryTests : IDisposable
{
    /// <summary>An extApps.xml as the Windows app writes it (UTF-8 BOM, CRLF, 4-space indent).</summary>
    private const string LegacyFile =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<Apps>\r\n" +
        "    <App DisplayName=\"Ping\" FileName=\"cmd\" Arguments=\"/K ping -t %HOSTNAME%\" WorkingDir=\"\" WaitForExit=\"False\" TryToIntegrate=\"False\" RunElevated=\"False\" ShowOnToolbar=\"True\" />\r\n" +
        "    <App DisplayName=\"PuTTY &amp; &quot;friends&quot;\" FileName=\"C:\\Program Files\\PuTTY\\putty.exe\" Arguments=\"-ssh %USERNAME%@%HOSTNAME% -pw %PASSWORD% &lt;x&gt;\" WorkingDir=\"C:\\Temp\" WaitForExit=\"False\" TryToIntegrate=\"True\" RunElevated=\"False\" ShowOnToolbar=\"False\" />\r\n" +
        "    <App DisplayName=\"Backup\" FileName=\"robocopy.exe\" Arguments=\"\\\\%HOSTNAME%\\c$ D:\\bk /MIR\" WorkingDir=\"\" WaitForExit=\"True\" TryToIntegrate=\"False\" RunElevated=\"True\" ShowOnToolbar=\"True\" />\r\n" +
        "</Apps>";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mrng-extapps-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static byte[] Utf8WithBom(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    private static List<ExternalTool> Read(byte[] bytes) => ExternalToolsRepository.Read(new MemoryStream(bytes));

    private static byte[] Write(IEnumerable<ExternalTool> tools)
    {
        var stream = new MemoryStream();
        ExternalToolsRepository.Write(stream, tools);
        return stream.ToArray();
    }

    [Fact]
    public void Read_LegacyFile_LoadsEveryField()
    {
        var tools = Read(Utf8WithBom(LegacyFile));

        tools.Should().HaveCount(3);
        tools[0].Should().BeEquivalentTo(new
        {
            DisplayName = "Ping",
            FileName = "cmd",
            Arguments = "/K ping -t %HOSTNAME%",
            WorkingDir = "",
            WaitForExit = false,
            TryIntegrate = false,
            RunElevated = false,
            ShowOnToolbar = true,
            Platform = ExternalToolPlatform.Any,
            IconPath = "",
        });
        tools[1].DisplayName.Should().Be("PuTTY & \"friends\"");
        tools[1].FileName.Should().Be(@"C:\Program Files\PuTTY\putty.exe");
        tools[1].Arguments.Should().Be("-ssh %USERNAME%@%HOSTNAME% -pw %PASSWORD% <x>");
        tools[1].WorkingDir.Should().Be(@"C:\Temp");
        tools[1].TryIntegrate.Should().BeTrue();
        tools[1].ShowOnToolbar.Should().BeFalse();
        tools[2].WaitForExit.Should().BeTrue();
        tools[2].RunElevated.Should().BeTrue();
    }

    [Fact]
    public void Write_LegacyFileContent_IsByteIdenticalToTheWindowsApp()
    {
        byte[] original = Utf8WithBom(LegacyFile);

        Write(Read(original)).Should().Equal(original);
    }

    [Fact]
    public void Write_MatchesTheLegacySaverAlgorithm()
    {
        // The legacy ExternalAppsSaver, verbatim except for the file name, with Windows line endings.
        var tools = Read(Utf8WithBom(LegacyFile));
        tools.Add(new ExternalTool("Tabs\tand\nnewlines", "x", "a 'b' \"c\" & d > e"));
        var legacy = new MemoryStream();
        using (var streamWriter = new StreamWriter(legacy, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" })
        {
            var xmlTextWriter = new XmlTextWriter(streamWriter) { Formatting = Formatting.Indented, Indentation = 4 };
            xmlTextWriter.WriteStartDocument();
            xmlTextWriter.WriteStartElement("Apps");
            foreach (var extA in tools)
            {
                xmlTextWriter.WriteStartElement("App");
                xmlTextWriter.WriteAttributeString("DisplayName", "", extA.DisplayName);
                xmlTextWriter.WriteAttributeString("FileName", "", extA.FileName);
                xmlTextWriter.WriteAttributeString("Arguments", "", extA.Arguments);
                xmlTextWriter.WriteAttributeString("WorkingDir", "", extA.WorkingDir);
                xmlTextWriter.WriteAttributeString("WaitForExit", "", Convert.ToString(extA.WaitForExit));
                xmlTextWriter.WriteAttributeString("TryToIntegrate", "", Convert.ToString(extA.TryIntegrate));
                xmlTextWriter.WriteAttributeString("RunElevated", "", Convert.ToString(extA.RunElevated));
                xmlTextWriter.WriteAttributeString("ShowOnToolbar", "", Convert.ToString(extA.ShowOnToolbar));
                xmlTextWriter.WriteEndElement();
            }
            xmlTextWriter.WriteEndElement();
            xmlTextWriter.WriteEndDocument();
            xmlTextWriter.Flush();
        }

        // XmlTextWriter declares the encoding the same way (utf-8) when given a UTF-8 StreamWriter.
        Encoding.UTF8.GetString(Write(tools)).Should().Be(Encoding.UTF8.GetString(legacy.ToArray()));
        Write(tools).Should().Equal(legacy.ToArray());
    }

    [Fact]
    public void Read_OldFileWithoutOptionalAttributes_UsesDefaults()
    {
        const string old = "<?xml version=\"1.0\"?><Apps><App DisplayName=\"Old\" FileName=\"old.exe\" Arguments=\"-x\" /></Apps>";

        var tool = Read(Encoding.UTF8.GetBytes(old)).Should().ContainSingle().Subject;

        tool.WorkingDir.Should().BeEmpty();
        tool.WaitForExit.Should().BeFalse();
        tool.TryIntegrate.Should().BeFalse();
        tool.RunElevated.Should().BeFalse();
        tool.ShowOnToolbar.Should().BeTrue("legacy tools default to being on the toolbar");
    }

    [Fact]
    public void Read_WaitForExitAndTryToIntegrate_TryToIntegrateWins_AsLegacy()
    {
        const string both = "<Apps><App DisplayName=\"x\" FileName=\"x\" Arguments=\"\" WaitForExit=\"True\" TryToIntegrate=\"True\" /></Apps>";

        var tool = Read(Encoding.UTF8.GetBytes(both)).Single();

        tool.TryIntegrate.Should().BeTrue();
        tool.WaitForExit.Should().BeFalse();
    }

    [Fact]
    public void NewAttributes_RoundTrip_AndAreOnlyWrittenWhenSet()
    {
        var tools = new List<ExternalTool>
        {
            new("Linux only", "xterm") { Platform = ExternalToolPlatform.Linux, IconPath = "/usr/share/icons/term.png" },
            new("Anywhere", "ping"),
        };

        byte[] bytes = Write(tools);
        string xml = Encoding.UTF8.GetString(bytes);
        var reread = Read(bytes);

        reread[0].Platform.Should().Be(ExternalToolPlatform.Linux);
        reread[0].IconPath.Should().Be("/usr/share/icons/term.png");
        reread[1].Platform.Should().Be(ExternalToolPlatform.Any);
        xml.Should().Contain("Platform=\"Linux\" Icon=\"/usr/share/icons/term.png\"");
        xml.Split("<App ").Last().Should().NotContain("Platform=").And.NotContain("Icon=");
    }

    [Fact]
    public void FileWrittenWithNewAttributes_StillHasEveryAttributeTheLegacyLoaderReads()
    {
        var bytes = Write([new ExternalTool("t", "f", "a", "w") { Platform = ExternalToolPlatform.Windows }]);
        var document = new XmlDocument();
        document.Load(new MemoryStream(bytes));

        var element = (XmlElement)document.DocumentElement!.ChildNodes[0]!;
        foreach (string name in (string[])["DisplayName", "FileName", "Arguments", "WorkingDir", "RunElevated", "WaitForExit", "TryToIntegrate", "ShowOnToolbar"])
            element.HasAttribute(name).Should().BeTrue(name);
        bool.Parse(element.GetAttribute("ShowOnToolbar")).Should().BeTrue();
    }

    [Fact]
    public void Read_NotAnAppsFile_Throws()
    {
        var act = () => Read(Encoding.UTF8.GetBytes("<Connections/>"));

        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void Read_DtdIsRejected()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE Apps [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Apps><App DisplayName=\"&x;\" FileName=\"f\" Arguments=\"\"/></Apps>";

        var act = () => Read(Encoding.UTF8.GetBytes(xxe));

        act.Should().Throw<XmlException>();
    }

    [Fact]
    public void SaveAndLoad_ThroughTheFileSystem_RoundTrip()
    {
        var repository = new ExternalToolsRepository(Path.Combine(_directory, "sub", ExternalToolsRepository.FileName));
        repository.Load().Should().BeNull("there is no file yet");

        repository.Save([new ExternalTool("A", "a"), new ExternalTool("B", "b") { WaitForExit = true }]);

        repository.Exists.Should().BeTrue();
        repository.Load()!.Select(t => (t.DisplayName, t.WaitForExit)).Should().Equal(("A", false), ("B", true));
        File.Exists(repository.FilePath + ".tmp").Should().BeFalse();
    }
}
