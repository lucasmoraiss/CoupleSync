using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Rule 1 of the chain: the AI is on for every group, with no acceptance, until a member switches it off for the
/// group on purpose (couples.ai_disabled_at_utc). The rows of ai_consents switch nothing on or off. The gateway is
/// the real one, with the real gate and repository on a real database (SQLite): the answer is read again right
/// before every call, so switching it off stops the very next one.
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

    /// <summary>"Off for the group", as the route stores it: written straight into the column of the group.</summary>
    private void SwitchOff(Guid coupleId)
    {
        using var db = _kit.NewContext();
        Assert.Equal(1, db.Database.ExecuteSqlInterpolated($"UPDATE couples SET ai_disabled_at_utc = {_kit.Clock.UtcNow} WHERE id = {coupleId}"));
    }

    [Fact]
    public async Task AGroupWithNoAcceptance_IsOn_AndTheProviderIsCalled()
    {
        var provider = new ScriptedProvider(Gemini, Flash);
        _kit.Chain(AiChains.Assistant, provider);

        var result = await AskAsync(_coupleId);

        Assert.Equal(LlmGatewayOutcome.Ok, result.Outcome);
        Assert.Equal(1, provider.Calls);
        Assert.Single(_kit.Rows());
    }

    [Fact]
    public async Task OnlyTheGroupThatSwitchedItOff_IsStopped_AndACallWithoutAGroup_OrOfAGroupThatDoesNotExist_SendsNothing()
    {
        var provider = new ScriptedProvider(Gemini, Flash);
        _kit.Chain(AiChains.Assistant, provider);
        SwitchOff(_otherCoupleId);

        Assert.Equal(LlmGatewayOutcome.Ok, (await AskAsync(_coupleId)).Outcome);
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await AskAsync(_otherCoupleId)).Outcome);
        // Background work without a group, and a group nobody has: nothing is sent.
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await AskAsync(null)).Outcome);
        Assert.Equal(LlmGatewayOutcome.NotConsented, (await AskAsync(Guid.NewGuid())).Outcome);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task TheRowsOfAiConsents_OfAnyVersion_RevokedOrNot_OfMembersOrNot_SwitchNothingOnOrOff()
    {
        _kit.Chain(AiChains.Assistant, new ScriptedProvider(Gemini, Flash));

        // An acceptance of the version before, one of a version that does not exist yet, one of the text in force.
        Accept(_coupleId, _anaId, version: 1);
        Accept(_coupleId, _anaId, version: AiConsent.CurrentVersion + 1);
        Accept(_coupleId, _brunoId);
        // Someone who accepted for this group but is not in it: Carla belongs to the other group.
        using (var db = _kit.NewContext())
        {
            var carlaId = db.CoupleMembers.Single(m => m.CoupleId == _otherCoupleId).UserId;
            db.AiConsents.Add(AiConsent.Accept(_coupleId, carlaId, AiConsent.CurrentVersion, _kit.Clock.UtcNow));
            db.SaveChanges();
        }

        Assert.True(await Gate().IsEnabledAsync(_coupleId, CancellationToken.None));

        // Every acceptance revoked, as the "off for the group" and the "take mine back" of before left them: that
        // is not "off on purpose" — only the column of the group is.
        RevokeAll(_coupleId);
        Assert.True(await Gate().IsEnabledAsync(_coupleId, CancellationToken.None));
        Assert.Equal(LlmGatewayOutcome.Ok, (await AskAsync(_coupleId)).Outcome);

        // And an acceptance in force does not switch on a group that switched itself off.
        Accept(_otherCoupleId, _anaId);
        SwitchOff(_otherCoupleId);
        Assert.False(await Gate().IsEnabledAsync(_otherCoupleId, CancellationToken.None));
    }

    [Fact]
    public async Task SwitchingItOffWhileTheFirstModelAnswers_StopsTheChainBeforeTheNextModel()
    {
        var first = new ScriptedProvider(Gemini, Flash).Then((_, _) =>
        {
            // The other member switches the AI off for the group while the first link is answering.
            SwitchOff(_coupleId);
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
