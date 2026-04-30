using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.Transactions.Commands;

/// <summary>
/// Creates a transaction manually entered by the user (no OCR / no bank notification).
/// Persists a synthetic <see cref="TransactionEventIngest"/> entry marked as the
/// origin so the existing Transaction → IngestEvent invariant is preserved.
/// </summary>
public sealed class CreateManualTransactionCommandHandler
{
    private const string ManualBank = "MANUAL";

    private readonly ITransactionRepository _transactionRepository;
    private readonly INotificationCaptureRepository _ingestRepository;
    private readonly IDateTimeProvider _clock;
    private readonly IAlertPolicyService _alertPolicyService;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly INotificationSettingsRepository _notificationSettingsRepository;
    private readonly ILogger<CreateManualTransactionCommandHandler> _logger;

    public CreateManualTransactionCommandHandler(
        ITransactionRepository transactionRepository,
        INotificationCaptureRepository ingestRepository,
        IDateTimeProvider clock,
        IAlertPolicyService alertPolicyService,
        INotificationEventRepository notificationEventRepository,
        INotificationSettingsRepository notificationSettingsRepository,
        ILogger<CreateManualTransactionCommandHandler> logger)
    {
        _transactionRepository = transactionRepository;
        _ingestRepository = ingestRepository;
        _clock = clock;
        _alertPolicyService = alertPolicyService;
        _notificationEventRepository = notificationEventRepository;
        _notificationSettingsRepository = notificationSettingsRepository;
        _logger = logger;
    }

    public async Task<Transaction> HandleAsync(CreateManualTransactionCommand cmd, CancellationToken ct)
    {
        if (cmd.CoupleId == Guid.Empty) throw new AppException("INVALID_INPUT", "CoupleId is required.", 400);
        if (cmd.UserId == Guid.Empty) throw new AppException("INVALID_INPUT", "UserId is required.", 400);
        if (cmd.Amount <= 0) throw new AppException("INVALID_INPUT", "Amount must be greater than zero.", 400);
        if (string.IsNullOrWhiteSpace(cmd.Currency)) throw new AppException("INVALID_INPUT", "Currency is required.", 400);
        if (string.IsNullOrWhiteSpace(cmd.Category)) throw new AppException("INVALID_INPUT", "Category is required.", 400);

        var now = _clock.UtcNow;
        var eventTs = cmd.EventTimestampUtc == default ? now : cmd.EventTimestampUtc;

        var ingest = TransactionEventIngest.Create(
            coupleId: cmd.CoupleId,
            userId: cmd.UserId,
            bank: ManualBank,
            amount: cmd.Amount,
            currency: cmd.Currency,
            eventTimestamp: eventTs,
            description: cmd.Description,
            merchant: cmd.Merchant,
            rawNotificationTextRedacted: null,
            createdAtUtc: now);

        await _ingestRepository.AddIngestEventAsync(ingest, ct);

        // Fingerprint includes 'manual:' prefix + ingest id to guarantee uniqueness
        // (user may legitimately enter duplicate amounts on the same day).
        var fingerprint = $"manual:{ingest.Id:N}";

        var transaction = Transaction.Create(
            coupleId: cmd.CoupleId,
            userId: cmd.UserId,
            fingerprint: fingerprint,
            bank: ManualBank,
            amount: cmd.Amount,
            currency: cmd.Currency.Trim().ToUpperInvariant(),
            eventTimestampUtc: eventTs,
            description: cmd.Description,
            merchant: cmd.Merchant,
            category: cmd.Category.Trim(),
            ingestEventId: ingest.Id,
            createdAtUtc: now);

        await _transactionRepository.AddTransactionAsync(transaction, ct);
        await _transactionRepository.SaveChangesAsync(ct);

        // Fire-and-not-propagate: alert policy evaluation after successful transaction persist.
        try
        {
            var nowUtc = _clock.UtcNow;
            var since = nowUtc.AddDays(-30);
            var settings = await _notificationSettingsRepository.GetByUserIdAsync(cmd.UserId, cmd.CoupleId, ct);
            if (settings is not null)
            {
                var recentTransactions = await _transactionRepository.GetRecentByCoupleAsync(cmd.CoupleId, since, ct);
                var alertEvents = await _alertPolicyService.EvaluatePostIngestAsync(
                    cmd.CoupleId, cmd.UserId, transaction, recentTransactions, settings, nowUtc, ct);
                if (alertEvents.Count > 0)
                {
                    await _notificationEventRepository.AddRangeAsync(alertEvents, ct);
                    await _notificationEventRepository.SaveChangesAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert policy evaluation failed for couple {CoupleId}", cmd.CoupleId);
        }

        return transaction;
    }
}
