using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tools;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Tools;

/// <summary>
/// The Windows rules are the legacy ones: every case of mRemoteNGTests/Tools/ExternalToolsArgumentParserTests is
/// repeated here with the same input and expected output.
/// </summary>
public sealed class ExternalToolArgumentParserTests
{
    private const string TestString = @"()%!^abc123*<>&|""'\";
    private const string StringAfterMetacharacterEscaping = @"^(^)^%^!^^abc123*^<^>^&^|^""'\";
    private const string StringAfterAllEscaping = @"^(^)^%^!^^abc123*^<^>^&^|\^""'\";
    private const string StringAfterNoEscaping = TestString;
    private const string PortAsString = "9933";
    private const string SampleCommandString = @"/k echo ()%!^abc123*<>&|""'\";
    private const string ComSpec = @"C:\Windows\system32\cmd.exe";

    private static readonly ConnectionInfo Connection = new()
    {
        Name = TestString,
        Hostname = TestString,
        Port = 9933,
        Username = TestString,
        Password = TestString,
        Domain = TestString,
        Description = TestString,
        MacAddress = TestString,
        UserField = TestString,
    };

    private static string? Environment(string name) => name.Equals("COMSPEC", StringComparison.OrdinalIgnoreCase) ? ComSpec : null;

    private static ExternalToolArgumentParser WindowsParser(ConnectionInfo? connection = null) =>
        new(ExternalToolVariables.FromConnection(connection ?? Connection), ArgumentEscapingStyle.WindowsShell, Environment);

    public static TheoryData<string, string> LegacyCases()
    {
        var data = new TheoryData<string, string>();
        foreach (string name in (string[])["NAME", "HOSTNAME", "USERNAME", "PASSWORD", "DOMAIN", "DESCRIPTION", "MACADDRESS", "USERFIELD"])
        {
            data.Add($"%{name}%", StringAfterAllEscaping);
            data.Add($"%-{name}%", StringAfterMetacharacterEscaping);
            data.Add($"%!{name}%", StringAfterNoEscaping);
        }
        data.Add("%PORT%", PortAsString);
        data.Add("%-PORT%", PortAsString);
        data.Add("%!PORT%", PortAsString);
        // EmptyVariableTagsNotParsed
        data.Add("%%", "%%");
        // ParsingWorksWhenVariableIsNotInFirstPosition
        data.Add("/k echo %!USERNAME%", SampleCommandString);
        // EnvironmentVariablesParsed
        data.Add("%COMSPEC%", ComSpec);
        // UnsupportedParametersNotParsed
        data.Add("%UNSUPPORTEDPARAMETER%", "%UNSUPPORTEDPARAMETER%");
        // BackslashEscapedEnvironmentVariablesParsed
        data.Add(@"\%COMSPEC\%", ComSpec);
        // ChevronEscapedEnvironmentVariablesNotParsed
        data.Add("^%COMSPEC^%", "%COMSPEC%");
        return data;
    }

    [Theory]
    [MemberData(nameof(LegacyCases))]
    public void WindowsStyle_MatchesLegacyParser(string input, string expected) =>
        WindowsParser().ParseArguments(input).Should().Be(expected);

    [Fact]
    public void NullConnectionInfoResultsInEmptyVariables() =>
        new ExternalToolArgumentParser(null, ArgumentEscapingStyle.WindowsShell, Environment)
            .ParseArguments("test %USERNAME% test").Should().Be("test  test");

    [Fact]
    public void NullConnection_ReplacesEnvironmentVariablesWithEmptyText_AsLegacy() =>
        new ExternalToolArgumentParser(null, ArgumentEscapingStyle.WindowsShell, Environment)
            .ParseArguments("%COMSPEC%").Should().Be(string.Empty);

    [Theory]
    [InlineData("%hostname%")]
    [InlineData("%HostName%")]
    public void VariableNames_AreCaseInsensitive(string input) =>
        WindowsParser(new ConnectionInfo { Hostname = "srv01" }).ParseArguments(input).Should().Be("srv01");

    [Fact]
    public void SeveralVariables_AreReplacedInPlace()
    {
        var connection = new ConnectionInfo { Hostname = "srv01", Port = 2222, Username = "bob" };

        WindowsParser(connection).ParseArguments("-l %USERNAME% -P %PORT% %HOSTNAME%")
            .Should().Be("-l bob -P 2222 srv01");
    }

    [Fact]
    public void TrailingQuote_DoublesTrailingBackslashes_AsLegacy()
    {
        var connection = new ConnectionInfo { Hostname = @"path\" };

        WindowsParser(connection).ParseArguments("'%HOSTNAME%'").Should().Be(@"'path\\'");
    }

    [Fact]
    public void ProtocolVariable_IsTheConnectionsProtocol()
    {
        var connection = new ConnectionInfo { Protocol = mRemoteNG.Core.Connection.Protocol.ProtocolType.SSH2 };

        WindowsParser(connection).ParseArguments("%PROTOCOL%").Should().Be("SSH2");
    }

    [Fact]
    public void ValueThatEqualsItsOwnToken_FallsBackToTheEnvironment_AsLegacy()
    {
        var connection = new ConnectionInfo { Name = "%COMSPEC%" };

        WindowsParser(connection).ParseArguments("%COMSPEC%").Should().Be(ComSpec);
    }

    // ── POSIX style ────────────────────────────────────────────────────────

    private static ExternalToolArgumentParser PosixParser(ConnectionInfo connection) =>
        new(ExternalToolVariables.FromConnection(connection), ArgumentEscapingStyle.Posix, _ => null);

    [Theory]
    [InlineData("plain")]
    [InlineData(TestString)]
    [InlineData(@"back\slash ""double"" 'single' $HOME `cmd` ; rm -rf ~")]
    [InlineData(@"trailing\")]
    [InlineData("")]
    public void PosixStyle_QuotedVariable_IsExactlyOneArgument(string value)
    {
        var parsed = PosixParser(new ConnectionInfo { Password = value }).ParseArguments("--password \"%PASSWORD%\" end");

        CommandLineTokenizer.Split(parsed).Should().Equal("--password", value, "end");
    }

    [Theory]
    [InlineData("srv01")]
    [InlineData(@"a""b'c\d")]
    public void PosixStyle_UnquotedVariableWithoutWhitespace_IsOneArgument(string value)
    {
        var parsed = PosixParser(new ConnectionInfo { Hostname = value }).ParseArguments("ping %HOSTNAME%");

        CommandLineTokenizer.Split(parsed).Should().Equal("ping", value);
    }

    [Fact]
    public void PosixStyle_MinusEscapes_LikePlain()
    {
        var parsed = PosixParser(new ConnectionInfo { UserField = "x\"y" }).ParseArguments("%-USERFIELD%");

        CommandLineTokenizer.Split(parsed).Should().Equal("x\"y");
    }

    [Fact]
    public void PosixStyle_BangInsertsRawValue_WhichMayAddArguments()
    {
        var parsed = PosixParser(new ConnectionInfo { UserField = "-v -o \"A B\"" }).ParseArguments("ssh %!USERFIELD% host");

        CommandLineTokenizer.Split(parsed).Should().Equal("ssh", "-v", "-o", "A B", "host");
    }

    [Fact]
    public void PosixStyle_UsesEnvironmentVariables()
    {
        var parser = new ExternalToolArgumentParser(ExternalToolVariables.FromConnection(new ConnectionInfo()), ArgumentEscapingStyle.Posix,
            name => name == "HOME" ? "/home/me" : null);

        parser.ParseArguments("%HOME%/bin").Should().Be("/home/me/bin");
    }
}
