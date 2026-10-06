using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Auth;

public sealed class PasswordAndLoginTimingTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    // password rule

    [Theory]
    [InlineData("abd12345x")]
    [InlineData("Minha-senha-9")]
    [InlineData("açaí2025!")]
    public void PasswordPolicy_AcceptsAGoodPassword(string password)
    {
        Assert.Empty(PasswordPolicy.Validate(password, "ana@example.com"));
    }

    [Theory]
    [InlineData("a1", new[] { PasswordPolicy.TooShortMessage })]
    [InlineData("12345678", new[] { PasswordPolicy.NeedsLetterMessage, PasswordPolicy.TooCommonMessage })]
    [InlineData("abcdefghij", new[] { PasswordPolicy.NeedsDigitMessage })]
    [InlineData("", new[] { PasswordPolicy.TooShortMessage, PasswordPolicy.NeedsLetterMessage, PasswordPolicy.NeedsDigitMessage })]
    [InlineData("password", new[] { PasswordPolicy.NeedsDigitMessage, PasswordPolicy.TooCommonMessage })]
    [InlineData("senha123", new[] { PasswordPolicy.TooCommonMessage })]
    [InlineData("SENHA123", new[] { PasswordPolicy.TooCommonMessage })]
    public void PasswordPolicy_ListsExactlyWhatIsMissing(string password, string[] expected)
    {
        Assert.Equal(expected, PasswordPolicy.Validate(password, "ana@example.com"));
    }

    [Fact]
    public void PasswordPolicy_RejectsTheEmailItself_ButNotAShortLocalPart()
    {
        Assert.Contains(PasswordPolicy.SameAsEmailMessage, PasswordPolicy.Validate("ana.souza@example.com", "ana.souza@example.com"));
        Assert.Contains(PasswordPolicy.SameAsEmailMessage, PasswordPolicy.Validate("anasouza1", "anasouza1@example.com"));
        Assert.Empty(PasswordPolicy.Validate("ana12345", "ana12345x@example.com"));
    }

    // login timing: unknown e-mail still spends a bcrypt verification, on a hash with the real work factor

    [Fact]
    public async Task Login_UnknownEmail_VerifiesAgainstTheDummyHash()
    {
        var hasher = new SpyPasswordHasher();
        var handler = NewLoginHandler(new FakeAuthRepository(), hasher);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            handler.HandleAsync(new LoginCommand("ghost@example.com", "whatever123"), CancellationToken.None));

        Assert.Equal("INVALID_CREDENTIALS", ex.Code);
        Assert.Equal(new[] { hasher.DummyHash }, hasher.VerifiedHashes);
    }

    [Fact]
    public async Task Login_KnownEmailWrongPassword_SameErrorAsUnknownEmail()
    {
        var repository = new FakeAuthRepository();
        var real = new BCryptPasswordHasher();
        repository.Users.Add(User.Create(EmailAddress.From("ana@example.com"), "Ana", real.HashPassword("Correta123"), Now));
        var handler = NewLoginHandler(repository, real);

        var wrong = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            handler.HandleAsync(new LoginCommand("ana@example.com", "Errada123"), CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            handler.HandleAsync(new LoginCommand("ghost@example.com", "Errada123"), CancellationToken.None));

        Assert.Equal(wrong.Code, unknown.Code);
        Assert.Equal(wrong.Message, unknown.Message);
    }

    [Fact]
    public void DummyHash_IsAValidBcryptHash_WithTheSameWorkFactorAsRealHashes()
    {
        var hasher = new BCryptPasswordHasher();

        var real = hasher.HashPassword("Qualquer123");

        Assert.Equal(CostOf(real), CostOf(hasher.DummyHash));
        Assert.False(hasher.VerifyPassword("Qualquer123", hasher.DummyHash));
        Assert.Same(hasher.DummyHash, hasher.DummyHash);
    }

    private static string CostOf(string bcryptHash) => bcryptHash.Split('$')[2];

    // logout

    [Fact]
    public async Task Logout_RemovesTheMatchingRefreshToken_AndIgnoresUnknownOnes()
    {
        var repository = new FakeAuthRepository();
        var hasher = new Sha256TokenHasher();
        repository.RefreshTokens.Add(RefreshToken.CreateForUser(Guid.NewGuid(), hasher.Hash("raw-token"), Now.AddDays(7), Now));
        var handler = new LogoutCommandHandler(repository, hasher);

        await handler.HandleAsync(new LogoutCommand("unknown"), CancellationToken.None);
        Assert.Single(repository.RefreshTokens);

        await handler.HandleAsync(new LogoutCommand("raw-token"), CancellationToken.None);
        Assert.Empty(repository.RefreshTokens);
    }

    // change password

    [Fact]
    public async Task ChangePassword_ReplacesTheRefreshToken_AndTheHash()
    {
        var repository = new FakeAuthRepository();
        var hasher = new BCryptPasswordHasher();
        var tokens = new Sha256TokenHasher();
        var user = User.Create(EmailAddress.From("ana@example.com"), "Ana", hasher.HashPassword("Antiga123"), Now);
        repository.Users.Add(user);
        repository.RefreshTokens.Add(RefreshToken.CreateForUser(user.Id, tokens.Hash("old-raw"), Now.AddDays(7), Now));
        var handler = new ChangePasswordCommandHandler(repository, hasher, new StubJwtTokenService(), tokens, new FixedDateTimeProvider(Now), TestJwtOptions.Default());

        var result = await handler.HandleAsync(new ChangePasswordCommand(user.Id, "Antiga123", "Nova456789"), CancellationToken.None);

        Assert.True(hasher.VerifyPassword("Nova456789", user.PasswordHash));
        var stored = Assert.Single(repository.RefreshTokens);
        Assert.Equal(tokens.Hash(result.RefreshToken!), stored.TokenHash);
        Assert.Null(await repository.FindRefreshTokenByHashAsync(tokens.Hash("old-raw"), CancellationToken.None));
        Assert.Equal(1, repository.SaveChangesCalls);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ChangesNothing()
    {
        var repository = new FakeAuthRepository();
        var hasher = new BCryptPasswordHasher();
        var user = User.Create(EmailAddress.From("ana@example.com"), "Ana", hasher.HashPassword("Antiga123"), Now);
        repository.Users.Add(user);
        var originalHash = user.PasswordHash;
        var handler = new ChangePasswordCommandHandler(repository, hasher, new StubJwtTokenService(), new Sha256TokenHasher(), new FixedDateTimeProvider(Now), TestJwtOptions.Default());

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            handler.HandleAsync(new ChangePasswordCommand(user.Id, "Outra123", "Nova456789"), CancellationToken.None));

        Assert.Equal("INVALID_CURRENT_PASSWORD", ex.Code);
        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Equal(0, repository.SaveChangesCalls);
    }

    // create/join group: session for a user whose refresh token was deleted

    [Fact]
    public async Task RefreshTokenIssuer_IssuesWhenMissingOrExpired_AndLeavesAValidOneAlone()
    {
        var repository = new FakeAuthRepository();
        var tokens = new Sha256TokenHasher();
        var userId = Guid.NewGuid();

        var issued = await RefreshTokenIssuer.EnsureAsync(repository, tokens, userId, Now, 7, CancellationToken.None);
        Assert.NotNull(issued);
        Assert.Equal(tokens.Hash(issued!), Assert.Single(repository.RefreshTokens).TokenHash);

        var untouched = await RefreshTokenIssuer.EnsureAsync(repository, tokens, userId, Now.AddDays(1), 7, CancellationToken.None);
        Assert.Null(untouched);
        Assert.Equal(tokens.Hash(issued), Assert.Single(repository.RefreshTokens).TokenHash);

        var afterExpiry = await RefreshTokenIssuer.EnsureAsync(repository, tokens, userId, Now.AddDays(8), 7, CancellationToken.None);
        Assert.NotNull(afterExpiry);
        Assert.Equal(tokens.Hash(afterExpiry!), Assert.Single(repository.RefreshTokens).TokenHash);
    }

    private static LoginCommandHandler NewLoginHandler(FakeAuthRepository repository, IPasswordHasher hasher) => new(
        repository, hasher, new StubJwtTokenService(), new Sha256TokenHasher(), new FixedDateTimeProvider(Now), TestJwtOptions.Default());

    private sealed class SpyPasswordHasher : IPasswordHasher
    {
        public List<string> VerifiedHashes { get; } = new();

        public string DummyHash => "dummy-hash-marker";

        public string HashPassword(string password) => "hashed:" + password;

        public bool VerifyPassword(string password, string passwordHash)
        {
            VerifiedHashes.Add(passwordHash);
            return false;
        }
    }
}
