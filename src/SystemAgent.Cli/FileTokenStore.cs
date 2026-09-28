using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli;

/// <summary>
/// CLIのログイン状態（アクセストークンを含む）。~/.config/systemagent/session.json（Windows は %APPDATA%\systemagent\session.json）に保存する。
/// <list type="bullet">
/// <item>Linux: 実行ユーザーのみ読み書き可能なファイル（600）にする。</item>
/// <item>Windows: 既定のアクセス権では他のユーザーから読める場合があるため、DPAPI（CurrentUser）で暗号化して保存する。
/// 同じWindowsユーザーでなければ復号できない。</item>
/// </list>
/// </summary>
/// <param name="filePath">保存先（テスト用。省略時は上記の既定の場所）。</param>
public sealed class FileTokenStore(string? filePath = null) : ITokenProvider
{
    // Windows で DPAPI により暗号化した内容の目印（以前の平文のファイルも読めるようにする）
    private const string ProtectedPrefix = "DPAPI:";
    private static readonly byte[] Entropy = "SystemAgent.Cli.Session"u8.ToArray();

    // DoNotVerify: 既定では ~/.config が未作成だと空文字が返り、カレントディレクトリに保存されてしまう
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "systemagent", "session.json");

    private readonly string _filePath = filePath ?? DefaultFilePath;

    public Session? Load()
    {
        if (!File.Exists(_filePath)) return null;
        var text = File.ReadAllText(_filePath);
        if (text.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                text = Unprotect(text[ProtectedPrefix.Length..]);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // 別のユーザー・別のPCで作られたファイル。未ログインとして扱う
                return null;
            }
        }
        var session = JsonSerializer.Deserialize<Session>(text, ApiClient.Json);
        return session is not null && session.Token.ExpiresAt > DateTimeOffset.UtcNow ? session : null;
    }

    public void Save(Session session)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var json = JsonSerializer.Serialize(session, ApiClient.Json);
        var content = OperatingSystem.IsWindows() ? ProtectedPrefix + Protect(json) : json;
        using var stream = new FileStream(_filePath, options);
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    [SupportedOSPlatform("windows")]
    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    [SupportedOSPlatform("windows")]
    private static string Unprotect(string encoded) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser));

    public void Clear() => File.Delete(_filePath);

    ValueTask<string?> ITokenProvider.GetAccessTokenAsync() => ValueTask.FromResult(Load()?.Token.AccessToken);

    ValueTask ITokenProvider.OnUnauthorizedAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }
}

/// <param name="Url">ログインした接続先。以降のコマンドは--url省略時にこれを使う。</param>
public sealed record Session(string Url, string UserName, TokenResponse Token);
