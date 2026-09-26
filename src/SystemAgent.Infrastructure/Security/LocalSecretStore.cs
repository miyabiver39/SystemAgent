using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Security;

namespace SystemAgent.Infrastructure.Security;

/// <summary>
/// ホスト固有のmaster.keyでAES-GCM暗号化したsecrets.encを扱う（ADR-003）。
/// ディレクトリ・ファイルはいずれも実行ユーザーのみ読み書き可能（Linuxでは700/600）で作成する。
/// 緊急認証ユーザーが未作成の間はワンタイムのセットアップトークンをsetup-tokenファイルに置く（ADR-015）。
/// </summary>
public sealed class LocalSecretStore : ILocalSecretStore
{
    public const string MasterKeyFileName = "master.key";
    public const string SecretsFileName = "secrets.enc";
    public const string SetupTokenFileName = "setup-token";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly PasswordHasher<object> Hasher = new();
    // ユーザー有無をレスポンス時間から推測されないよう、該当ユーザーが無い場合もこのハッシュで検証処理を行う
    private static readonly string DummyHash = Hasher.HashPassword(null!, RandomNumberGenerator.GetHexString(32));
    private static readonly UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory;
    private readonly byte[] _masterKey;
    private readonly ILogger<LocalSecretStore> _logger;
    private readonly Lock _lock = new();
    private SecretData _data;

    public LocalSecretStore(string directory, ILogger<LocalSecretStore> logger)
    {
        _directory = directory;
        _logger = logger;
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
            _data = new SecretData(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)), []);
            Save();
        }

        if (IsSetupRequired) EnsureSetupToken();
    }

    public bool IsSetupRequired => _data.EmergencyUsers.Count == 0;

    public string SetupTokenPath => PathOf(SetupTokenFileName);

    public SetupResult CompleteSetup(string setupToken, string userName, string password)
    {
        lock (_lock)
        {
            if (!IsSetupRequired) return SetupResult.AlreadyCompleted;

            var expected = File.Exists(SetupTokenPath) ? File.ReadAllText(SetupTokenPath).Trim() : "";
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(setupToken.Trim())))
            {
                return SetupResult.InvalidToken;
            }

            _data = _data with { EmergencyUsers = [new EmergencyUser(userName, Hasher.HashPassword(null!, password))] };
            Save();
            File.Delete(SetupTokenPath);
            _logger.LogInformation("初期セットアップが完了しました。緊急認証ユーザー '{User}' を作成しました。", userName);
            return SetupResult.Completed;
        }
    }

    public byte[] GetJwtSigningKey() => Convert.FromBase64String(_data.JwtSigningKey);

    public bool VerifyEmergencyUser(string userName, string password)
    {
        var user = _data.EmergencyUsers.FirstOrDefault(u => u.UserName == userName);
        var result = Hasher.VerifyHashedPassword(null!, user?.PasswordHash ?? DummyHash, password);
        return user is not null && result != PasswordVerificationResult.Failed;
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
            return true;
        }
    }

    public string? GetSecret(string name) => _data.Items?.GetValueOrDefault(name);

    public void SetSecret(string name, string? value)
    {
        lock (_lock)
        {
            var items = new Dictionary<string, string>(_data.Items ?? []);
            if (value is null) items.Remove(name);
            else items[name] = value;
            _data = _data with { Items = items };
            Save();
        }
    }

    private string PathOf(string fileName) => Path.Combine(_directory, fileName);

    private void EnsureSetupToken()
    {
        if (!File.Exists(SetupTokenPath))
        {
            WriteFile(SetupTokenPath, Encoding.UTF8.GetBytes(RandomNumberGenerator.GetHexString(32, lowercase: true) + "\n"));
        }
        _logger.LogWarning(
            "初期セットアップが未実施です。`sudo systemagent setup` を実行するか、WebUIの初期セットアップ画面で {Path} に記載のセットアップトークンを入力してください。",
            SetupTokenPath);
    }

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

    /// <param name="Items">名前付きの秘密情報。以前の形式のファイルには無いためnull許容。</param>
    private sealed record SecretData(string JwtSigningKey, List<EmergencyUser> EmergencyUsers, Dictionary<string, string>? Items = null);

    private sealed record EmergencyUser(string UserName, string PasswordHash);
}
