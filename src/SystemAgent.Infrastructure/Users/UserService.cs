using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SystemAgent.Core.Users;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Persistence.Entities;

namespace SystemAgent.Infrastructure.Users;

public sealed class UserService(AppDbContext db, TimeProvider time) : IUserService
{
    private static readonly PasswordHasher<UserEntity> Hasher = new();

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

    public async Task<bool> DeleteAsync(string userName, CancellationToken cancellationToken = default) =>
        await db.Users.Where(u => u.UserName == userName).ExecuteDeleteAsync(cancellationToken) > 0;

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
        if (user is null) return false;

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await ChangePasswordAsync(userName, password, cancellationToken);
        }
        return result != PasswordVerificationResult.Failed;
    }
}
