using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>
/// TigerVNC <c>Xvnc</c> on display :63 (port 5963) offering only VeNCrypt X509Vnc, with a self-signed certificate
/// made for the run.
/// </summary>
public sealed class XvncX509Fixture : IDisposable
{
    public const int DisplayNumber = 63;
    public const int Port = 5963;
    public const string Password = "tls-pass";

    private readonly Process? _xvnc;
    private readonly StringBuilder _log = new();

    public XvncX509Fixture()
    {
        var xvnc = ExternalProcess.FindExecutable("Xvnc");
        var vncpasswd = ExternalProcess.FindExecutable("vncpasswd");
        if (xvnc is null || vncpasswd is null)
        {
            SkipReason = "Xvnc / vncpasswd (TigerVNC) not installed";
            return;
        }
        if (ExternalProcess.IsListening(Port) || !ExternalProcess.ClaimDisplay(DisplayNumber))
        {
            SkipReason = $"Port {Port} or display :{DisplayNumber} is already in use";
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(Path.Combine(Directory, "cert.pem"), certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(Directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
            Certificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        var passwordFile = Path.Combine(Directory, "passwd");
        using (var passwd = Process.Start(new ProcessStartInfo(vncpasswd, "-f")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!)
        {
            passwd.StandardInput.WriteLine(Password);
            passwd.StandardInput.Close();
            using var file = File.Create(passwordFile);
            passwd.StandardOutput.BaseStream.CopyTo(file);
            passwd.WaitForExit();
        }

        _xvnc = ExternalProcess.Start(xvnc,
        [
            $":{DisplayNumber}", "-rfbport", $"{Port}", "-SecurityTypes", "X509Vnc", "-PasswordFile", passwordFile,
            "-X509Cert", Path.Combine(Directory, "cert.pem"), "-X509Key", Path.Combine(Directory, "key.pem"),
            "-geometry", "320x200", "-depth", "24", "-localhost", "-nolisten", "tcp", "-AlwaysShared",
        ], _log);
        if (!ExternalProcess.WaitUntil(() => ExternalProcess.IsListening(Port), TimeSpan.FromSeconds(15), _xvnc))
        {
            lock (_log) SkipReason = $"Xvnc did not start: {_log}";
        }
    }

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"mremoteng-xvnc-x509-{Guid.NewGuid():N}");

    public X509Certificate2? Certificate { get; }

    public string? SkipReason { get; }

    public void Dispose()
    {
        ExternalProcess.Stop(_xvnc, DisplayNumber);
        Certificate?.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

[Trait("Category", "Integration")]
public sealed class XvncVeNCryptIntegrationTests : IClassFixture<XvncX509Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly XvncX509Fixture _server;

    public XvncVeNCryptIntegrationTests(XvncX509Fixture server) => _server = server;

    [SkippableFact]
    public async Task X509Vnc_ConnectsOverTls_AndDecodesTheDesktop()
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", XvncX509Fixture.Port);
        SslPolicyErrors seen = SslPolicyErrors.None;
        var options = new RfbClientOptions
        {
            Password = XvncX509Fixture.Password,
            TlsTargetHost = "localhost",
            ValidateServerCertificate = (X509Certificate2 certificate, SslPolicyErrors errors, out string? message) =>
            {
                seen = errors;
                message = null;
                return certificate.Thumbprint == _server.Certificate!.Thumbprint;
            },
        };

        using var client = await RfbClient.ConnectAsync(tcp.GetStream(), options).WaitAsync(Timeout);
        var updated = new TaskCompletionSource();
        client.FramebufferUpdated += (_, _) => updated.TrySetResult();
        client.Start();
        await updated.Task.WaitAsync(Timeout);

        client.SecurityType.Should().Be(RfbSecurityType.VeNCrypt);
        client.SecuritySubtype.Should().Be(VeNCryptSubtype.X509Vnc);
        client.IsEncrypted.Should().BeTrue();
        client.ServerCertificate!.Thumbprint.Should().Be(_server.Certificate!.Thumbprint);
        seen.Should().HaveFlag(SslPolicyErrors.RemoteCertificateChainErrors);
        client.Framebuffer.Width.Should().Be(320);
        client.Framebuffer.Height.Should().Be(200);
    }

    [SkippableFact]
    public async Task VncProtocol_PinsTheCertificateOnFirstUse_AndRefusesADifferentPin()
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        var pins = Path.Combine(_server.Directory, $"pins-{Guid.NewGuid():N}");
        var parameters = new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = XvncX509Fixture.Port,
            Protocol = ProtocolType.Vnc,
            Password = XvncX509Fixture.Password,
        };

        using (var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance, new VncCertificateStore(pins)))
        {
            await protocol.ConnectAsync(parameters).WaitAsync(Timeout);
            protocol.State.Should().Be(ConnectionState.Connected);
            protocol.SecurityType.Should().Be(RfbSecurityType.VeNCrypt);
            await protocol.DisconnectAsync();
        }
        File.ReadAllText(pins).Should().Contain($"127.0.0.1:{XvncX509Fixture.Port} {VncCertificateStore.Fingerprint(_server.Certificate!)}");

        using (var again = new VncProtocol(NullLogger<VncProtocol>.Instance, new VncCertificateStore(pins)))
        {
            await again.ConnectAsync(parameters).WaitAsync(Timeout);
            again.State.Should().Be(ConnectionState.Connected, "the pinned certificate is accepted");
            await again.DisconnectAsync();
        }

        File.WriteAllText(pins, $"127.0.0.1:{XvncX509Fixture.Port} SHA256:somethingElse{Environment.NewLine}");
        using var refused = new VncProtocol(NullLogger<VncProtocol>.Instance, new VncCertificateStore(pins));
        var act = () => refused.ConnectAsync(parameters).WaitAsync(Timeout);
        await act.Should().ThrowAsync<RfbAuthenticationException>().WithMessage("*has changed*");
    }
}
