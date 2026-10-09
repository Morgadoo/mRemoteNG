using FluentAssertions;
using mRemoteNG.Protocols.Ssh;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Protocols.Ssh.SshTestKeys;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public sealed class KnownHostsHostKeyVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mremoteng-verifier-" + Guid.NewGuid().ToString("N"));
    private readonly string _appFile;
    private readonly string _userFile;
    private readonly KnownHostsStore _store;
    private readonly ISshUserPrompt _prompt = Substitute.For<ISshUserPrompt>();
    private readonly KnownHostsHostKeyVerifier _verifier;

    public KnownHostsHostKeyVerifierTests()
    {
        Directory.CreateDirectory(_dir);
        _appFile = Path.Combine(_dir, "known_hosts");
        _userFile = Path.Combine(_dir, "user_known_hosts");
        _store = new KnownHostsStore(_appFile, [_userFile]);
        _verifier = new KnownHostsHostKeyVerifier(_store, _prompt);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HostKeyInfo Tampered(HostKeyInfo key)
    {
        var blob = (byte[])key.KeyBlob.Clone();
        blob[^1] ^= 0xFF;
        return new HostKeyInfo(key.Host, key.Port, blob);
    }

    [Fact]
    public void KnownMatchingKey_IsTrustedWithoutPrompt()
    {
        File.WriteAllText(_userFile, $"server ssh-ed25519 {Ed25519}\n");

        _verifier.Verify(Key("server", 22, Ed25519)).IsTrusted.Should().BeTrue();

        _prompt.DidNotReceiveWithAnyArgs().ConfirmHostKey(default!);
    }

    [Fact]
    public void UnknownKey_PromptsWithFingerprint_AndAcceptAndSaveStoresIt()
    {
        HostKeyPromptRequest? asked = null;
        _prompt.ConfirmHostKey(Arg.Do<HostKeyPromptRequest>(r => asked = r)).Returns(HostKeyDecision.AcceptAndSave);
        var key = Key("server", 2222, Ed25519);

        _verifier.Verify(key).IsTrusted.Should().BeTrue();

        asked!.Status.Should().Be(HostKeyStatus.Unknown);
        asked.HostKey.Fingerprint.Should().Be(Ed25519Fingerprint);
        asked.KnownHostsFile.Should().Be(_appFile);
        File.ReadAllText(_appFile).Should().Contain($"[server]:2222 ssh-ed25519 {Ed25519}");

        // A fresh verifier (new app run) trusts the stored key silently.
        var prompt = Substitute.For<ISshUserPrompt>();
        new KnownHostsHostKeyVerifier(_store, prompt).Verify(key).IsTrusted.Should().BeTrue();
        prompt.DidNotReceiveWithAnyArgs().ConfirmHostKey(default!);
    }

    [Fact]
    public void UnknownKey_AcceptOnce_TrustsForThisSessionWithoutStoring()
    {
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(HostKeyDecision.AcceptOnce);
        var key = Key("server", 22, Ed25519);

        _verifier.Verify(key).IsTrusted.Should().BeTrue();
        _verifier.Verify(key).IsTrusted.Should().BeTrue();

        _prompt.ReceivedWithAnyArgs(1).ConfirmHostKey(default!);
        File.Exists(_appFile).Should().BeFalse();
    }

    [Fact]
    public void UnknownKey_Rejected_IsNotTrustedOrStored()
    {
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(HostKeyDecision.Reject);

        var verdict = _verifier.Verify(Key("server", 22, Ed25519));

        verdict.IsTrusted.Should().BeFalse();
        verdict.Reason.Should().Contain(Ed25519Fingerprint);
        File.Exists(_appFile).Should().BeFalse();
    }

    [Fact]
    public void ChangedKey_IsRefusedByDefault_WithMismatchRequest()
    {
        var stored = Key("server", 22, Ed25519);
        _store.Add(stored);
        HostKeyPromptRequest? asked = null;
        _prompt.ConfirmHostKey(Arg.Do<HostKeyPromptRequest>(r => asked = r)).Returns(HostKeyDecision.Reject);

        var verdict = _verifier.Verify(Tampered(stored));

        verdict.IsTrusted.Should().BeFalse();
        verdict.Reason.Should().Contain("changed");
        asked!.Status.Should().Be(HostKeyStatus.Mismatch);
        asked.ConflictingEntries.Should().ContainSingle(e => e.Fingerprint == Ed25519Fingerprint);
    }

    [Theory]
    [InlineData(HostKeyDecision.AcceptOnce)]
    [InlineData(HostKeyDecision.AcceptAndSave)]
    public void ChangedKey_CannotBeAcceptedWithoutExplicitReplace(HostKeyDecision decision)
    {
        var stored = Key("server", 22, Ed25519);
        _store.Add(stored);
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(decision);

        _verifier.Verify(Tampered(stored)).IsTrusted.Should().BeFalse();
        _store.Check(stored).Status.Should().Be(HostKeyStatus.Trusted);
    }

    [Fact]
    public void ChangedKey_ReplaceAndSave_ReplacesStoredKey()
    {
        var stored = Key("server", 22, Ed25519);
        _store.Add(stored);
        var replacement = Tampered(stored);
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(HostKeyDecision.ReplaceAndSave);

        _verifier.Verify(replacement).IsTrusted.Should().BeTrue();

        _store.Check(replacement).Status.Should().Be(HostKeyStatus.Trusted);
        _store.Check(stored).Status.Should().Be(HostKeyStatus.Mismatch);
    }

    [Fact]
    public void ReplaceOnUnknownKey_IsNotAccepted()
    {
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(HostKeyDecision.ReplaceAndSave);

        _verifier.Verify(Key("server", 22, Ed25519)).IsTrusted.Should().BeFalse();
    }

    [Fact]
    public void RevokedKey_IsRefusedWithoutPrompt()
    {
        File.WriteAllText(_userFile, $"@revoked * ssh-ed25519 {Ed25519}\n");

        _verifier.Verify(Key("server", 22, Ed25519)).IsTrusted.Should().BeFalse();

        _prompt.DidNotReceiveWithAnyArgs().ConfirmHostKey(default!);
    }

    [Fact]
    public void PromptFailure_RejectsTheKey()
    {
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Throws(new InvalidOperationException("no UI"));

        _verifier.Verify(Key("server", 22, Ed25519)).IsTrusted.Should().BeFalse();
    }

    [Fact]
    public void UnknownKeyOfNewType_ForKnownHost_FlagsOtherKeyTypes()
    {
        _store.Add(Key("server", 22, Ed25519));
        HostKeyPromptRequest? asked = null;
        _prompt.ConfirmHostKey(Arg.Do<HostKeyPromptRequest>(r => asked = r)).Returns(HostKeyDecision.Reject);

        _verifier.Verify(Key("server", 22, Rsa));

        asked!.Status.Should().Be(HostKeyStatus.Unknown);
        asked.OtherKeyTypesKnown.Should().BeTrue();
    }

    [Fact]
    public void NonInteractivePrompt_RejectsUnknownKeys()
    {
        var verifier = new KnownHostsHostKeyVerifier(_store, new NonInteractiveSshUserPrompt());

        verifier.Verify(Key("server", 22, Ed25519)).IsTrusted.Should().BeFalse();
    }
}
