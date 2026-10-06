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

/// <summary>Fix round 1: durable per-address cap, atomic reset, no user-chosen text in e-mails, retry on insert races.</summary>
[Trait("Category", "EmailCodes")]
public sealed class EmailCodeHardeningTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private const string OldPassword = "SenhaAntiga123";

    private sealed class Rig
    {
        public Rig(string name = "Ana")
        {
            Clock = new MutableDateTimeProvider(Start);
            Repository = new FakeAuthRepository();
            Sender = new InMemoryEmailSender();
            Hasher = new BCryptPasswordHasher();
            User = User.Create(EmailAddress.From("ana@example.com"), name, Hasher.HashPassword(OldPassword), Start);
            Repository.Users.Add(User);
            Repository.RefreshTokens.Add(RefreshToken.CreateForUser(User.Id, "hash-device-1", Start.AddDays(7), Start));
        }

        public MutableDateTimeProvider Clock { get; }
        public FakeAuthRepository Repository { get; }
        public InMemoryEmailSender Sender { get; }
        public BCryptPasswordHasher Hasher { get; }
        public User User { get; }

        /// <summary>A brand new flow over the same database: what the API looks like after a restart.</summary>
        public EmailCodeFlow NewFlow() => EmailTestKit.NewFlow(Repository, Sender, Clock);

        public string LastCode => InMemoryEmailSender.CodeOf(Sender.Sent[^1]);
    }

    // 3: the cap lives in the database

    [Fact]
    public async Task TheRequestCap_SurvivesARestart()
    {
        var rig = new Rig();
        var before = rig.NewFlow();
        for (var i = 0; i < 5; i++)
        {
            await new RequestPasswordResetCommandHandler(rig.Repository, before, EmailTestKit.NewCodeService())
                .HandleAsync(new RequestPasswordResetCommand("ana@example.com"), CancellationToken.None);
        }

        Assert.Equal(5, rig.Sender.Sent.Count);

        var afterRestart = rig.NewFlow(); // new process: nothing in memory
        await new RequestPasswordResetCommandHandler(rig.Repository, afterRestart, EmailTestKit.NewCodeService())
            .HandleAsync(new RequestPasswordResetCommand("ana@example.com"), CancellationToken.None);

        Assert.Equal(5, rig.Sender.Sent.Count);
    }

    [Fact]
    public async Task TheCap_IsPerPurpose_AndRecoversAfterTheWindow()
    {
        var rig = new Rig();
        var flow = rig.NewFlow();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(CodeIssueOutcome.Sent, await flow.IssueAsync(rig.User, EmailCodePurpose.PasswordReset, CancellationToken.None));
        }

        Assert.Equal(CodeIssueOutcome.Throttled, await rig.NewFlow().IssueAsync(rig.User, EmailCodePurpose.PasswordReset, CancellationToken.None));
        Assert.Equal(CodeIssueOutcome.Sent, await rig.NewFlow().IssueAsync(rig.User, EmailCodePurpose.EmailVerification, CancellationToken.None));

        rig.Clock.UtcNow = Start.AddHours(1).AddSeconds(1);
        Assert.Equal(CodeIssueOutcome.Sent, await rig.NewFlow().IssueAsync(rig.User, EmailCodePurpose.PasswordReset, CancellationToken.None));
    }

    // 4: consuming the code, saving the password and revoking the sessions are all-or-nothing

    [Fact]
    public async Task Reset_WhenRevokingTheSessionsFails_ChangesNothing()
    {
        var rig = new Rig();
        var flow = rig.NewFlow();
        await flow.IssueAsync(rig.User, EmailCodePurpose.PasswordReset, CancellationToken.None);
        var code = rig.LastCode;
        rig.Repository.RevokeRefreshTokensFailure = new InvalidOperationException("database went away");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ResetPasswordCommandHandler(rig.Repository, rig.Hasher, flow)
                .HandleAsync(new ResetPasswordCommand("ana@example.com", code, "NovaSenha456"), CancellationToken.None));

        Assert.True(rig.Hasher.VerifyPassword(OldPassword, rig.User.PasswordHash), "the password must not change while the old session survives");
        Assert.Single(rig.Repository.RefreshTokens);
        Assert.Single(rig.Repository.EmailCodes); // the code was not burned: the user can try again

        rig.Repository.RevokeRefreshTokensFailure = null;
        await new ResetPasswordCommandHandler(rig.Repository, rig.Hasher, flow)
            .HandleAsync(new ResetPasswordCommand("ana@example.com", code, "NovaSenha456"), CancellationToken.None);
        Assert.True(rig.Hasher.VerifyPassword("NovaSenha456", rig.User.PasswordHash));
        Assert.Empty(rig.Repository.RefreshTokens);
    }

    // 5: no user-chosen text in the e-mails

    [Theory]
    [InlineData(EmailCodePurpose.EmailVerification)]
    [InlineData(EmailCodePurpose.PasswordReset)]
    public async Task Emails_NeverCarryTheUserChosenName(string purpose)
    {
        var rig = new Rig(name: "Compre agora em http://evil.example/pix");

        await rig.NewFlow().IssueAsync(rig.User, purpose, CancellationToken.None);

        var message = Assert.Single(rig.Sender.Sent);
        Assert.DoesNotContain("evil", message.TextContent);
        Assert.DoesNotContain("evil", message.HtmlContent);
        Assert.DoesNotContain("evil", message.Subject);
        Assert.DoesNotContain("evil", message.ToName);
        Assert.StartsWith("Olá!", message.TextContent);
    }

    // 7: a lost insert race is not "too many codes"

    [Fact]
    public async Task Resend_WhenAConcurrentRequestWinsTheInsert_RetriesInsteadOfReportingTooManyCodes()
    {
        var rig = new Rig();
        rig.Repository.FailNextCodeStores = 1;

        await new ResendEmailVerificationCommandHandler(rig.Repository, rig.NewFlow())
            .HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);

        Assert.Single(rig.Sender.Sent); // the retry succeeded
    }

    [Fact]
    public async Task Resend_WhenTheRaceKeepsLosing_StillIsNot429()
    {
        var rig = new Rig();
        rig.Repository.FailNextCodeStores = 2;

        var outcome = await rig.NewFlow().IssueAsync(rig.User, EmailCodePurpose.EmailVerification, CancellationToken.None);
        Assert.Equal(CodeIssueOutcome.Raced, outcome);

        rig.Repository.FailNextCodeStores = 2;
        await new ResendEmailVerificationCommandHandler(rig.Repository, rig.NewFlow())
            .HandleAsync(new ResendEmailVerificationCommand(rig.User.Id), CancellationToken.None);
    }
}
