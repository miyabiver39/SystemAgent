using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Core.Security;
using SystemAgent.Infrastructure.Security;

namespace SystemAgent.Core.Tests;

public sealed class LocalSecretStoreTests : IDisposable
{
    private const string Password = "initial-password-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-secret-tests-" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LocalSecretStore Open() => new(_dir, NullLogger<LocalSecretStore>.Instance);

    private string SetupToken() => File.ReadAllText(Path.Combine(_dir, LocalSecretStore.SetupTokenFileName)).Trim();

    private LocalSecretStore OpenAndSetUp()
    {
        var store = Open();
        Assert.Equal(SetupResult.Completed, store.CompleteSetup(SetupToken(), "admin", Password));
        return store;
    }

    [Fact]
    public void FirstOpen_RequiresSetupAndIssuesToken()
    {
        var store = Open();

        Assert.True(store.IsSetupRequired);
        Assert.Equal(32, SetupToken().Length);
        Assert.False(store.VerifyEmergencyUser("admin", ""));
        Assert.Equal(64, store.GetJwtSigningKey().Length);
    }

    [Fact]
    public void Setup_WithWrongToken_IsRejected()
    {
        var store = Open();

        Assert.Equal(SetupResult.InvalidToken, store.CompleteSetup("wrong", "admin", Password));
        Assert.Equal(SetupResult.InvalidToken, store.CompleteSetup("", "admin", Password));
        Assert.True(store.IsSetupRequired);
    }

    [Fact]
    public void Setup_CreatesUser_ConsumesToken_AndCannotRunTwice()
    {
        var store = Open();
        var token = SetupToken();

        Assert.Equal(SetupResult.Completed, store.CompleteSetup(token, "admin", Password));

        Assert.False(store.IsSetupRequired);
        Assert.False(File.Exists(Path.Combine(_dir, LocalSecretStore.SetupTokenFileName)));
        Assert.True(store.VerifyEmergencyUser("admin", Password));
        Assert.False(store.VerifyEmergencyUser("admin", "wrong"));
        Assert.False(store.VerifyEmergencyUser("nobody", Password));
        Assert.Equal(SetupResult.AlreadyCompleted, store.CompleteSetup(token, "other", Password));
    }

    [Fact]
    public void Reopen_BeforeSetup_KeepsSameToken()
    {
        Open();
        var token = SetupToken();

        Assert.True(Open().IsSetupRequired);
        Assert.Equal(token, SetupToken());
    }

    [Fact]
    public void SecretsFile_DoesNotContainPlaintext()
    {
        OpenAndSetUp();

        var blob = File.ReadAllText(Path.Combine(_dir, LocalSecretStore.SecretsFileName));
        Assert.DoesNotContain("admin", blob);
    }

    [Fact]
    public void Reopen_KeepsSigningKeyAndChangedPassword()
    {
        var first = OpenAndSetUp();
        Assert.True(first.ChangeEmergencyPassword("admin", "changed-password-1"));

        var second = Open();

        Assert.False(second.IsSetupRequired);
        Assert.Equal(first.GetJwtSigningKey(), second.GetJwtSigningKey());
        Assert.True(second.VerifyEmergencyUser("admin", "changed-password-1"));
        Assert.False(second.VerifyEmergencyUser("admin", Password));
    }

    [Fact]
    public void NamedSecrets_PersistEncrypted_AndCanBeDeleted()
    {
        Open().SetSecret("pki.node.key", "-----BEGIN PRIVATE KEY-----secret-----END PRIVATE KEY-----");

        var reopened = Open();
        Assert.Equal("-----BEGIN PRIVATE KEY-----secret-----END PRIVATE KEY-----", reopened.GetSecret("pki.node.key"));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(_dir, LocalSecretStore.SecretsFileName)));

        reopened.SetSecret("pki.node.key", null);
        Assert.Null(Open().GetSecret("pki.node.key"));
    }

    [Fact]
    public void ChangePassword_UnknownUser_ReturnsFalse()
    {
        Assert.False(OpenAndSetUp().ChangeEmergencyPassword("nobody", "whatever-password"));
    }

    [Fact]
    public void TamperedSecretsFile_FailsToOpen()
    {
        Open();
        var path = Path.Combine(_dir, LocalSecretStore.SecretsFileName);
        var blob = File.ReadAllBytes(path);
        blob[^1] ^= 0xFF;
        File.WriteAllBytes(path, blob);

        Assert.Throws<InvalidOperationException>(Open);
    }

    [Fact]
    public void MasterKeyFromAnotherHost_FailsToOpen()
    {
        Open();
        File.WriteAllBytes(Path.Combine(_dir, LocalSecretStore.MasterKeyFileName), new byte[32]);

        Assert.Throws<InvalidOperationException>(Open);
    }
}
