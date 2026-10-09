using FluentAssertions;
using mRemoteNG.Protocols.Rdp;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Rdp;

/// <summary>
/// Log lines below were captured from FreeRDP 3.32.1 (xfreerdp3) against xrdp 0.9.24 unless noted otherwise.
/// </summary>
public class FreeRdpOutputParserTests
{
    private static FreeRdpOutputParser Feed(params string[] lines)
    {
        var parser = new FreeRdpOutputParser();
        foreach (string line in lines)
            parser.Process(line);
        return parser;
    }

    [Fact]
    public void TransitionToActive_IsConnected()
    {
        const string line = "[09:37:30:589] [15407:00003c31] [DEBUG][com.freerdp.core.rdp] - [rdp_client_transition_to_state][0x562e9c415950]: CONNECTION_STATE_FINALIZATION_CLIENT_FONT_MAP --> CONNECTION_STATE_ACTIVE";

        new FreeRdpOutputParser().Process(line).Should().Be(FreeRdpSignal.Connected);
    }

    [Theory]
    [InlineData("[09:37:30:503] [15407:00003c31] [DEBUG][com.freerdp.core.rdp] - [rdp_client_transition_to_state][0x562e9c415950]: CONNECTION_STATE_INITIAL --> CONNECTION_STATE_NEGO")]
    [InlineData("[09:41:15:048] [19258:00004b3c] [ERROR][com.freerdp.core.rdp] - [rdp_recv_callback_int][0x558ae2764950]: CONNECTION_STATE_ACTIVE status STATE_RUN_FAILED [-1]")]
    [InlineData("[09:37:30:770] [15407:00003c31] [DEBUG][com.freerdp.core.rdp] - [rdp_read_share_control_header][0x562e9c415950]: type=PDU_TYPE_DATA[0x00000007], tpktLength=495, remainingLength=489")]
    [InlineData("")]
    public void OtherLines_AreNotConnected(string line)
    {
        new FreeRdpOutputParser().Process(line).Should().NotBe(FreeRdpSignal.Connected);
    }

    [Fact]
    public void FramebufferLine_IsOnlyAFramebufferSignal()
    {
        // Seen before post-connect, which can still fail afterwards (e.g. exit 138 when /sound:sys:pulse has no pulse).
        new FreeRdpOutputParser().Process("[09:22:44:186] [6883:00001ae5] [INFO][com.freerdp.gdi] - [gdi_init_ex]: Local framebuffer format  PIXEL_FORMAT_BGRX32")
            .Should().Be(FreeRdpSignal.FramebufferReady);
    }

    [Fact]
    public void NetworkDisconnect_IsReconnecting()
    {
        new FreeRdpOutputParser().Process("[09:41:15:048] [19258:00004b3c] [INFO][com.freerdp.client.common] - [client_auto_reconnect_ex]: Network disconnect!")
            .Should().Be(FreeRdpSignal.Reconnecting);
    }

    [Fact]
    public void ClosedPort_DescribesConnectFailure()
    {
        var parser = Feed(
            "[09:52:32:592] [3259:00000cbd] [ERROR][com.freerdp.core] - [get_next_addrinfo]: ERRCONNECT_CONNECT_FAILED [0x00020006]",
            "[09:52:32:593] [3259:00000cbd] [ERROR][com.freerdp.core.transport] - [transport_connect_layer]: ConnectLayer 127.0.0.1:3390 [15000ms] failed",
            "[09:52:32:593] [3259:00000cbd] [ERROR][com.freerdp.core.nego] - [nego_connect]: Failed to connect");

        parser.LastErrorCode.Should().Be("ERRCONNECT_CONNECT_FAILED");
        parser.DescribeFailure(141).Should().StartWith("Could not connect to the host");
    }

    [Fact]
    public void NlaRequiredButUnsupported_DescribesNegotiationFailure()
    {
        var parser = Feed(
            "[09:24:13:076] [9189:000023e9] [ERROR][com.freerdp.core.nego] - [nego_connect]: Protocol Security Negotiation Failure",
            "[09:24:13:076] [9189:000023e9] [ERROR][com.freerdp.core] - [rdp_client_connect]: ERRCONNECT_SECURITY_NEGO_CONNECT_FAILED [0x0002000C]");

        parser.DescribeFailure(133).Should().StartWith("Security negotiation failed");
    }

    [Fact]
    public void LogonFailure_DescribesAuthenticationFailure()
    {
        // Same "<code> [0x...]" format as the captured lines above; ERRCONNECT_LOGON_FAILURE is what FreeRDP sets
        // when NLA rejects the credentials (not reproducible with xrdp, which has no NLA).
        var parser = Feed("[10:00:00:000] [1:2] [ERROR][com.freerdp.core] - [nla_recv_pdu]: ERRCONNECT_LOGON_FAILURE [0x00020014]");

        parser.DescribeFailure(134).Should().Be("Authentication failed: the user name or password is incorrect.");
    }

    [Theory]
    [InlineData(132)]
    [InlineData(134)]
    [InlineData(154)]
    public void AuthExitCodes_WithoutLog_DescribeAuthenticationFailure(int exitCode)
    {
        new FreeRdpOutputParser().DescribeFailure(exitCode).Should().StartWith("Authentication failed");
    }

    [Fact]
    public void ChangedPinnedCertificate_ExplainsHowToResolve()
    {
        var parser = Feed(
            "!!!Certificate for 127.0.0.1:3389 (RDP-Server) has changed!!!",
            "[09:47:07:692] [29775:00007451] [ERROR][com.freerdp.crypto] - [tls_print_certificate_error]: @    WARNING: NEW HOST IDENTIFICATION!     @",
            "[09:47:07:692] [29775:00007451] [ERROR][com.freerdp.crypto] - [tls_print_certificate_error]: Add correct host key in /root/.config/freerdp/server/127.0.0.1_3389.pem to get rid of this message.",
            "Old Certificate details:",
            "[09:48:27:503] [31629:00007b8f] [ERROR][com.freerdp.crypto] - [freerdp_tls_handshake]: certificate not trusted, aborting.",
            "[09:48:27:503] [31629:00007b8f] [ERROR][com.freerdp.core] - [transport_default_connect_tls]: ERRCONNECT_TLS_CONNECT_FAILED [0x00020008]");

        string message = parser.DescribeFailure(143);

        message.Should().Contain("has changed").And.Contain("/root/.config/freerdp/server/127.0.0.1_3389.pem");
    }

    [Fact]
    public void UnknownCertificateWithDenyPolicy_IsNotReportedAsChanged()
    {
        // FreeRDP prints "REMOTE HOST IDENTIFICATION HAS CHANGED" for an unknown certificate as well.
        var parser = Feed(
            "[09:50:22:936] [906:0000038c] [ERROR][com.freerdp.crypto] - [tls_print_certificate_name_mismatch_error]: @           WARNING: CERTIFICATE NAME MISMATCH!           @",
            "[09:50:22:936] [906:0000038c] [ERROR][com.freerdp.crypto] - [tls_print_new_certificate_warn]: @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @",
            "[09:50:22:936] [906:0000038c] [ERROR][com.freerdp.crypto] - [tls_print_new_certificate_warn]: Host key verification failed.",
            "[09:50:22:936] [906:0000038c] [ERROR][com.freerdp.crypto] - [freerdp_tls_handshake]: certificate not trusted, aborting.",
            "[09:50:22:936] [906:0000038c] [ERROR][com.freerdp.core] - [transport_default_connect_tls]: ERRCONNECT_TLS_CONNECT_FAILED [0x00020008]",
            "[09:50:22:936] [906:0000038c] [INFO][com.freerdp.crypto] - [tls_verify_certificate]: No certificate stored, automatically denying.");

        string message = parser.DescribeFailure(143);

        message.Should().NotContain("has changed");
        message.Should().Contain("not trusted").And.Contain("different host name").And.Contain("rdp.certPolicy");
    }

    [Fact]
    public void CertificateWarningsOnSuccessfulFirstUse_DoNotAffectOtherFailures()
    {
        // TOFU first use: FreeRDP prints change/mismatch warnings but connects; a later unrelated failure must
        // not be blamed on the certificate.
        var parser = Feed(
            "[09:22:44:145] [6883:00001ae5] [ERROR][com.freerdp.crypto] - [tls_print_new_certificate_warn]: @    WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!     @",
            "[09:22:44:145] [6883:00001ae5] [INFO][com.freerdp.crypto] - [tls_verify_certificate]: No certificate stored, automatically accepting.",
            "[09:30:00:000] [6883:00001ae5] [ERROR][com.freerdp.core] - [freerdp_connect]: ERRCONNECT_POST_CONNECT_FAILED [0x00020003]");

        parser.DescribeFailure(138).Should().StartWith("FreeRDP failed to initialise the session");
    }

    [Fact]
    public void DecoderFailure_IsNotReportedAsUserCancel()
    {
        var parser = Feed(
            "[09:41:15:048] [19258:00004b3c] [ERROR][com.freerdp.codec] - [freerdp_bitmap_decompress_planar]: planar->maxWidth 872 < nSrcWidth 1072",
            "[09:41:15:048] [19258:00004b3c] [ERROR][com.freerdp.core.rdp] - [rdp_recv_callback_int][0x558ae2764950]: CONNECTION_STATE_ACTIVE status STATE_RUN_FAILED [-1]",
            "[09:41:15:048] [19258:00004b3c] [ERROR][com.freerdp.core] - [freerdp_abort_connect_context]: ERRCONNECT_CONNECT_CANCELLED [0x0002000B]");

        parser.DescribeFailure(145).Should().StartWith("FreeRDP aborted the session after failing to process data");
    }

    [Fact]
    public void ServerErrInfo_UsesFreeRdpDescription_AndCountsAsNormalEndForLogoff()
    {
        // FreeRDP's rdp_print_errinfo format: "<ERRINFO_NAME> (0x<code>):<description>".
        var parser = Feed("[10:00:00:000] [1:2] [INFO][com.freerdp.core] - [rdp_print_errinfo]: ERRINFO_LOGOFF_BY_USER (0x0000000C):The disconnection was initiated by the user logging off their session on the server.");

        parser.IsNormalSessionEnd(5).Should().BeTrue();
        parser.DescribeFailure(5).Should().StartWith("The disconnection was initiated by the user logging off");
    }

    [Fact]
    public void CredentialPromptWithoutTerminal_IsExplained()
    {
        // FreeRDP 3.32 without /p: and with stdin closed (the prompt text "Domain:" precedes the log line).
        var parser = Feed(
            "Domain:          [09:57:37:483] [8338:00002094] [ERROR][com.freerdp.client.common] - [client_cli_read_string]: freerdp_interruptible_get_line returned Inappropriate ioctl for device [25]",
            "[09:57:37:483] [8338:00002094] [ERROR][com.freerdp.core] - [transport_connect_tls]: ERRCONNECT_CONNECT_CANCELLED [0x0002000B]");

        parser.DescribeFailure(145).Should().StartWith("FreeRDP asked for credentials interactively");
    }

    [Fact]
    public void CommandLineRejected_IsExplained()
    {
        var parser = Feed("[09:25:05:000] [1:2] [ERROR][com.winpr.commandline] - [CommandLineParseArgumentsA]: Failed at index 2 [<censored>]: Unexpected keyword");

        parser.DescribeFailure(23).Should().Contain("rejected its command line");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(11, true)]
    [InlineData(131, false)]
    [InlineData(145, false)]
    public void NormalSessionEnd_ByExitCode(int exitCode, bool expected)
    {
        new FreeRdpOutputParser().IsNormalSessionEnd(exitCode).Should().Be(expected);
    }
}
