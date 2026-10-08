using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Issue #38 — rule 1 of the chain now reads ai_consents: the group has the AI on while at least one acceptance of
/// the current version, not revoked, belongs to someone who is still a member. The gateway is the real one, with the
/// real gate and repository on a real database (SQLite): the answer is read again right before every call, so
/// switching it off stops the very next one.
/// </summary>
[Trait("Category", "AiChat")]
public sealed class ServerAiConsentGateTests : IDisposable
{
    private const string Gemini = "gemini";
    private const string Flash = "gemini-flash-latest";
    private const string Lite = "gemini-flash-lite-latest";

    private readonly LlmGatewayTestKit _kit = new();
    private readonly Guid _coupleId;
    private readonly Guid _anaId;
    private readonly Guid _brunoId;
    private readonly Guid _otherCoupleId;

    public ServerAiConsentGateTests()
    {
        var now = _kit.Clock.UtcNow;
        using var db = _kit.NewContext();
        var ana = User.Create(EmailAddress.From("ana@example.com"), "Ana Exemplo", "hash", now);
        var bruno = User.Create(EmailAddress.From("bruno@example.com"), "Bruno Exemplo", "hash", now);
        var carla = User.Create(EmailAddress.From("carla@example.com"), "Carla Exemplo", "hash", now);
        var couple = Couple.Create("ABCD1234", now);
        couple.AddMember(ana, now);
        couple.AddMember(bruno, now);
        var other = Couple.Create("WXYZ9876", now);
        other.AddMember(carla, now);
        db.AddRange(ana, bruno, carla, couple, other);
        db.SaveChanges();
        (_coupleId, _anaId, _brunoId, _otherCoupleId) = (couple.Id, ana.Id, bruno.Id, other.Id);
    }

    public void Dispose() => _kit.Dispose();

    private ServerAiConsentGate Gate() => new(new AiActivationRepository(_kit.NewContext()));

    private LlmGateway Gateway() => new(
        _kit.Catalog,
        new AiUsageRepository(_kit.NewContext()),
        Gate(),
        Microsoft.Extensions.Options.Options.Create(_kit.Options),
        new LlmMinuteWindow(),
        _kit.Clock,
        new AdvancingWaiter(_kit.Clock),
        _kit.Log);

    private Task<LlmGatewayResult<TestAnswer>> AskAsync(Guid? coupleId)
        => Gateway().GenerateAsync<TestAnswer>(coupleId, LlmGatewayTestKit.Request(), LlmCallMode.Interactive, CancellationToken.None);

    private void Accept(Guid coupleId, Guid userId, int version = AiConsent.CurrentVersion)
    {
        using var db = _kit.NewContext();
        db.AiConsents.Add(AiConsent.Accept(coupleId, userId, version, _kit.Clock.UtcNow));
        db.SaveChanges();
    }

    private void RevokeAll(Guid coupleId)
    {
        using var db = _kit.NewContext();
        foreach (var consent in db.AiConsents.Where(c => c.CoupleId == coupleId).ToList()) consent.Revoke(_kit.Clock.UtcNow, _brunoId);
        db.SaveChanges();
    }

    [Fact]
    public async Task AGroupWithNoAcceptance_IsNotConsented_AndNoProviderIsCalled()
    {
        var provider = new ScriptedProvider(Gemini, Flash);
        _kit.Chain(AiChains.Assistant, provider);

        var result = await AskAsync(_coupleId);

        Assert.Equal(LlmGatewayOutcome.NotConsented, result.Outcome);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(_kit.Rows());
    }

    [Fact]
    public async Task OneAcceptanceOfTheCurrentVersion_SwitchesTheWholeGroupOn_AndOnlyThatGroup()
    {
        _kit.Chain(AiChains.Assistant, new ScriptedProvider(Gemini, Flash));
        Accept(_coupleId, _anaId);

        Assert.Equal(LlmGatewayOutcome.Ok, (await AskAsync(_coupleId)).Outcome);
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await AskAsync(_otherCoupleId)).Outcome);
        // A call without a group has nobody who could have accepted.
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await AskAsync(null)).Outcome);
    }

    [Fact]
    public async Task AnAcceptanceOfAnotherVersion_ARevokedOne_AndOneOfSomeoneWhoIsNotAMember_DoNotCount()
    {
        _kit.Chain(AiChains.Assistant, new ScriptedProvider(Gemini, Flash));

        // Another version of the text.
        Accept(_coupleId, _anaId, version: AiConsent.CurrentVersion + 1);
        Assert.False(await Gate().IsEnabledAsync(_coupleId, CancellationToken.None));

        // Someone who accepted for this group but is not (or no longer) in it: Carla belongs to the other group.
        using (var db = _kit.NewContext())
        {
            var carlaId = db.CoupleMembers.Single(m => m.CoupleId == _otherCoupleId).UserId;
            db.AiConsents.Add(AiConsent.Accept(_coupleId, carlaId, AiConsent.CurrentVersion, _kit.Clock.UtcNow));
            db.SaveChanges();
        }

        Assert.False(await Gate().IsEnabledAsync(_coupleId, CancellationToken.None));

        // A revoked acceptance of a member.
        Accept(_coupleId, _brunoId);
        Assert.True(await Gate().IsEnabledAsync(_coupleId, CancellationToken.None));
        RevokeAll(_coupleId);
        Assert.False(await Gate().IsEnabledAsync(_coupleId, CancellationToken.None));
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await AskAsync(_coupleId)).Outcome);
    }

    [Fact]
    public async Task SwitchingItOffWhileTheFirstModelAnswers_StopsTheChainBeforeTheNextModel()
    {
        Accept(_coupleId, _anaId);
        var first = new ScriptedProvider(Gemini, Flash).Then((_, _) =>
        {
            // The other member switches the AI off for the group while the first link is answering.
            RevokeAll(_coupleId);
            return Task.FromResult(ScriptedProvider.Result(LlmOutcome.Error));
        });
        var second = new ScriptedProvider(Gemini, Lite);
        _kit.Chain(AiChains.Assistant, first, second);

        var result = await AskAsync(_coupleId);

        Assert.Equal(LlmGatewayOutcome.NotConsented, result.Outcome);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }
}
