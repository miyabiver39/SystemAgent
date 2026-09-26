namespace SystemAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// 通常JWT認証（DB管理ユーザー）用のユーザーエンティティ（基本設計書 6章）。
/// </summary>
public class UserEntity
{
    public Guid Id { get; set; }
    public required string UserName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
