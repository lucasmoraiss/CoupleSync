using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeAuthRepository : IAuthRepository
{
    public List<User> Users { get; } = new();

    public List<RefreshToken> RefreshTokens { get; } = new();

    public bool? RotateRefreshTokenIfMatchResultOverride { get; set; }

    public Exception? SaveChangesException { get; set; }

    public int SaveChangesCalls { get; private set; }

    public int RotateRefreshTokenIfMatchCalls { get; private set; }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return Task.FromResult(Users.Any(x => x.Email == normalized));
    }

    public Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return Task.FromResult(Users.SingleOrDefault(x => x.Email == normalized));
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Users.SingleOrDefault(x => x.Id == userId));
    }

    public Task<RefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        return Task.FromResult(RefreshTokens.SingleOrDefault(x => x.TokenHash == tokenHash));
    }

    public Task<RefreshToken?> FindRefreshTokenByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(RefreshTokens.SingleOrDefault(x => x.UserId == userId));
    }

    public Task AddUserAsync(User user, CancellationToken cancellationToken)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpsertRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken)
    {
        var existing = RefreshTokens.SingleOrDefault(x => x.UserId == refreshToken.UserId);

        if (existing is null)
        {
            RefreshTokens.Add(refreshToken);
            return Task.CompletedTask;
        }

        existing.Rotate(refreshToken.TokenHash, refreshToken.ExpiresAtUtc, refreshToken.UpdatedAtUtc);
        return Task.CompletedTask;
    }

    public Task<bool> RotateRefreshTokenIfMatchAsync(
        string currentTokenHash,
        string newTokenHash,
        DateTime newExpiresAtUtc,
        DateTime updatedAtUtc,
        DateTime now,
        CancellationToken cancellationToken)
    {
        RotateRefreshTokenIfMatchCalls++;

        if (RotateRefreshTokenIfMatchResultOverride.HasValue)
        {
            return Task.FromResult(RotateRefreshTokenIfMatchResultOverride.Value);
        }

        var existing = RefreshTokens.SingleOrDefault(x => x.TokenHash == currentTokenHash && x.ExpiresAtUtc > now);

        if (existing is null)
        {
            return Task.FromResult(false);
        }

        existing.Rotate(newTokenHash, newExpiresAtUtc, updatedAtUtc);
        return Task.FromResult(true);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (SaveChangesException is not null)
        {
            throw SaveChangesException;
        }

        SaveChangesCalls++;
        return Task.CompletedTask;
    }
}
