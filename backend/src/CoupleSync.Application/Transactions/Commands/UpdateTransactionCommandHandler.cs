using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Notification;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.Transactions.Commands;

/// <summary>
/// Edits a transaction of the couple. Budget spent, reports and goal progress are computed from the stored
/// transactions on every read, so saving the new values is what keeps them coherent. Alerts are evaluated again
/// (budget alerts are already deduplicated per category and month by the policy), except that a transaction
/// which was already large does not raise the large-transaction alert a second time.
/// </summary>
public sealed class UpdateTransactionCommandHandler
{
    private readonly ITransactionRepository _repository;
    private readonly IDateTimeProvider _clock;
    private readonly IAlertPolicyService _alertPolicyService;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly ILogger<UpdateTransactionCommandHandler> _logger;

    public UpdateTransactionCommandHandler(
        ITransactionRepository repository,
        IDateTimeProvider clock,
        IAlertPolicyService alertPolicyService,
        INotificationEventRepository notificationEventRepository,
        ILogger<UpdateTransactionCommandHandler> logger)
    {
        _repository = repository;
        _clock = clock;
        _alertPolicyService = alertPolicyService;
        _notificationEventRepository = notificationEventRepository;
        _logger = logger;
    }

    public async Task<Transaction> HandleAsync(UpdateTransactionCommand command, CancellationToken ct)
    {
        var transaction = await _repository.GetByIdAsync(command.TransactionId, command.CoupleId, ct)
            ?? throw new NotFoundException("TRANSACTION_NOT_FOUND", "Transação não encontrada.");

        if (command.Amount is { } amount
            && (amount <= 0 || amount > MoneyRules.MaxAmount || !MoneyRules.HasAtMostTwoDecimals(amount)))
        {
            throw new AppException(
                "INVALID_INPUT",
                "O valor deve ser maior que zero, ter no máximo duas casas decimais e não passar de R$ 999.999.999,99.",
                400);
        }

        string? category = null;
        if (command.Category is not null)
            category = TransactionCategories.TryNormalize(command.Category)
                ?? throw new AppException("INVALID_CATEGORY", TransactionCategories.InvalidMessage, 400);

        var previousAmount = transaction.Amount;
        var changesSpending = command.Amount.HasValue || command.EventTimestampUtc.HasValue || category is not null;

        transaction.Edit(command.Amount, command.Description, command.EventTimestampUtc, category, command.Merchant);
        await _repository.SaveChangesAsync(ct);

        if (changesSpending)
            await EvaluateAlertsAsync(transaction, previousAmount, ct);

        return transaction;
    }

    private async Task EvaluateAlertsAsync(Transaction transaction, decimal previousAmount, CancellationToken ct)
    {
        try
        {
            var nowUtc = _clock.UtcNow;
            // Alerts talk about the current month; editing an older transaction changes no current alert.
            if (BrazilTime.MonthOf(transaction.EventTimestampUtc) != BrazilTime.MonthOf(nowUtc))
                return;

            var recent = await _repository.GetRecentByCoupleAsync(transaction.CoupleId, nowUtc.AddDays(-30), ct);
            var events = await _alertPolicyService.EvaluatePostIngestAsync(
                transaction.CoupleId, transaction, recent, nowUtc, ct);

            // A transaction that was already above the threshold produced its large-transaction alert back then.
            var alreadyLarge = CurrencyRules.IsBrl(transaction.Currency)
                && previousAmount > AlertPolicyService.LargeTransactionThreshold;
            var toSend = alreadyLarge
                ? events.Where(e => e.AlertType != AlertPolicyService.LargeTransactionKind).ToList()
                : events.ToList();

            if (toSend.Count > 0)
            {
                await _notificationEventRepository.AddRangeAsync(toSend, ct);
                await _notificationEventRepository.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert policy evaluation failed after editing a transaction of couple {CoupleId}", transaction.CoupleId);
        }
    }
}
