using System.ComponentModel.DataAnnotations;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Ha;
using SystemAgent.Core.Nodes;

namespace SystemAgent.Core.Contracts;

// WebAPIの入出力型。サーバー(SystemAgent.Web)とクライアント(SystemAgent.Client: WebUI/CLI共通)で共有する。

public sealed record HealthResponse(string Status, bool Database, DateTimeOffset TimestampUtc);

public sealed record LoginRequest([Required] string UserName, [Required] string Password);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string AuthSource);

public sealed record MeResponse(string UserName, string AuthSource);

public sealed record ChangePasswordRequest([Required, MinLength(PasswordPolicy.MinLength)] string NewPassword);

public sealed record CreateUserRequest(
    [Required, RegularExpression(UserNamePolicy.Pattern)] string UserName,
    [Required, MinLength(PasswordPolicy.MinLength)] string Password);

public sealed record ContainerRuntimeResponse(string Name, string Version, string TemplateId, bool SupportsPods);

public sealed record ContainerLogsResponse(string Logs);

public sealed record PullImageRequest([Required, MaxLength(512)] string Image);

public sealed record ImportImageResponse(string Output);

public sealed record DeployRequest([Required, MaxLength(128)] string Tag);

/// <summary>登録済みのコンテナレジストリ（パスワードは返さない）。</summary>
public sealed record RegistryView(string Registry, string Username, DateTimeOffset UpdatedAt);

/// <param name="Password">省略時は保存済みのパスワードでログインし直す。</param>
public sealed record SaveRegistryRequest(
    [Required, MaxLength(253)] string Registry,
    [Required, MaxLength(256)] string Username,
    [MaxLength(4096)] string? Password);

public sealed record NtpResponse(NtpImplementationInfo Implementation, NtpStatus Status);

public sealed record SetNtpServersRequest([Required, MinLength(1)] IReadOnlyList<string> Servers);

/// <param name="Joined">クラスタに参加済み（またはCAとして初期化済み）か。</param>
public sealed record ClusterStatusResponse(
    bool Joined, string? ClusterName, Guid? NodeId, string NodeName, bool IsCa, string? CaFingerprint,
    DateTimeOffset? CertificateNotAfter, string AdvertiseAddress, int ClusterPort);

public sealed record InitializeClusterRequest([Required, RegularExpression("^[A-Za-z0-9._-]{1,64}$")] string ClusterName);

public sealed record CreateJoinTokenRequest([Range(5, 1440)] int ValidMinutes = 60);

public sealed record JoinTokenResponse(string Token, DateTimeOffset ExpiresAt, string CaUrl);

public sealed record JoinClusterRequest([Required] string Token);

/// <summary>新規ノード → CAノード（mTLSポート、CAの指紋で検証済みのTLS上）。</summary>
public sealed record EnrollRequest(
    [Required] string Secret, [Required, MaxLength(253)] string NodeName, [Required, MaxLength(253)] string Address,
    [Range(1, 65535)] int ClusterPort, [Required] OsInfo Os, [Required] string CsrPem);

public sealed record EnrollResponse(Guid NodeId, string ClusterName, string NodeCertificatePem, string CaCertificatePem);

/// <param name="Source">None / Secret（暗号化して保存済み）/ Configuration（設定ファイル）</param>
public sealed record DatabaseSettingsResponse(
    string Source, string? Server, int? Port, string? Database, string? User, bool Reachable, string? Error,
    IReadOnlyList<string> PendingMigrations);

public sealed record SetDatabaseRequest(
    [Required, MaxLength(253)] string Server,
    [Range(1, 65535)] int Port,
    [Required, MaxLength(64)] string Database,
    [Required, MaxLength(80)] string User,
    [Required] string Password);

public sealed record BackupFileInfo(string Name, long SizeBytes, DateTimeOffset CreatedAt);

/// <param name="RateLimitKBps">帯域制御（KB/秒、0は無制限）。</param>
/// <param name="DailyAt">定時バックアップの時刻（HH:mm、このノードのローカル時刻）。未設定なら定時実行しない。</param>
public sealed record BackupSettingsResponse(string Directory, int Retention, int RateLimitKBps, string? DailyAt, string? Tool);

public sealed record BackupListResponse(BackupSettingsResponse Settings, IReadOnlyList<BackupFileInfo> Files);

/// <param name="Confirm">誤操作防止のため、復元するファイル名をもう一度指定する。</param>
public sealed record RestoreBackupRequest([Required] string Confirm);

/// <param name="Operable">このサービスを操作（起動・停止等）できるか（管理対象か）。</param>
public sealed record ManagedServiceResponse(ServiceStatus Status, bool Operable);

public sealed record ServiceLogsResponse(string Logs);

/// <summary>HA設定（パスワード類は返さず、設定済みかどうかだけ返す）。</summary>
public sealed record HaSettingsView(
    bool Enabled, string Interface, string VirtualIp, int VirtualRouterId, int Priority, bool HasAuthPass,
    IReadOnlyList<string> UnicastPeers, string? UnicastSourceIp, HaReturnMode ReturnMode,
    string? LocalDbTarget, string? ReplicationUser, bool HasReplicationPassword, string ReplicationSourceHost, int ReplicationSourcePort);

public sealed record HaStatusResponse(
    HaSettingsView? Settings, VrrpState State, DateTimeOffset? Since, bool RejoinPending, DbRoleStatus? Db, IReadOnlyList<HaEvent> History);

/// <param name="AuthPass">null なら既存の値を維持、空文字なら認証なし。</param>
/// <param name="LocalDbPassword">空なら既存のDB管理用接続を維持する。</param>
/// <param name="ReplicationPassword">空なら既存の値を維持する。</param>
/// <param name="Apply">保存後に keepalived.conf を生成して適用する。</param>
public sealed record SetHaSettingsRequest(
    bool Enabled,
    [Required] string Interface,
    [Required] string VirtualIp,
    [Range(1, 255)] int VirtualRouterId,
    [Range(1, 254)] int Priority,
    string? AuthPass,
    IReadOnlyList<string>? UnicastPeers,
    string? UnicastSourceIp,
    HaReturnMode ReturnMode,
    string? LocalDbHost,
    int LocalDbPort,
    string? LocalDbUser,
    string? LocalDbPassword,
    string? ReplicationUser,
    string? ReplicationPassword,
    string? ReplicationSourceHost,
    int ReplicationSourcePort,
    bool Apply);

public sealed record HaNotifyRequest(VrrpState State);

public sealed record SetupStatusResponse(bool Required);

public sealed record SetupRequest(
    [Required] string SetupToken,
    [Required, RegularExpression(UserNamePolicy.Pattern)] string UserName,
    [Required, MinLength(PasswordPolicy.MinLength)] string Password);

public static class PasswordPolicy
{
    public const int MinLength = 12;
}

public static class UserNamePolicy
{
    public const string Pattern = "^[a-zA-Z0-9._-]{1,64}$";
}

/// <summary>JWTのauth_sourceクレーム値。通常認証(DB)と緊急認証(ローカル)を区別する。</summary>
public static class AuthSources
{
    public const string Database = "db";
    public const string Emergency = "local";
}
