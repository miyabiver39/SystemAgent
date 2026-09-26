namespace SystemAgent.Infrastructure.Persistence.Entities;

/// <summary>クラスタ全体の設定（CA証明書など公開情報のみ。秘密鍵はCAノードのローカル秘密情報に置く）。</summary>
public class ClusterSettingEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>ノード参加用のワンタイムトークン。値そのものは保存せずSHA-256ハッシュのみ持つ。</summary>
public class JoinTokenEntity
{
    public Guid Id { get; set; }
    public required string SecretHash { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByNodeId { get; set; }
}
