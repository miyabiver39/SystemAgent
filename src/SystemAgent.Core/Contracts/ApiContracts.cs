using System.ComponentModel.DataAnnotations;
using SystemAgent.Core.CapabilityProviders;
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

public sealed record RegisterNodeRequest(
    [Required, MaxLength(253)] string HostName,
    [Required, MaxLength(45)] string IpAddress,
    [Required] OsInfo Os,
    NodeRole Role = NodeRole.Managed);

public sealed record ContainerRuntimeResponse(string Name, string Version, string TemplateId, bool SupportsPods);

public sealed record ContainerLogsResponse(string Logs);

public sealed record PullImageRequest([Required, MaxLength(512)] string Image);

public sealed record ImportImageResponse(string Output);

public sealed record NtpResponse(NtpImplementationInfo Implementation, NtpStatus Status);

public sealed record SetNtpServersRequest([Required, MinLength(1)] IReadOnlyList<string> Servers);

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
