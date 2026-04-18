using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Auth;

public sealed class RegisterCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenEmailAlreadyExists_ShouldThrowConflict()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository();
        repository.Users.Add(User.Create(EmailAddress.From("existing@example.com"), "Existing", "hash", now));

        var handler = new RegisterCommandHandler(
            repository,
            new BCryptPasswordHasher(),
            new StubJwtTokenService(),
            new Sha256TokenHasher(),
            new FixedDateTimeProvider(now),
            Options.Create(new JwtOptions
            {
                Secret = "this-is-a-secure-test-secret-with-32chars",
                Issuer = "CoupleSync.Test",
                Audience = "CoupleSync.Mobile.Test",
                AccessTokenTtlMinutes = 15,
                RefreshTokenTtlDays = 7
            }));

        var command = new RegisterCommand("existing@example.com", "Another", "SecurePass123");

        var exception = await Assert.ThrowsAsync<ConflictException>(() => handler.HandleAsync(command, CancellationToken.None));

        Assert.Equal("EMAIL_ALREADY_IN_USE", exception.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenDatabaseReportsUniqueConstraintViolation_ShouldThrowConflict()
    {
        var now = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var repository = new FakeAuthRepository
        {
            SaveChangesException = new DbUpdateException("duplicate key value violates unique constraint 23505")
        };

        var handler = new RegisterCommandHandler(
            repository,
            new BCryptPasswordHasher(),
            new StubJwtTokenService(),
            new Sha256TokenHasher(),
            new FixedDateTimeProvider(now),
            Options.Create(new JwtOptions
            {
                Secret = "this-is-a-secure-test-secret-with-32chars",
                Issuer = "CoupleSync.Test",
                Audience = "CoupleSync.Mobile.Test",
                AccessTokenTtlMinutes = 15,
                RefreshTokenTtlDays = 7
            }));

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            handler.HandleAsync(new RegisterCommand("new-user@example.com", "New User", "SecurePass123"), CancellationToken.None));

        Assert.Equal("EMAIL_ALREADY_IN_USE", exception.Code);
        Assert.Equal("Email already in use.", exception.Message);
    }
}
