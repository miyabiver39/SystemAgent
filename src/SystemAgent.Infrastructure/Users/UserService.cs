using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SystemAgent.Core.Errors;
using SystemAgent.Core.Users;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Users;

public sealed class UserService(AppDbContext db, TimeProvider time) : IUserService
{
    private static readonly PasswordHasher<UserEntity> Hasher = new();
    // ユーザーの有無をレスポンス時間から推測されないよう、該当ユーザーが無い場合もこのハッシュで同じ検証処理を行う
    private static readonly UserEntity DummyUser = new() { UserName = "", PasswordHash = "" };
    private static readonly string DummyHash = Hasher.HashPassword(DummyUser, RandomNumberGenerator.GetHexString(32));

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking()
            .OrderBy(u => u.UserName)
            .Select(u => new UserSummary(u.UserName, u.IsActive, u.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<UserSummary?> CreateAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(u => u.UserName == userName, cancellationToken)) return null;

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            PasswordHash = "",
            CreatedAt = time.GetUtcNow(),
        };
        user.PasswordHash = Hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return new UserSummary(user.UserName, user.IsActive, user.CreatedAt);
    }

    public async Task<bool> DeleteAsync(string userName, CancellationToken cancellationToken = default)
    {
        // 別の操作者が同時に別のユーザーを削除しても最後の1人が消えないよう、有効なユーザーの行をロックしてから数える
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var active = await db.Database
            .SqlQueryRaw<string>("SELECT UserName AS Value FROM Users WHERE IsActive = 1 FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (!await db.Users.AnyAsync(u => u.UserName == userName, cancellationToken)) return false;
        if (!active.Any(name => name != userName))
            throw new InvalidRequestException("最後のユーザーは削除できません（通常ログインできる管理者がいなくなります）。先に別のユーザーを作成してください。");

        await db.Users.Where(u => u.UserName == userName).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ChangePasswordAsync(string userName, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        if (user is null) return false;

        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.UserName == userName && u.IsActive, cancellationToken);
        var result = Hasher.VerifyHashedPassword(user ?? DummyUser, user?.PasswordHash ?? DummyHash, password);
        if (user is null) return false;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await ChangePasswordAsync(userName, password, cancellationToken);
        }
        return result != PasswordVerificationResult.Failed;
    }
}
