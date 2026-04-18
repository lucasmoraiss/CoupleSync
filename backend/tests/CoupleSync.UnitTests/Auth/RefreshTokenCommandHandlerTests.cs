using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Auth;

public sealed class RefreshTokenCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenRefreshTokenIsValidAndNotNearExpiry_ShouldRotateTokenAndPersist()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository();
        var tokenHasher = new Sha256TokenHasher();
        var jwtTokenService = new StubJwtTokenService { Token = "access-token-rotated" };
        var rawRefreshToken = "raw-refresh-token-to-rotate";

        var user = User.Create(EmailAddress.From("user@example.com"), "User", "hash", now.AddDays(-2));
        repository.Users.Add(user);
        repository.RefreshTokens.Add(
            RefreshToken.CreateForUser(
                user.Id,
                tokenHasher.Hash(rawRefreshToken),
                now.AddDays(2),
                now.AddDays(-5)));

        var handler = CreateHandler(repository, tokenHasher, jwtTokenService, now);

        var result = await handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None);

        Assert.Equal("access-token-rotated", result.AccessToken);
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.Equal(1, repository.RotateRefreshTokenIfMatchCalls);
        Assert.Equal(0, repository.SaveChangesCalls);

        var rotatedToken = repository.RefreshTokens.Single();
        Assert.Equal(tokenHasher.Hash(result.RefreshToken!), rotatedToken.TokenHash);
        Assert.NotEqual(tokenHasher.Hash(rawRefreshToken), rotatedToken.TokenHash);
        Assert.Equal(now.AddDays(7), rotatedToken.ExpiresAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WhenRefreshTokenIsNearExpiry_ShouldRotateTokenAndPersist()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository();
        var tokenHasher = new Sha256TokenHasher();
        var jwtTokenService = new StubJwtTokenService { Token = "access-token-rotated" };
        var rawRefreshToken = "raw-refresh-token-rotate";

        var user = User.Create(EmailAddress.From("user@example.com"), "User", "hash", now.AddDays(-2));
        repository.Users.Add(user);
        repository.RefreshTokens.Add(
            RefreshToken.CreateForUser(
                user.Id,
                tokenHasher.Hash(rawRefreshToken),
                now.AddHours(12),
                now.AddDays(-6)));

        var handler = CreateHandler(repository, tokenHasher, jwtTokenService, now);

        var result = await handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None);

        Assert.Equal("access-token-rotated", result.AccessToken);
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.Equal(1, repository.RotateRefreshTokenIfMatchCalls);
        Assert.Equal(0, repository.SaveChangesCalls);

        var rotatedToken = repository.RefreshTokens.Single();
        Assert.Equal(tokenHasher.Hash(result.RefreshToken!), rotatedToken.TokenHash);
        Assert.NotEqual(tokenHasher.Hash(rawRefreshToken), rotatedToken.TokenHash);
        Assert.Equal(now.AddDays(7), rotatedToken.ExpiresAtUtc);
    }

    [Fact]
    public async Task HandleAsync_WhenRefreshTokenIsReplayedAfterSuccessfulRefresh_ShouldThrowUnauthorized()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository();
        var tokenHasher = new Sha256TokenHasher();
        var jwtTokenService = new StubJwtTokenService { Token = "access-token" };
        var rawRefreshToken = "raw-refresh-token-replay";

        var user = User.Create(EmailAddress.From("user@example.com"), "User", "hash", now.AddDays(-2));
        repository.Users.Add(user);
        repository.RefreshTokens.Add(
            RefreshToken.CreateForUser(
                user.Id,
                tokenHasher.Hash(rawRefreshToken),
                now.AddDays(3),
                now.AddDays(-6)));

        var handler = CreateHandler(repository, tokenHasher, jwtTokenService, now);

        var firstResult = await handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(firstResult.RefreshToken));

        var exception = await Assert.ThrowsAsync<UnauthorizedException>(
            () => handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None));

        Assert.Equal("UNAUTHORIZED", exception.Code);
        Assert.Equal("Invalid or expired session.", exception.Message);
    }

    [Fact]
    public async Task HandleAsync_WhenAtomicRotationAffectsNoRows_ShouldThrowUnauthorized()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository
        {
            RotateRefreshTokenIfMatchResultOverride = false
        };
        var tokenHasher = new Sha256TokenHasher();
        var jwtTokenService = new StubJwtTokenService { Token = "access-token" };
        var rawRefreshToken = "raw-refresh-token-atomic-failure";

        var user = User.Create(EmailAddress.From("user@example.com"), "User", "hash", now.AddDays(-2));
        repository.Users.Add(user);
        repository.RefreshTokens.Add(
            RefreshToken.CreateForUser(
                user.Id,
                tokenHasher.Hash(rawRefreshToken),
                now.AddDays(3),
                now.AddDays(-6)));

        var handler = CreateHandler(repository, tokenHasher, jwtTokenService, now);

        var exception = await Assert.ThrowsAsync<UnauthorizedException>(
            () => handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None));

        Assert.Equal("UNAUTHORIZED", exception.Code);
        Assert.Equal("Invalid or expired session.", exception.Message);
        Assert.Equal(1, repository.RotateRefreshTokenIfMatchCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenRefreshTokenUserDoesNotExist_ShouldThrowUnauthorized()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository();
        var tokenHasher = new Sha256TokenHasher();
        var rawRefreshToken = "raw-refresh-token-orphan";

        repository.RefreshTokens.Add(
            RefreshToken.CreateForUser(
                Guid.NewGuid(),
                tokenHasher.Hash(rawRefreshToken),
                now.AddDays(2),
                now.AddDays(-7)));

        var handler = CreateHandler(repository, tokenHasher, new StubJwtTokenService(), now);

        var exception = await Assert.ThrowsAsync<UnauthorizedException>(
            () => handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None));

        Assert.Equal("UNAUTHORIZED", exception.Code);
        Assert.Equal("Invalid or expired session.", exception.Message);
    }

    [Fact]
    public async Task HandleAsync_WhenRefreshTokenIsExpired_ShouldThrowUnauthorized()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository();
        var tokenHasher = new Sha256TokenHasher();
        var rawRefreshToken = "raw-refresh-token";

        var user = User.Create(EmailAddress.From("user@example.com"), "User", "hash", now.AddDays(-2));
        repository.Users.Add(user);
        repository.RefreshTokens.Add(
            RefreshToken.CreateForUser(
                user.Id,
                tokenHasher.Hash(rawRefreshToken),
                now.AddMinutes(-1),
                now.AddDays(-7)));

        var handler = CreateHandler(repository, tokenHasher, new StubJwtTokenService(), now);

        var exception = await Assert.ThrowsAsync<UnauthorizedException>(
            () => handler.HandleAsync(new RefreshTokenCommand(rawRefreshToken), CancellationToken.None));

        Assert.Equal("UNAUTHORIZED", exception.Code);
        Assert.Equal("Invalid or expired session.", exception.Message);
    }

    private static RefreshTokenCommandHandler CreateHandler(
        FakeAuthRepository repository,
        Sha256TokenHasher tokenHasher,
        StubJwtTokenService jwtTokenService,
        DateTime now)
    {
        return new RefreshTokenCommandHandler(
            repository,
            jwtTokenService,
            tokenHasher,
            new FixedDateTimeProvider(now),
            Options.Create(new JwtOptions
            {
                Secret = "this-is-a-secure-test-secret-with-32chars",
                Issuer = "CoupleSync.Test",
                Audience = "CoupleSync.Mobile.Test",
                AccessTokenTtlMinutes = 15,
                RefreshTokenTtlDays = 7
            }));
    }
}
