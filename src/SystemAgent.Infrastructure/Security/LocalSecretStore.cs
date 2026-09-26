using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Security;

namespace SystemAgent.Infrastructure.Security;

/// <summary>
/// ホスト固有のmaster.keyでAES-GCM暗号化したsecrets.encを扱う（ADR-003）。
/// ディレクトリ・ファイルはいずれも実行ユーザーのみ読み書き可能（Linuxでは700/600）で作成する。
/// </summary>
public sealed class LocalSecretStore : ILocalSecretStore
{
    public const string MasterKeyFileName = "master.key";
    public const string SecretsFileName = "secrets.enc";
    public const string InitialPasswordFileName = "initial-admin-password";
    public const string InitialEmergencyUserName = "admin";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory;
    private readonly byte[] _masterKey;
    private readonly Lock _lock = new();
    private SecretData _data;

    public LocalSecretStore(string directory, ILogger<LocalSecretStore> logger)
    {
        _directory = directory;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _masterKey = LoadOrCreateMasterKey();

        if (File.Exists(PathOf(SecretsFileName)))
        {
            _data = Load();
        }
        else
        {
            var initialPassword = RandomNumberGenerator.GetString(PasswordAlphabet, 20);
            _data = new SecretData(
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
                [new EmergencyUser(InitialEmergencyUserName, Hasher.HashPassword(null!, initialPassword))]);
            Save();
            WriteFile(PathOf(InitialPasswordFileName), System.Text.Encoding.UTF8.GetBytes(initialPassword + "\n"));
            logger.LogWarning(
                "ローカル秘密情報を初期化しました。緊急ユーザー '{User}' の初期パスワードは {Path} を参照し、ログイン後に変更してください。",
                InitialEmergencyUserName, PathOf(InitialPasswordFileName));
        }
    }

    public byte[] GetJwtSigningKey() => Convert.FromBase64String(_data.JwtSigningKey);

    public bool VerifyEmergencyUser(string userName, string password)
    {
        var user = _data.EmergencyUsers.FirstOrDefault(u => u.UserName == userName);
        if (user is null)
        {
            // ユーザー有無をレスポンス時間から推測されないよう、存在しない場合もハッシュ計算を行う
            Hasher.VerifyHashedPassword(null!, _data.EmergencyUsers[0].PasswordHash, password);
            return false;
        }
        return Hasher.VerifyHashedPassword(null!, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }

    public bool ChangeEmergencyPassword(string userName, string newPassword)
    {
        lock (_lock)
        {
            var index = _data.EmergencyUsers.FindIndex(u => u.UserName == userName);
            if (index < 0) return false;

            var users = _data.EmergencyUsers.ToList();
            users[index] = users[index] with { PasswordHash = Hasher.HashPassword(null!, newPassword) };
            _data = _data with { EmergencyUsers = users };
            Save();
            File.Delete(PathOf(InitialPasswordFileName));
            return true;
        }
    }

    private string PathOf(string fileName) => Path.Combine(_directory, fileName);

    private byte[] LoadOrCreateMasterKey()
    {
        var path = PathOf(MasterKeyFileName);
        if (File.Exists(path)) return File.ReadAllBytes(path);

        var key = RandomNumberGenerator.GetBytes(32);
        WriteFile(path, key);
        return key;
    }

    private SecretData Load()
    {
        var blob = File.ReadAllBytes(PathOf(SecretsFileName));
        var plain = new byte[blob.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_masterKey, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException(
                $"{SecretsFileName} の復号に失敗しました。{MasterKeyFileName} が別ホストのものか、ファイルが改ざんされています。", ex);
        }
        return JsonSerializer.Deserialize<SecretData>(plain)
            ?? throw new InvalidOperationException($"{SecretsFileName} の内容が不正です。");
    }

    private void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(_data);
        var blob = new byte[NonceSize + TagSize + plain.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_masterKey, TagSize))
        {
            aes.Encrypt(nonce, plain, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize));
        }
        WriteFile(PathOf(SecretsFileName), blob);
    }

    private static void WriteFile(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerReadWrite;
        using (var stream = new FileStream(tmp, options))
        {
            stream.Write(content);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private sealed record SecretData(string JwtSigningKey, List<EmergencyUser> EmergencyUsers);

    private sealed record EmergencyUser(string UserName, string PasswordHash);
}
