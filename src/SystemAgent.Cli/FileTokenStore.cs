using System.Text.Json;
using SystemAgent.Client;
using SystemAgent.Core.Contracts;

namespace SystemAgent.Cli;

/// <summary>
/// CLIのログイン状態。実行ユーザーのみ読み書き可能なファイル（~/.config/systemagent/session.json）に保存する。
/// </summary>
public sealed class FileTokenStore : ITokenProvider
{
    // DoNotVerify: 既定では ~/.config が未作成だと空文字が返り、カレントディレクトリに保存されてしまう
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "systemagent", "session.json");

    public Session? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath), ApiClient.Json);
        return session is not null && session.Token.ExpiresAt > DateTimeOffset.UtcNow ? session : null;
    }

    public void Save(Session session)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(FilePath, options);
        JsonSerializer.Serialize(stream, session, ApiClient.Json);
    }

    public void Clear() => File.Delete(FilePath);

    ValueTask<string?> ITokenProvider.GetAccessTokenAsync() => ValueTask.FromResult(Load()?.Token.AccessToken);

    ValueTask ITokenProvider.OnUnauthorizedAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }
}

/// <param name="Url">ログインした接続先。以降のコマンドは--url省略時にこれを使う。</param>
public sealed record Session(string Url, string UserName, TokenResponse Token);
