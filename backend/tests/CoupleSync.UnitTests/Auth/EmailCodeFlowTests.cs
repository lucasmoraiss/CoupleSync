using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Auth;

/// <summary>Password reset and e-mail confirmation: codes, attempts, expiry, single use, nothing revealed about accounts.</summary>
[Trait("Category", "EmailCodes")]
public sealed class EmailCodeFlowTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private const string OldPassword = "SenhaAntiga123";

    private sealed class Rig
    {
        public Rig()
        {
            Clock = new MutableDateTimeProvider(Start);
            Repository = new FakeAuthRepository();
            Sender = new InMemoryEmailSender();
            Flow = EmailTestKit.NewFlow(Repository, Sender, Clock);
            Hasher = new BCryptPasswordHasher();
            User = User.Create(EmailAddress.From("ana@example.com"), "Ana", Hasher.HashPassword(OldPassword), Start);
            Repository.Users.Add(User);
            Repository.RefreshTokens.Add(RefreshToken.CreateForUser(User.Id, "hash-device-1", Start.AddDays(7), Start));
        }

        public MutableDateTimeProvider Clock { get; }
        public FakeAuthRepository Repository { get; }
        public InMemoryEmailSender Sender { get; }
        public EmailCodeFlow Flow { get; }
        public BCryptPasswordHasher Hasher { get; }
        public User User { get; }

        public RequestPasswordResetCommandHandler Request => new(Repository, Flow, EmailTestKit.NewCodeService());
        public ResetPasswordCommandHandler Reset => new(Repository, Hasher, Flow);
        public ConfirmEmailCommandHandler Confirm => new(Repository, Flow);
        public ResendEmailVerificationCommandHandler Resend => new(Repository, Flow);

        public string LastCode => InMemoryEmailSender.CodeOf(Sender.Sent[^1]);

        public Task RequestResetAsync(string email = "ana@example.com") =>
            Request.HandleAsync(new RequestPasswordResetCommand(email), CancellationToken.None);

        public Task ResetAsync(string code, string newPassword = "NovaSenha456", string email = "ana@example.com") =>
            Reset.HandleAsync(new ResetPasswordCommand(email, code, newPassword), CancellationToken.None);
    }

    private static async Task<AppException> Fails(Func<Task> action) => await Assert.ThrowsAsync<BadRequestException>(action);

    // code service

    [Fact]
    public void Codes_AreSixDigits_AndVary()
    {
        var service = EmailTestKit.NewCodeService();
        var codes = Enumerable.Range(0, 200).Select(_ => service.Generate()).ToList();

        Assert.All(codes, code => Assert.Matches(@"^\d{6}$", code));
        Assert.True(codes.Distinct().Count() > 150);
    }

    [Fact]
    public void Hash_IsKeyed_BoundToUserAndPurpose_AndNotTheCode()
    {
        var service = EmailTestKit.NewCodeService();
        var user = Guid.NewGuid();
        var hash = service.Hash(user, EmailCodePurpose.PasswordReset, "123456");

        Assert.DoesNotContain("123456", hash);
        Assert.True(service.Verify(user, EmailCodePurpose.PasswordReset, "123456", hash));
        Assert.False(service.Verify(user, EmailCodePurpose.PasswordReset, "123457", hash));
        Assert.False(service.Verify(user, EmailCodePurpose.EmailVerification, "123456", hash));
        Assert.False(service.Verify(Guid.NewGuid(), EmailCodePurpose.PasswordReset, "123456", hash));

        var otherKey = new VerificationCodeService(Options.Create(new JwtOptions { Secret = "a-completely-different-secret-value-xx" }));
        Assert.NotEqual(hash, otherKey.Hash(user, EmailCodePurpose.PasswordReset, "123456"));
    }

    // request

    [Fact]
    public async Task Request_ForKnownAccount_SendsOneEmailWithSixDigitCode_StoredOnlyHashed_Valid15Minutes()
    {
        var rig = new Rig();

        await rig.RequestResetAsync();

        var message = Assert.Single(rig.Sender.Sent);
        Assert.Equal("ana@example.com", message.ToAddress);
        var code = InMemoryEmailSender.CodeOf(message);
        Assert.Matches(@"^\d{6}$", code);
        Assert.Contains(code, message.HtmlContent);
        Assert.DoesNotContain("http", message.HtmlContent);
        Assert.Contains("senha", message.Subject, StringComparison.OrdinalIgnoreCase);

        var stored = Assert.Single(rig.Repository.EmailCodes);
        Assert.NotEqual(code, stored.CodeHash);
        Assert.DoesNotContain(code, stored.CodeHash);
        Assert.Equal(EmailCodePurpose.PasswordReset, stored.Purpose);
        Assert.Equal(Start.AddMinutes(15), stored.ExpiresAtUtc);
    }

    [Fact]
    public async Task Request_ForUnknownAccount_SendsNothing_StoresNothing_AndDoesNotThrow()
    {
        var rig = new Rig();

        await rig.RequestResetAsync("ghost@example.com");

        Assert.Empty(rig.Sender.Sent);
        Assert.Empty(rig.Repository.EmailCodes);
    }

    [Fact]
    public async Task Request_WhenTheProviderFails_StillCompletesNormally()
    {
        var rig = new Rig();
        rig.Sender.Failure = new HttpRequestException("provider down");

        await rig.RequestResetAsync();
        await rig.RequestResetAsync("ghost@example.com");
    }

    [Fact]
    public async Task Request_WithoutEmailConfigured_Answers503_EmailNotConfigured()
    {
        var rig = new Rig();
        rig.Sender.IsConfigured = false;

        var ex = await Assert.ThrowsAsync<AppException>(() => rig.RequestResetAsync());

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("EMAIL_NOT_CONFIGURED", ex.Code);
    }

    [Fact]
    public async Task Request_AgainReplacesThePreviousCode()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var first = rig.LastCode;
        await rig.RequestResetAsync();
        var second = rig.LastCode;

        Assert.Single(rig.Repository.EmailCodes);
        if (first != second) // 1 in a million they collide; then there is nothing to prove
        {
            await Fails(() => rig.ResetAsync(first));
        }

        await rig.ResetAsync(second);
    }

    [Fact]
    public async Task Request_PerAddressCap_StopsSendingSilently_ThenRecoversAfterTheWindow()
    {
        var rig = new Rig();

        for (var i = 0; i < EmailCodeFlow.MaxCodesPerWindow + 3; i++)
        {
            await rig.RequestResetAsync();
        }

        Assert.Equal(EmailCodeFlow.MaxCodesPerWindow, rig.Sender.Sent.Count);

        rig.Clock.UtcNow = Start.Add(EmailCodeFlow.IssueWindow).AddSeconds(1);
        await rig.RequestResetAsync();
        Assert.Equal(EmailCodeFlow.MaxCodesPerWindow + 1, rig.Sender.Sent.Count);
    }

    // reset

    [Fact]
    public async Task Reset_WithTheRightCode_ChangesPassword_RevokesAllRefreshTokens_AndTheCodeIsSingleUse()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var code = rig.LastCode;

        await rig.ResetAsync(code);

        Assert.True(rig.Hasher.VerifyPassword("NovaSenha456", rig.User.PasswordHash));
        Assert.False(rig.Hasher.VerifyPassword(OldPassword, rig.User.PasswordHash));
        Assert.Empty(rig.Repository.RefreshTokens);
        Assert.Empty(rig.Repository.EmailCodes);

        var again = await Fails(() => rig.ResetAsync(code, "OutraSenha789"));
        Assert.Equal("INVALID_CODE", again.Code);
        Assert.True(rig.Hasher.VerifyPassword("NovaSenha456", rig.User.PasswordHash));
    }

    [Fact]
    public async Task Reset_WithAWrongCode_ChangesNothing()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var wrong = rig.LastCode == "000000" ? "000001" : "000000";

        var ex = await Fails(() => rig.ResetAsync(wrong));

        Assert.Equal("INVALID_CODE", ex.Code);
        Assert.True(rig.Hasher.VerifyPassword(OldPassword, rig.User.PasswordHash));
        Assert.Single(rig.Repository.RefreshTokens);
    }

    [Fact]
    public async Task Reset_AfterFiveWrongAttempts_EvenTheRightCodeIsRejected()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var right = rig.LastCode;
        var wrong = right == "000000" ? "000001" : "000000";

        for (var i = 0; i < EmailCodeFlow.MaxAttempts; i++)
        {
            await Fails(() => rig.ResetAsync(wrong));
        }

        var ex = await Fails(() => rig.ResetAsync(right));
        Assert.Equal("INVALID_CODE", ex.Code);
        Assert.True(rig.Hasher.VerifyPassword(OldPassword, rig.User.PasswordHash));
    }

    [Fact]
    public async Task Reset_TheFifthAttemptStillCounts_AsLongAsItIsRight()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var right = rig.LastCode;
        var wrong = right == "000000" ? "000001" : "000000";

        for (var i = 0; i < EmailCodeFlow.MaxAttempts - 1; i++)
        {
            await Fails(() => rig.ResetAsync(wrong));
        }

        await rig.ResetAsync(right);
        Assert.True(rig.Hasher.VerifyPassword("NovaSenha456", rig.User.PasswordHash));
    }

    [Fact]
    public async Task Reset_AfterANewRequest_TheAttemptsStartOverOnTheNewCode()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var wrong = rig.LastCode == "000000" ? "000001" : "000000";
        for (var i = 0; i < EmailCodeFlow.MaxAttempts; i++)
        {
            await Fails(() => rig.ResetAsync(wrong));
        }

        await rig.RequestResetAsync();
        await rig.ResetAsync(rig.LastCode);

        Assert.True(rig.Hasher.VerifyPassword("NovaSenha456", rig.User.PasswordHash));
    }

    [Fact]
    public async Task Reset_AfterExpiry_IsRejected()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var code = rig.LastCode;
        rig.Clock.UtcNow = Start.AddMinutes(15).AddSeconds(1);

        var ex = await Fails(() => rig.ResetAsync(code));

        Assert.Equal("INVALID_CODE", ex.Code);
        Assert.True(rig.Hasher.VerifyPassword(OldPassword, rig.User.PasswordHash));
    }

    [Fact]
    public async Task Reset_ForUnknownAccount_FailsLikeAWrongCode()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var known = await Fails(() => rig.ResetAsync("111111"));
        var unknown = await Fails(() => rig.ResetAsync("111111", email: "ghost@example.com"));

        Assert.Equal(known.Code, unknown.Code);
        Assert.Equal(known.Message, unknown.Message);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
    }

    [Fact]
    public async Task Reset_WithAWeakPassword_IsRejected_WithoutSpendingAnAttempt()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        var code = rig.LastCode;

        var ex = await Fails(() => rig.ResetAsync(code, "curta"));

        Assert.Equal("VALIDATION_ERROR", ex.Code);
        Assert.Contains(PasswordPolicy.TooShortMessage, ex.Errors!["NewPassword"]);
        Assert.Equal(0, rig.Repository.EmailCodes.Single().Attempts);
        await rig.ResetAsync(code);
    }

    [Fact]
    public async Task Reset_ACodeOfAnotherPurpose_DoesNotWork()
    {
        var rig = new Rig();
        await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
        var verificationCode = rig.LastCode;

        var ex = await Fails(() => rig.ResetAsync(verificationCode));

        Assert.Equal("INVALID_CODE", ex.Code);
    }

    [Fact]
    public async Task Reset_ForAnInactiveAccount_IsTreatedAsUnknown()
    {
        var rig = new Rig();
        await rig.RequestResetAsync();
        typeof(User).GetProperty(nameof(User.IsActive))!.SetValue(rig.User, false);

        var ex = await Fails(() => rig.ResetAsync(rig.LastCode));

        Assert.Equal("INVALID_CODE", ex.Code);
    }

    // e-mail confirmation

    [Fact]
    public async Task Confirm_WithTheRightCode_MarksTheEmailVerified_OnlyOnce()
    {
        var rig = new Rig();
        Assert.False(rig.User.EmailVerified);
        await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
        var code = rig.LastCode;

        await rig.Confirm.HandleAsync(new ConfirmEmailCommand(rig.User.Id, code), CancellationToken.None);

        Assert.True(rig.User.EmailVerified);
        Assert.Empty(rig.Repository.EmailCodes);
    }

    [Fact]
    public async Task Confirm_WithAWrongCode_LeavesTheEmailUnverified_AndLocksAfterFiveAttempts()
    {
        var rig = new Rig();
        await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
        var right = rig.LastCode;
        var wrong = right == "000000" ? "000001" : "000000";

        for (var i = 0; i < EmailCodeFlow.MaxAttempts; i++)
        {
            var ex = await Fails(() => rig.Confirm.HandleAsync(new ConfirmEmailCommand(rig.User.Id, wrong), CancellationToken.None));
            Assert.Equal("INVALID_CODE", ex.Code);
        }

        await Fails(() => rig.Confirm.HandleAsync(new ConfirmEmailCommand(rig.User.Id, right), CancellationToken.None));
        Assert.False(rig.User.EmailVerified);
    }

    [Fact]
    public async Task Resend_InvalidatesThePreviousCode()
    {
        var rig = new Rig();
        await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
        var first = rig.LastCode;
        await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
        var second = rig.LastCode;

        if (first != second)
        {
            await Fails(() => rig.Confirm.HandleAsync(new ConfirmEmailCommand(rig.User.Id, first), CancellationToken.None));
        }

        await rig.Confirm.HandleAsync(new ConfirmEmailCommand(rig.User.Id, second), CancellationToken.None);
        Assert.True(rig.User.EmailVerified);
    }

    [Fact]
    public async Task Resend_OverTheCap_Answers429_ForTheOwnAccount()
    {
        var rig = new Rig();
        for (var i = 0; i < EmailCodeFlow.MaxCodesPerWindow; i++)
        {
            await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
        }

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
    }

    [Fact]
    public async Task Resend_WhenAlreadyVerified_SendsNothing()
    {
        var rig = new Rig();
        rig.User.MarkEmailVerified();

        await rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);

        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task ConfirmAndResend_WithoutEmailConfigured_Answer503()
    {
        var rig = new Rig();
        rig.Sender.IsConfigured = false;

        var confirm = await Assert.ThrowsAsync<AppException>(() =>
            rig.Confirm.HandleAsync(new ConfirmEmailCommand(rig.User.Id, "123456"), CancellationToken.None));
        var resend = await Assert.ThrowsAsync<AppException>(() =>
            rig.Resend.HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None));

        Assert.Equal("EMAIL_NOT_CONFIGURED", confirm.Code);
        Assert.Equal("EMAIL_NOT_CONFIGURED", resend.Code);
    }

    // registration

    private static RegisterCommandHandler NewRegister(Rig rig) => new(
        rig.Repository,
        rig.Hasher,
        new StubJwtTokenService(),
        new Sha256TokenHasher(),
        rig.Clock,
        Options.Create(new JwtOptions { Secret = "this-is-a-secure-test-secret-with-32chars" }),
        rig.Flow,
        NullLogger<RegisterCommandHandler>.Instance);

    [Fact]
    public async Task Register_SendsTheConfirmationCode_AndStartsUnverified()
    {
        var rig = new Rig();

        var result = await NewRegister(rig).HandleAsync(new RegisterCommand("bia@example.com", "Bia", "SenhaForte123"), CancellationToken.None);

        Assert.False(result.User.EmailVerified);
        var message = Assert.Single(rig.Sender.Sent);
        Assert.Equal("bia@example.com", message.ToAddress);
        Assert.Matches(@"^\d{6}$", InMemoryEmailSender.CodeOf(message));
        Assert.Equal(EmailCodePurpose.EmailVerification, rig.Repository.EmailCodes.Single().Purpose);
    }

    [Fact]
    public async Task Register_StillSucceeds_WhenEmailIsNotConfigured_OrTheProviderFails()
    {
        var off = new Rig();
        off.Sender.IsConfigured = false;
        var failing = new Rig();
        failing.Sender.Failure = new InvalidOperationException("boom");

        var first = await NewRegister(off).HandleAsync(new RegisterCommand("bia@example.com", "Bia", "SenhaForte123"), CancellationToken.None);
        var second = await NewRegister(failing).HandleAsync(new RegisterCommand("bia@example.com", "Bia", "SenhaForte123"), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(first.AccessToken));
        Assert.False(string.IsNullOrEmpty(second.AccessToken));
        Assert.Empty(off.Sender.Sent);
        Assert.Contains(failing.Repository.Users, u => u.Email == "bia@example.com");
    }
}
