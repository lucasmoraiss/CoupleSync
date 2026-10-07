using CoupleSync.Application.Notification;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Support;

/// <summary>An AlertPolicyService wired to in-memory fakes, with a couple of known members.</summary>
public sealed class AlertPolicyTestKit
{
    public FakeBudgetRepository Budgets { get; } = new();
    public FakeTransactionRepository Transactions { get; } = new();
    public FakeNotificationEventRepository Events { get; } = new();
    public FakeCoupleRepository Couples { get; } = new();
    public FakeNotificationSettingsRepository Settings { get; } = new();
    public Guid CoupleId { get; }
    public IReadOnlyList<User> Members { get; }
    public AlertPolicyService Service { get; }

    public AlertPolicyTestKit(int memberCount = 1, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);
        var couple = Couple.Create("ABC123", now);
        var members = new List<User>();
        for (var i = 0; i < memberCount; i++)
        {
            var user = User.Create(EmailAddress.From($"member{i}-{Guid.NewGuid():N}@example.com"), $"Membro {i}", "hash", now);
            couple.AddMember(user, now);
            members.Add(user);
        }

        Couples.Couples.Add(couple);
        CoupleId = couple.Id;
        Members = members;
        Service = new AlertPolicyService(Budgets, Transactions, Events, Couples, Settings);
    }

    public Guid Author => Members[0].Id;
}
