using System.Text.Json;
using SystemAgent.Core.Ha;
using SystemAgent.Core.Security;

namespace SystemAgent.Infrastructure.Ha;

/// <summary>
/// このノードのHA設定（ADR-022）。VRRPの認証パスワードやDB管理者・レプリケーションの資格情報を含むため、
/// ローカル秘密情報に暗号化して保存する。
/// </summary>
/// <param name="VirtualIp">VIP（CIDR形式、例: 10.0.0.100/24）。</param>
/// <param name="AuthPass">VRRPの認証パスワード（keepalivedの制約で最大8文字）。空なら認証なし。</param>
/// <param name="UnicastPeers">ユニキャストで通信する相手のIP。空ならマルチキャスト。</param>
/// <param name="LocalDb">このノード自身のMariaDBに管理者権限で接続する接続文字列（昇格・降格に使う）。</param>
/// <param name="ReplicationSourcePort">再参加時の接続先ポート（通常は3306。接続先ホストはVIP）。</param>
public sealed record HaSettings(
    bool Enabled,
    string Interface,
    string VirtualIp,
    int VirtualRouterId,
    int Priority,
    string? AuthPass,
    IReadOnlyList<string> UnicastPeers,
    string? UnicastSourceIp,
    HaReturnMode ReturnMode,
    string? LocalDb,
    string? ReplicationUser,
    string? ReplicationPassword,
    int ReplicationSourcePort = 3306,
    string? ReplicationSourceHost = null)
{
    /// <summary>再参加時の接続先。未指定ならVIP（常にマスターを指す）。</summary>
    public string SourceHost => ReplicationSourceHost is { Length: > 0 } host ? host : VirtualIp.Split('/')[0];
}

public sealed class HaSettingsStore(ILocalSecretStore secrets)
{
    private const string SecretName = "ha.settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HaSettings? Load() =>
        secrets.GetSecret(SecretName) is { } json ? JsonSerializer.Deserialize<HaSettings>(json, Json) : null;

    public void Save(HaSettings settings) => secrets.SetSecret(SecretName, JsonSerializer.Serialize(settings, Json));
}
