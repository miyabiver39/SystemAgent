using Microsoft.Extensions.Logging.Abstractions;
using SystemAgent.Infrastructure.Security;

namespace SystemAgent.Core.Tests;

public sealed class LocalSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-secret-tests-" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LocalSecretStore Open() => new(_dir, NullLogger<LocalSecretStore>.Instance);

    private string InitialPassword() =>
        File.ReadAllText(Path.Combine(_dir, LocalSecretStore.InitialPasswordFileName)).Trim();

    [Fact]
    public void FirstOpen_CreatesAdminWithInitialPasswordFile()
    {
        var store = Open();

        Assert.True(store.VerifyEmergencyUser(LocalSecretStore.InitialEmergencyUserName, InitialPassword()));
        Assert.False(store.VerifyEmergencyUser(LocalSecretStore.InitialEmergencyUserName, "wrong"));
        Assert.False(store.VerifyEmergencyUser("nobody", InitialPassword()));
        Assert.Equal(64, store.GetJwtSigningKey().Length);
    }

    [Fact]
    public void SecretsFile_DoesNotContainPlaintext()
    {
        Open();

        var blob = File.ReadAllText(Path.Combine(_dir, LocalSecretStore.SecretsFileName));
        Assert.DoesNotContain(LocalSecretStore.InitialEmergencyUserName, blob);
    }

    [Fact]
    public void Reopen_KeepsSigningKeyAndChangedPassword()
    {
        var first = Open();
        var initial = InitialPassword();
        Assert.True(first.ChangeEmergencyPassword(LocalSecretStore.InitialEmergencyUserName, "changed-password-1"));
        Assert.False(File.Exists(Path.Combine(_dir, LocalSecretStore.InitialPasswordFileName)));

        var second = Open();

        Assert.Equal(first.GetJwtSigningKey(), second.GetJwtSigningKey());
        Assert.True(second.VerifyEmergencyUser(LocalSecretStore.InitialEmergencyUserName, "changed-password-1"));
        Assert.False(second.VerifyEmergencyUser(LocalSecretStore.InitialEmergencyUserName, initial));
    }

    [Fact]
    public void ChangePassword_UnknownUser_ReturnsFalse()
    {
        Assert.False(Open().ChangeEmergencyPassword("nobody", "whatever-password"));
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
