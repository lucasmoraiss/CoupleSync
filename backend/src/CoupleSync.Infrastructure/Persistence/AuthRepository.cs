using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class AuthRepository : IAuthRepository
{
    private readonly AppDbContext _dbContext;

    public AuthRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return _dbContext.Users.AnyAsync(x => x.Email == normalized, cancellationToken);
    }

    public Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return _dbContext.Users.SingleOrDefaultAsync(x => x.Email == normalized, cancellationToken);
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    public Task<RefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        return _dbContext.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
    }

    public Task<RefreshToken?> FindRefreshTokenByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.RefreshTokens.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
    }

    public async Task AddUserAsync(User user, CancellationToken cancellationToken)
    {
        await _dbContext.Users.AddAsync(user, cancellationToken);
    }

    public async Task UpsertRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.RefreshTokens
            .SingleOrDefaultAsync(x => x.UserId == refreshToken.UserId, cancellationToken);

        if (existing is null)
        {
            await _dbContext.RefreshTokens.AddAsync(refreshToken, cancellationToken);
            return;
        }

        existing.Rotate(refreshToken.TokenHash, refreshToken.ExpiresAtUtc, refreshToken.UpdatedAtUtc);
    }

    public async Task<bool> RotateRefreshTokenIfMatchAsync(
        string currentTokenHash,
        string newTokenHash,
        DateTime newExpiresAtUtc,
        DateTime updatedAtUtc,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var affectedRows = await _dbContext.RefreshTokens
            .Where(x => x.TokenHash == currentTokenHash && x.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.TokenHash, newTokenHash)
                    .SetProperty(x => x.ExpiresAtUtc, newExpiresAtUtc)
                    .SetProperty(x => x.UpdatedAtUtc, updatedAtUtc),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task<bool> RevokeRefreshTokenByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var affectedRows = await _dbContext.RefreshTokens
            .Where(x => x.TokenHash == tokenHash)
            .ExecuteDeleteAsync(cancellationToken);

        return affectedRows > 0;
    }

    public Task<int> RevokeRefreshTokensByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.RefreshTokens
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<EmailCode?> FindEmailCodeAsync(Guid userId, string purpose, CancellationToken cancellationToken)
    {
        return _dbContext.EmailCodes
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId && x.Purpose == purpose, cancellationToken);
    }

    public async Task<EmailCodeReissueResult> TryReissueEmailCodeAsync(
        Guid userId,
        string purpose,
        string codeHash,
        DateTime expiresAtUtc,
        DateTime now,
        int maxPerWindow,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var windowCutoff = now - window;

        // One statement: the cap check and the increment are the same atomic UPDATE (row-locked on PostgreSQL), so
        // concurrent requests cannot all pass the check at the same count. SET expressions read the pre-update row.
        var affectedRows = await _dbContext.EmailCodes
            .Where(x => x.UserId == userId
                && x.Purpose == purpose
                && (x.IssueWindowStartedAtUtc <= windowCutoff || x.IssueCount < maxPerWindow))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.CodeHash, codeHash)
                    .SetProperty(x => x.ExpiresAtUtc, expiresAtUtc)
                    .SetProperty(x => x.CreatedAtUtc, now)
                    .SetProperty(x => x.Attempts, 0)
                    .SetProperty(x => x.IssueCount, x => x.IssueWindowStartedAtUtc <= windowCutoff ? 1 : x.IssueCount + 1)
                    .SetProperty(x => x.IssueWindowStartedAtUtc, x => x.IssueWindowStartedAtUtc <= windowCutoff ? now : x.IssueWindowStartedAtUtc),
                cancellationToken);

        if (affectedRows > 0)
        {
            return EmailCodeReissueResult.Reissued;
        }

        var exists = await _dbContext.EmailCodes.AnyAsync(x => x.UserId == userId && x.Purpose == purpose, cancellationToken);
        return exists ? EmailCodeReissueResult.LimitReached : EmailCodeReissueResult.NoCode;
    }

    public async Task AddEmailCodeAsync(EmailCode code, CancellationToken cancellationToken)
    {
        _dbContext.EmailCodes.Add(code);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(code).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<bool> TryRegisterEmailCodeAttemptAsync(Guid codeId, int maxAttempts, CancellationToken cancellationToken)
    {
        var affectedRows = await _dbContext.EmailCodes
            .Where(x => x.Id == codeId && x.Attempts < maxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);

        return affectedRows == 1;
    }

    public async Task<bool> ConsumeEmailCodeAsync(Guid codeId, string verifiedCodeHash, CancellationToken cancellationToken)
    {
        var affectedRows = await _dbContext.EmailCodes
            .Where(x => x.Id == codeId && x.CodeHash == verifiedCodeHash)
            .ExecuteDeleteAsync(cancellationToken);

        return affectedRows == 1;
    }

    public async Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            await action();
            return;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await action(); // an exception skips the commit; disposing the transaction rolls everything back
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}
