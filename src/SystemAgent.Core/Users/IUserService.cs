namespace SystemAgent.Core.Users;

/// <summary>
/// 通常JWT認証用のDB管理ユーザー（基本設計書 6章）。
/// </summary>
public interface IUserService
{
    Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <returns>同名ユーザーが存在する場合はnull。</returns>
    Task<UserSummary?> CreateAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>ユーザーを削除する。有効なユーザーが他に1人もいなくなる削除は InvalidRequestException（通常ログインできなくなるため）。</summary>
    /// <returns>ユーザーが存在しなければfalse。</returns>
    Task<bool> DeleteAsync(string userName, CancellationToken cancellationToken = default);

    Task<bool> ChangePasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default);

    Task<bool> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default);
}

public sealed record UserSummary(string UserName, bool IsActive, DateTimeOffset CreatedAt);
