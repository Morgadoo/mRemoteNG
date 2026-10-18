using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>VeNCrypt negotiation against an in-process server, including a real TLS handshake.</summary>
public sealed class VncVeNCryptTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeRfbServer _server = new();
    private readonly TcpClient _tcp = new();
    private RfbClient? _client;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _tcp.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<RfbClient> ConnectAsync(RfbClientOptions options, Func<Task> serverScript)
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        await _server.AcceptAsync();
        var clientTask = RfbClient.ConnectAsync(_tcp.GetStream(), options);
        var serverTask = serverScript();
        try
        {
            _client = await clientTask.WaitAsync(Timeout);
        }
        finally
        {
            if (serverTask.IsCompleted) await serverTask;
        }
        await serverTask.WaitAsync(Timeout);
        return _client;
    }

    /// <summary>Version 3.8, VeNCrypt chosen from <paramref name="topLevel"/>, VeNCrypt 0.2 agreed, sub-types offered.</summary>
    private async Task<uint> OfferSubtypesAsync(byte[] topLevel, params uint[] subtypes)
    {
        await _server.NegotiateVersionAsync("003.008");
        await _server.SendAsync(new RfbBytes().U8(topLevel.Length).Raw(topLevel));
        (await _server.ReceiveAsync(1))[0].Should().Be(RfbSecurityType.VeNCrypt);
        await _server.SendAsync(new RfbBytes().U8(0).U8(2));
        (await _server.ReceiveAsync(2)).Should().Equal(0, 2);
        var offer = new RfbBytes().U8(0).U8(subtypes.Length);
        foreach (var subtype in subtypes) offer.U32(subtype);
        await _server.SendAsync(offer);
        var choice = await _server.ReceiveAsync(4);
        return (uint)(choice[0] << 24 | choice[1] << 16 | choice[2] << 8 | choice[3]);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Timeout);
        return buffer;
    }

    private static X509Certificate2 SelfSignedCertificate(string name = "vnc.test")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Exported and reloaded so that SslStream can use the private key on every platform.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public async Task VeNCrypt_IsPreferred_AndItsUnencryptedVncSubtypeUsesTheVncPassword()
    {
        var client = await ConnectAsync(new RfbClientOptions { Password = "secret" }, async () =>
        {
            (await OfferSubtypesAsync([RfbSecurityType.VncAuthentication, RfbSecurityType.VeNCrypt],
                VeNCryptSubtype.TlsVnc, VeNCryptSubtype.VncAuthentication)).Should().Be(VeNCryptSubtype.VncAuthentication);
            var challenge = RandomNumberGenerator.GetBytes(16);
            await _server.SendAsync(challenge);
            (await _server.ReceiveAsync(16)).Should().Equal(VncAuthentication.ComputeResponse("secret", challenge));
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(32, 16, "plain");
        });

        client.SecurityType.Should().Be(RfbSecurityType.VeNCrypt);
        client.SecuritySubtype.Should().Be(VeNCryptSubtype.VncAuthentication);
        client.IsEncrypted.Should().BeFalse();
        client.DesktopName.Should().Be("plain");
    }

    [Fact]
    public async Task X509Vnc_RunsTheRestOfTheSessionOverTls_AndAsksTheValidator()
    {
        using var certificate = SelfSignedCertificate();
        X509Certificate2? validated = null;
        SslPolicyErrors seenErrors = SslPolicyErrors.None;
        var options = new RfbClientOptions
        {
            Password = "secret",
            TlsTargetHost = "vnc.test",
            ValidateServerCertificate = (X509Certificate2 c, SslPolicyErrors e, out string? message) =>
            {
                validated = c;
                seenErrors = e;
                message = "pinned";
                return true;
            },
        };

        var client = await ConnectAsync(options, async () =>
        {
            (await OfferSubtypesAsync([RfbSecurityType.VeNCrypt], VeNCryptSubtype.VncAuthentication, VeNCryptSubtype.X509Vnc))
                .Should().Be(VeNCryptSubtype.X509Vnc, "TLS is preferred over the unencrypted sub-type");
            await _server.SendAsync([1]);
            await using var tls = new SslStream(_server.Stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(certificate).WaitAsync(Timeout);

            var challenge = RandomNumberGenerator.GetBytes(16);
            await tls.WriteAsync(challenge);
            (await ReadExactlyAsync(tls, 16)).Should().Equal(VncAuthentication.ComputeResponse("secret", challenge));
            await tls.WriteAsync(new RfbBytes().U32(0).ToArray());

            (await ReadExactlyAsync(tls, 1))[0].Should().Be(1, "ClientInit (shared) arrives encrypted");
            await tls.WriteAsync(new RfbBytes().U16(8).U16(4)
                .U8(32).U8(24).U8(0).U8(1).U16(255).U16(255).U16(255).U8(16).U8(8).U8(0).U8(0).U8(0).U8(0)
                .String("tls desktop").ToArray());
            await ReadExactlyAsync(tls, 20);
            var header = await ReadExactlyAsync(tls, 4);
            await ReadExactlyAsync(tls, 4 * (header[2] << 8 | header[3]));
        });

        client.IsEncrypted.Should().BeTrue();
        client.SecuritySubtype.Should().Be(VeNCryptSubtype.X509Vnc);
        client.DesktopName.Should().Be("tls desktop");
        client.TlsVerdict.Should().Be("pinned");
        client.ServerCertificate!.Thumbprint.Should().Be(certificate.Thumbprint);
        validated!.Thumbprint.Should().Be(certificate.Thumbprint);
        seenErrors.Should().HaveFlag(SslPolicyErrors.RemoteCertificateChainErrors, "the certificate is self-signed");
    }

    [Fact]
    public async Task X509_RejectedCertificate_FailsWithTheValidatorsReason()
    {
        using var certificate = SelfSignedCertificate();
        var options = new RfbClientOptions
        {
            TlsTargetHost = "vnc.test",
            ValidateServerCertificate = (X509Certificate2 _, SslPolicyErrors _, out string? message) =>
            {
                message = "certificate changed";
                return false;
            },
        };

        var act = () => ConnectAsync(options, async () =>
        {
            await OfferSubtypesAsync([RfbSecurityType.VeNCrypt], VeNCryptSubtype.X509None);
            await _server.SendAsync([1]);
            await using var tls = new SslStream(_server.Stream, leaveInnerStreamOpen: true);
            try { await tls.AuthenticateAsServerAsync(certificate).WaitAsync(Timeout); }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { }
        });

        await act.Should().ThrowAsync<RfbAuthenticationException>().WithMessage("certificate changed");
    }

    [Fact]
    public async Task X509_WithoutAValidator_RejectsAnUntrustedCertificate()
    {
        using var certificate = SelfSignedCertificate();

        var act = () => ConnectAsync(new RfbClientOptions { TlsTargetHost = "vnc.test" }, async () =>
        {
            await OfferSubtypesAsync([RfbSecurityType.VeNCrypt], VeNCryptSubtype.X509None);
            await _server.SendAsync([1]);
            await using var tls = new SslStream(_server.Stream, leaveInnerStreamOpen: true);
            try { await tls.AuthenticateAsServerAsync(certificate).WaitAsync(Timeout); }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { }
        });

        await act.Should().ThrowAsync<RfbAuthenticationException>().WithMessage("*not trusted*");
    }

    [Fact]
    public async Task Plain_SendsTheUserNameAndPassword_AndFlagsThemAsCleartext()
    {
        var client = await ConnectAsync(new RfbClientOptions { Username = "alice", Password = "pässword" }, async () =>
        {
            (await OfferSubtypesAsync([RfbSecurityType.VeNCrypt], VeNCryptSubtype.Plain)).Should().Be(VeNCryptSubtype.Plain);
            var lengths = await _server.ReceiveAsync(8);
            lengths.Should().Equal(0, 0, 0, 5, 0, 0, 0, 9);
            Encoding.UTF8.GetString(await _server.ReceiveAsync(5)).Should().Be("alice");
            Encoding.UTF8.GetString(await _server.ReceiveAsync(9)).Should().Be("pässword");
            await _server.SendAsync(new RfbBytes().U32(0));
            await _server.InitialiseAsync(8, 8, "plain");
        });

        client.SentCleartextPassword.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticationFailure_NamesTheVeNCryptSubtype()
    {
        var act = () => ConnectAsync(new RfbClientOptions { Username = "alice", Password = "wrong" }, async () =>
        {
            await OfferSubtypesAsync([RfbSecurityType.VeNCrypt], VeNCryptSubtype.Plain);
            await _server.ReceiveAsync(8 + 5 + 5);
            await _server.SendAsync(new RfbBytes().U32(1).String("bad password"));
        });

        await act.Should().ThrowAsync<RfbAuthenticationException>().WithMessage("VeNCrypt Plain authentication failed: bad password");
    }

    [Theory]
    [InlineData(new byte[] { RfbSecurityType.VeNCrypt, RfbSecurityType.VncAuthentication }, true)]
    [InlineData(new byte[] { RfbSecurityType.VeNCrypt }, false)]
    public async Task OnlyAnonymousTls_IsUnsupported_AndSaysWhetherAnotherTypeWasOffered(byte[] topLevel, bool otherOffered)
    {
        var act = () => ConnectAsync(new RfbClientOptions { Password = "secret" }, async () =>
        {
            var offered = OfferSubtypesAsync(topLevel, VeNCryptSubtype.TlsNone, VeNCryptSubtype.TlsVnc);
            // The client gives up without choosing; the connection just closes.
            try { await offered; }
            catch (Exception ex) when (ex is IOException or EndOfStreamException) { }
        });

        (await act.Should().ThrowAsync<RfbVeNCryptUnsupportedException>().WithMessage("*TLSNone, TLSVnc*Anonymous TLS*"))
            .Which.OtherSecurityTypesOffered.Should().Be(otherOffered);
    }

    [Theory]
    [InlineData(null, null, new uint[] { 2, 1, 260, 261 }, 260u)]
    [InlineData(null, "pw", new uint[] { 2, 1, 260, 261 }, 261u)]
    [InlineData("user", "pw", new uint[] { 2, 261, 262, 256 }, 262u)]
    [InlineData(null, "pw", new uint[] { 1, 2, 256 }, 2u)]
    [InlineData(null, null, new uint[] { 2, 1 }, 1u)]
    [InlineData("user", null, new uint[] { 256, 2 }, 256u)]
    [InlineData(null, null, new uint[] { 261 }, 261u)]
    [InlineData(null, null, new uint[] { 257, 258, 259 }, null)]
    public void SubtypePreference_FavoursTls_ThenWhatTheCredentialsAllow(string? user, string? password, uint[] offered, uint? expected)
    {
        RfbClient.ChooseVeNCryptSubtype(offered, new RfbClientOptions { Username = user, Password = password })
            .Should().Be(expected);
    }

    // ── VncCertificateStore ────────────────────────────────────────────────

    [Fact]
    public void CertificateStore_PinsOnFirstUse_AcceptsTheSameCertificate_AndRejectsAChange()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mremoteng-vnc-pins-{Guid.NewGuid():N}", "vnc_certificates");
        try
        {
            using var first = SelfSignedCertificate();
            using var second = SelfSignedCertificate();
            var store = new VncCertificateStore(file);

            var initial = store.Verify("Server.Example", 5900, first, SslPolicyErrors.RemoteCertificateChainErrors);
            initial.IsTrusted.Should().BeTrue();
            initial.Message.Should().Contain("first use").And.Contain(VncCertificateStore.Fingerprint(first));
            File.ReadAllText(file).Should().Contain($"server.example:5900 {VncCertificateStore.Fingerprint(first)}");

            var reopened = new VncCertificateStore(file);
            reopened.Verify("server.example", 5900, first, SslPolicyErrors.RemoteCertificateChainErrors).IsTrusted.Should().BeTrue();
            var changed = reopened.Verify("server.example", 5900, second, SslPolicyErrors.RemoteCertificateChainErrors);
            changed.IsTrusted.Should().BeFalse();
            changed.Message.Should().Contain("has changed").And.Contain(file);
            reopened.Verify("server.example", 5901, second, SslPolicyErrors.RemoteCertificateChainErrors).IsTrusted
                .Should().BeTrue("pins are per host and port");
            reopened.Verify("server.example", 5900, second, SslPolicyErrors.None).IsTrusted
                .Should().BeTrue("a certificate the system trusts needs no pin");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
