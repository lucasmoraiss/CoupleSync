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

    public Task<bool> RevokeRefreshTokenByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        return Task.FromResult(RefreshTokens.RemoveAll(x => x.TokenHash == tokenHash) > 0);
    }

    public List<EmailCode> EmailCodes { get; } = new();

    public Exception? RevokeRefreshTokensFailure { get; set; }

    /// <summary>How many of the next code stores fail like a lost insert race (unique index on user + purpose).</summary>
    public int FailNextCodeStores { get; set; }

    /// <summary>A store failure that is NOT an insert race (connection lost, timeout...).</summary>
    public Exception? CodeStoreFailure { get; set; }

    public async Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var users = Users.Select(u => (User: u, u.PasswordHash, u.EmailVerified)).ToList();
        var codes = EmailCodes.ToList();
        var tokens = RefreshTokens.ToList();
        try
        {
            await action();
        }
        catch
        {
            foreach (var (user, hash, verified) in users)
            {
                user.ChangePasswordHash(hash);
                typeof(User).GetProperty(nameof(User.EmailVerified))!.SetValue(user, verified);
            }

            EmailCodes.Clear();
            EmailCodes.AddRange(codes);
            RefreshTokens.Clear();
            RefreshTokens.AddRange(tokens);
            throw;
        }
    }

    public Task<int> RevokeRefreshTokensByUserIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (RevokeRefreshTokensFailure is not null)
        {
            throw RevokeRefreshTokensFailure;
        }

        return Task.FromResult(RefreshTokens.RemoveAll(x => x.UserId == userId));
    }

    public Task<EmailCode?> FindEmailCodeAsync(Guid userId, string purpose, CancellationToken cancellationToken)
    {
        return Task.FromResult(EmailCodes.SingleOrDefault(x => x.UserId == userId && x.Purpose == purpose));
    }

    public Task<EmailCodeReissueResult> TryReissueEmailCodeAsync(
        Guid userId, string purpose, string codeHash, DateTime expiresAtUtc, DateTime now, int maxPerWindow, TimeSpan window, CancellationToken cancellationToken)
    {
        var existing = EmailCodes.SingleOrDefault(x => x.UserId == userId && x.Purpose == purpose);
        if (existing is null)
        {
            return Task.FromResult(EmailCodeReissueResult.NoCode);
        }

        if (existing.HasReachedIssueLimit(now, maxPerWindow, window))
        {
            return Task.FromResult(EmailCodeReissueResult.LimitReached);
        }

        existing.Reissue(codeHash, expiresAtUtc, now, window);
        return Task.FromResult(EmailCodeReissueResult.Reissued);
    }

    public Task AddEmailCodeAsync(EmailCode code, CancellationToken cancellationToken)
    {
        if (CodeStoreFailure is not null)
        {
            throw CodeStoreFailure;
        }

        if (FailNextCodeStores > 0)
        {
            FailNextCodeStores--;
            throw new CoupleSync.Application.Common.Exceptions.UniqueViolationException("duplicate key value violates unique constraint 23505");
        }

        EmailCodes.Add(code);
        return Task.CompletedTask;
    }

    public Task<bool> TryRegisterEmailCodeAttemptAsync(Guid codeId, int maxAttempts, CancellationToken cancellationToken)
    {
        var code = EmailCodes.SingleOrDefault(x => x.Id == codeId);
        if (code is null || code.Attempts >= maxAttempts)
        {
            return Task.FromResult(false);
        }

        code.RegisterAttempt();
        return Task.FromResult(true);
    }

    public Task<bool> ConsumeEmailCodeAsync(Guid codeId, string verifiedCodeHash, CancellationToken cancellationToken)
    {
        return Task.FromResult(EmailCodes.RemoveAll(x => x.Id == codeId && x.CodeHash == verifiedCodeHash) > 0);
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
