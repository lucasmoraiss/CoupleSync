using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// The accounts and the cards the group connected through Open Finance, by person (issue #27): each account with its
/// balance and when the bank gave it, each card with its bills. What a person connects belongs to the group:
/// everyone of the group reads all of it. Only what belongs to a connection still connected is shown (a disconnected
/// one is no longer read, and its balances would only grow old). Nothing is added up here: each value stays in its
/// own currency.
/// </summary>
public sealed class BankAccountsService
{
    /// <summary>How many bills already due a card shows.</summary>
    public const int MaxPreviousBills = 12;

    /// <summary>The name of who connected and is no longer in the group (leaving deletes the connection: only older data can have it).</summary>
    private const string FormerMemberName = "Pessoa que saiu do grupo";

    private readonly IBankConnectionRepository _connections;
    private readonly IBankSyncRepository _sync;
    private readonly ICoupleRepository _couples;
    private readonly IDateTimeProvider _clock;

    public BankAccountsService(
        IBankConnectionRepository connections,
        IBankSyncRepository sync,
        ICoupleRepository couples,
        IDateTimeProvider clock)
    {
        _connections = connections;
        _sync = sync;
        _couples = couples;
        _clock = clock;
    }

    public async Task<BankAccountsOverviewDto> GetAsync(Guid coupleId, Guid userId, CancellationToken ct)
    {
        var connections = (await _connections.GetConnectionsAsync(coupleId, ct))
            .Where(c => c.Status != BankConnectionStatus.Disconnected)
            .ToList();
        if (connections.Count == 0) return new BankAccountsOverviewDto([]);

        var connectionIds = connections.Select(c => c.Id).ToHashSet();
        var items = (await _connections.GetItemsAsync(coupleId, ct)).Where(i => connectionIds.Contains(i.ConnectionId)).ToList();
        var itemsById = items.ToDictionary(i => i.Id);
        var accounts = (await _connections.GetAccountsAsync(coupleId, ct)).Where(a => itemsById.ContainsKey(a.ItemId)).ToList();
        var billsByAccount = (await _sync.GetBillsOfGroupAsync(coupleId, null, null, ct)).ToLookup(b => b.BankAccountId);
        var names = await _couples.GetMemberNamesAsync(coupleId, ct);
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(_clock.UtcNow));
        var ownerOfItem = items.ToDictionary(i => i.Id, i => connections.First(c => c.Id == i.ConnectionId).UserId);

        var people = connections
            .Select(c => c.UserId)
            .Distinct()
            .Select(owner =>
            {
                var own = accounts
                    .Where(a => ownerOfItem[a.ItemId] == owner)
                    .OrderBy(a => itemsById[a.ItemId].ConnectorName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => NameOf(a), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => a.Id)
                    .ToList();
                return new BankAccountsPersonDto(
                    owner,
                    names.TryGetValue(owner, out var name) ? name : FormerMemberName,
                    owner == userId,
                    own.Where(a => !BankReconciliationRules.IsCreditCard(a.Type))
                        .Select(a => new BankBalanceAccountDto(
                            a.Id, itemsById[a.ItemId].ConnectorName, NameOf(a), a.Type, a.Subtype, a.NumberMasked, a.Currency, a.Balance, a.BalanceAtUtc))
                        .ToList(),
                    own.Where(a => BankReconciliationRules.IsCreditCard(a.Type))
                        .Select(a => MapCard(a, itemsById[a.ItemId].ConnectorName, billsByAccount[a.Id], today))
                        .ToList());
            })
            // Who asks first, then by name.
            .OrderByDescending(p => p.IsMe)
            .ThenBy(p => p.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.UserId)
            .ToList();

        return new BankAccountsOverviewDto(people);
    }

    private static string NameOf(BankAccount account)
        => string.IsNullOrWhiteSpace(account.Name) ? account.MarketingName ?? string.Empty : account.Name;

    /// <summary>
    /// The current bill is the first one due from <paramref name="today"/> (in Brazil) on: a bill due today is still
    /// to be paid today. The ones due after it come next, nearest first; the ones already due, most recent first.
    /// </summary>
    private static BankCardDto MapCard(BankAccount card, string bankName, IEnumerable<GroupCardBill> bills, DateOnly today)
    {
        var ordered = bills.OrderBy(b => b.DueDate).ThenBy(b => b.Id).ToList();
        var upcoming = ordered.Where(b => b.DueDate >= today).ToList();
        var previous = ordered.Where(b => b.DueDate < today).Reverse().Take(MaxPreviousBills).ToList();

        return new BankCardDto(
            card.Id,
            bankName,
            NameOf(card),
            card.Brand,
            card.NumberMasked,
            card.Currency,
            card.Balance,
            card.BalanceAtUtc,
            card.CreditLimit,
            card.AvailableCreditLimit,
            card.BalanceCloseDate,
            card.BalanceDueDate,
            upcoming.Count > 0 ? MapBill(upcoming[0]) : null,
            upcoming.Skip(1).Select(MapBill).ToList(),
            previous.Select(MapBill).ToList());
    }

    private static CardBillDto MapBill(GroupCardBill bill)
        => new(bill.Id, bill.DueDate, bill.ClosingDate, bill.TotalAmount, bill.Currency, bill.MinimumPayment, bill.IsClosed);
}
