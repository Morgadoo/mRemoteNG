using FluentAssertions;
using mRemoteNG.Core.Credential;
using mRemoteNG.Platform.Linux.Security;
using mRemoteNG.Platform.Security;
using NSubstitute;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

public sealed class FileCredentialRepositoryTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _filePath;
    private readonly ICryptoProvider _crypto;

    public FileCredentialRepositoryTests()
    {
        _filePath = _dir.Combine(FileCredentialRepository.DefaultFileName);
        _crypto = LinuxCryptoProvider.FromKeyFile(_dir.Combine(".keyfile"));
    }

    public void Dispose() => _dir.Dispose();

    private FileCredentialRepository CreateLoaded(ICryptoProvider? crypto = null)
    {
        var repository = new FileCredentialRepository(_filePath, crypto ?? _crypto);
        repository.LoadCredentials();
        return repository;
    }

    private static CredentialRecord Sample(string title = "Domain admin") => new()
    {
        Title = title,
        Username = "administrator",
        Password = "S3cr3t-P@ss",
        Domain = "CORP",
    };

    [Fact]
    public void Load_WithoutFile_IsEmpty()
    {
        var repository = CreateLoaded();
        repository.IsLoaded.Should().BeTrue();
        repository.CredentialRecords.Should().BeEmpty();
    }

    [Fact]
    public void AddAndSave_SurvivesRestart()
    {
        var record = Sample();
        var repository = CreateLoaded();
        repository.AddCredential(record);
        repository.SaveCredentials();

        var restarted = CreateLoaded(LinuxCryptoProvider.FromKeyFile(_dir.Combine(".keyfile")));

        restarted.CredentialRecords.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
    }

    [Fact]
    public void SavedFile_DoesNotContainPlaintextPassword()
    {
        var repository = CreateLoaded();
        repository.AddCredential(Sample());
        repository.SaveCredentials();

        var contents = File.ReadAllText(_filePath);
        contents.Should().NotContain("S3cr3t-P@ss");
        contents.Should().Contain("password=\"AESGCM:");
        contents.Should().Contain("username=\"administrator\"");
    }

    [SkippableFact]
    public void SavedFile_IsOwnerOnly()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix permissions only");
        var repository = CreateLoaded();
        repository.AddCredential(Sample());
        repository.SaveCredentials();

        File.GetUnixFileMode(_filePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void EditAndDelete_ArePersisted()
    {
        var keep = Sample("Keep");
        var remove = Sample("Remove");
        var repository = CreateLoaded();
        repository.AddCredential(keep);
        repository.AddCredential(remove);
        repository.SaveCredentials();

        var second = CreateLoaded();
        var edited = second.GetCredentialRecord(keep.Id)!;
        edited.Password = "changed";
        edited.Username = "root";
        second.RemoveCredential(second.GetCredentialRecord(remove.Id)!);
        second.SaveCredentials();

        var third = CreateLoaded();
        third.CredentialRecords.Should().ContainSingle();
        third.GetCredentialRecord(keep.Id)!.Password.Should().Be("changed");
        third.GetCredentialRecord(keep.Id)!.Username.Should().Be("root");
        third.GetCredentialRecord(remove.Id).Should().BeNull();
    }

    [Fact]
    public void ReplaceAllAndSave_CopiesRecords_AndSaves()
    {
        var repository = CreateLoaded();
        var working = Sample();

        repository.ReplaceAllAndSave([working]);
        working.Password = "edited after save";

        repository.GetCredentialRecord(working.Id)!.Password.Should().Be("S3cr3t-P@ss");
        CreateLoaded().GetCredentialRecord(working.Id)!.Password.Should().Be("S3cr3t-P@ss");
    }

    [Fact]
    public void ReplaceAllAndSave_RejectsDuplicateIds()
    {
        var record = Sample();
        var act = () => CreateLoaded().ReplaceAllAndSave([record, record]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lookup_ByIdAndTitle()
    {
        var record = Sample("Prod SSH");
        var repository = CreateLoaded();
        repository.ReplaceAllAndSave([record, Sample("Other")]);

        ICredentialLookup lookup = CreateLoaded();
        lookup.GetCredentialRecord(record.Id)!.Title.Should().Be("Prod SSH");
        lookup.FindByTitle("prod ssh")!.Id.Should().Be(record.Id);
        lookup.FindByTitle("missing").Should().BeNull();
        lookup.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void EmptyPassword_IsStoredEmpty()
    {
        var record = Sample();
        record.Password = string.Empty;
        CreateLoaded().ReplaceAllAndSave([record]);

        CreateLoaded().GetCredentialRecord(record.Id)!.Password.Should().BeEmpty();
    }

    [Fact]
    public void PasswordFromAnotherPlatform_IsKeptUntilReplaced()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(_filePath,
            $"<Credentials version=\"1\"><Credential id=\"{id}\" title=\"From Windows\" username=\"u\" domain=\"\" password=\"DPAPI:AQIDBA==\" /></Credentials>");

        var repository = CreateLoaded();
        repository.GetCredentialRecord(id)!.Password.Should().BeEmpty();
        repository.UndecryptableRecordIds.Should().Contain(id);

        // Saving without touching the password must not destroy it.
        repository.SaveCredentials();
        File.ReadAllText(_filePath).Should().Contain("password=\"DPAPI:AQIDBA==\"");

        // Entering a new password replaces it.
        CreateLoaded().GetCredentialRecord(id)!.Password.Should().BeEmpty();
        var again = CreateLoaded();
        again.GetCredentialRecord(id)!.Password = "new";
        again.SaveCredentials();
        CreateLoaded().GetCredentialRecord(id)!.Password.Should().Be("new");
    }

    [Fact]
    public void PasswordEncryptedWithAnotherKey_IsKeptAndNotThrown()
    {
        var record = Sample();
        CreateLoaded().ReplaceAllAndSave([record]);

        var otherKey = LinuxCryptoProvider.FromKeyFile(_dir.Combine("other.key"));
        var repository = CreateLoaded(otherKey);

        repository.GetCredentialRecord(record.Id)!.Password.Should().BeEmpty();
        repository.UndecryptableRecordIds.Should().Contain(record.Id);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("<Credentials><Credential id=\"not-a-guid\" /></Credentials>")]
    [InlineData("<Other />")]
    public void CorruptFile_IsBackedUp_AndRepositoryStartsEmpty(string contents)
    {
        File.WriteAllText(_filePath, contents);

        var repository = CreateLoaded();

        repository.CredentialRecords.Should().BeEmpty();
        Directory.GetFiles(_dir.Path, "credentials.xml.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public void Save_UsesCryptoProviderForEveryNonEmptyPassword()
    {
        var crypto = Substitute.For<ICryptoProvider>();
        crypto.Protect(Arg.Any<string>()).Returns(c => "X:" + c.Arg<string>().Length);
        var repository = new FileCredentialRepository(_filePath, crypto);

        repository.ReplaceAllAndSave([Sample("a"), Sample("b")]);

        crypto.Received(2).Protect("S3cr3t-P@ss");
    }
}
