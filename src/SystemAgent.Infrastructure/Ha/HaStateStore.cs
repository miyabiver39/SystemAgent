using System.Security.Cryptography;
using System.Text.Json;
using SystemAgent.Core.Ha;

namespace SystemAgent.Infrastructure.Ha;

/// <param name="RejoinPending">手動復帰モードで、レプリカとしての（再）参加が管理者の承認待ちか。</param>
public sealed record HaState(VrrpState State, DateTimeOffset? Since, bool RejoinPending, IReadOnlyList<HaEvent> History);

/// <summary>
/// VRRPの状態と履歴。DB停止中でも記録できるよう、ローカルファイル（/var/lib/systemagent/ha-state.json）に置く。
/// フック用トークンも同じディレクトリの root 専用ファイルに置く（keepalived の notify から呼ばれる CLI が読む）。
/// </summary>
public sealed class HaStateStore(string directory)
{
    public const string HookTokenFileName = "ha-hook-token";
    private const int MaxHistory = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _lock = new();

    private string StatePath => Path.Combine(directory, "ha-state.json");

    public string HookTokenPath => Path.Combine(directory, HookTokenFileName);

    public HaState Load()
    {
        lock (_lock)
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<HaState>(File.ReadAllText(StatePath), Json)!
                : new HaState(VrrpState.Unknown, null, false, []);
        }
    }

    public HaState Update(Func<HaState, HaState> change)
    {
        lock (_lock)
        {
            var next = change(Load());
            if (next.History.Count > MaxHistory) next = next with { History = next.History.TakeLast(MaxHistory).ToList() };
            Write(StatePath, JsonSerializer.Serialize(next, Json));
            return next;
        }
    }

    /// <summary>フック用トークン。無ければ作る。</summary>
    public string EnsureHookToken()
    {
        lock (_lock)
        {
            if (File.Exists(HookTokenPath)) return File.ReadAllText(HookTokenPath).Trim();
            var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
            Write(HookTokenPath, token + "\n");
            return token;
        }
    }

    public bool VerifyHookToken(string? token) =>
        token is { Length: > 0 } && File.Exists(HookTokenPath)
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(HookTokenPath).Trim()),
            System.Text.Encoding.UTF8.GetBytes(token.Trim()));

    private void Write(string path, string content)
    {
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(tmp, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
