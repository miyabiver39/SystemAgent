using System.ComponentModel.DataAnnotations;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Core.Ha;
using SystemAgent.Core.Nodes;

namespace SystemAgent.Core.Contracts;

// WebAPIの入出力型。サーバー(SystemAgent.Web)とクライアント(SystemAgent.Client: WebUI/CLI共通)で共有する。

public sealed record HealthResponse(string Status, bool Database, DateTimeOffset TimestampUtc);

public sealed record LoginRequest([Required] string UserName, [Required, MaxLength(PasswordPolicy.MaxLength)] string Password);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string AuthSource);

public sealed record MeResponse(string UserName, string AuthSource);

/// <param name="CurrentPassword">変更するアカウントの現在のパスワード（本人確認。ログイン中の画面を他人に使われても変更させない）。</param>
public sealed record ChangePasswordRequest(
    [Required, MaxLength(PasswordPolicy.MaxLength)] string CurrentPassword,
    [Required, MinLength(PasswordPolicy.MinLength), MaxLength(PasswordPolicy.MaxLength)] string NewPassword);

public sealed record CreateUserRequest(
    [Required, RegularExpression(UserNamePolicy.Pattern)] string UserName,
    [Required, MinLength(PasswordPolicy.MinLength), MaxLength(PasswordPolicy.MaxLength)] string Password);

public sealed record ContainerRuntimeResponse(string Name, string Version, string TemplateId, bool SupportsPods);

public sealed record ContainerLogsResponse(string Logs);

public sealed record PullImageRequest([Required, MaxLength(512)] string Image);

public sealed record ImportImageResponse(string Output);

/// <summary>監査ログの検索条件。From以上・To未満。Actionは前方一致、Textは内容の部分一致。</summary>
public sealed record AuditLogQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Actor = null,
    string? Action = null,
    string? Node = null,
    string? Text = null,
    int Page = 1,
    int PageSize = 50);

public sealed record AuditLogEntry(long Id, DateTimeOffset OccurredAt, string Actor, string Action, string? Detail, string? NodeName);

public sealed record AuditLogPage(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int Page, int PageSize);

public sealed record AuditLogFacets(IReadOnlyList<string> Actions, IReadOnlyList<string> Actors, IReadOnlyList<string> Nodes);

public sealed record DeployRequest([Required, MaxLength(128)] string Tag);

/// <summary>登録済みのコンテナレジストリ（パスワードは返さない）。</summary>
/// <param name="TlsVerify">false なら証明書を検証しない（自己署名証明書・HTTPのレジストリ）。</param>
public sealed record RegistryView(string Registry, string Username, DateTimeOffset UpdatedAt, bool TlsVerify = true);

/// <param name="Password">省略時は保存済みのパスワードでログインし直す。</param>
public sealed record SaveRegistryRequest(
    [Required, MaxLength(253)] string Registry,
    [Required, MaxLength(256)] string Username,
    [MaxLength(4096)] string? Password,
    bool TlsVerify = true);

/// <param name="Image">送るローカルのイメージ（名前またはID）。</param>
/// <param name="Target">送り先（レジストリ/リポジトリ:タグ。例: zot.example.com:5000/app/web:1.0）。レジストリは登録済みであること。</param>
public sealed record PushImageRequest([Required, MaxLength(512)] string Image, [Required, MaxLength(512)] string Target);

public sealed record RegistryTagsResponse(string Repository, IReadOnlyList<string> Tags);

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
    [Required, MinLength(PasswordPolicy.MinLength), MaxLength(PasswordPolicy.MaxLength)] string Password);

public static class PasswordPolicy
{
    public const int MinLength = 12;

    /// <summary>ハッシュ計算に時間のかかる極端に長い入力を受け付けない。</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// 新しいパスワードが規則を満たすか。満たさなければ理由（利用者向け）を返す。
    /// 長さに加え、ユーザー名を含むもの・現在と同じものは推測・使い回しされやすいため拒否する。
    /// </summary>
    public static string? Validate(string userName, string newPassword, string? currentPassword = null)
    {
        if (newPassword.Length < MinLength) return $"パスワードは{MinLength}文字以上で入力してください。";
        if (newPassword.Length > MaxLength) return $"パスワードは{MaxLength}文字以内で入力してください。";
        if (userName.Length > 0 && newPassword.Contains(userName, StringComparison.OrdinalIgnoreCase))
            return "パスワードにユーザー名を含めることはできません。";
        if (currentPassword is not null && newPassword == currentPassword) return "現在と同じパスワードには変更できません。";
        return null;
    }
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
