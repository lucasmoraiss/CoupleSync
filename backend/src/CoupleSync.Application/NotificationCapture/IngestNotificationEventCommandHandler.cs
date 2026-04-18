using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.NotificationCapture;

public sealed class IngestNotificationEventCommandHandler
{
    private const int MaxTextLength = 512;

    private readonly INotificationCaptureRepository _repository;
    private readonly INotificationEventSanitizer _sanitizer;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ICategoryMatchingService _categoryMatchingService;
    private readonly IFingerprintGenerator _fingerprintGenerator;
    private readonly IAlertPolicyService _alertPolicyService;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly INotificationSettingsRepository _notificationSettingsRepository;

    public IngestNotificationEventCommandHandler(
        INotificationCaptureRepository repository,
        INotificationEventSanitizer sanitizer,
        IDateTimeProvider dateTimeProvider,
        ITransactionRepository transactionRepository,
        ICategoryMatchingService categoryMatchingService,
        IFingerprintGenerator fingerprintGenerator,
        IAlertPolicyService alertPolicyService,
        INotificationEventRepository notificationEventRepository,
        INotificationSettingsRepository notificationSettingsRepository)
    {
        _repository = repository;
        _sanitizer = sanitizer;
        _dateTimeProvider = dateTimeProvider;
        _transactionRepository = transactionRepository;
        _categoryMatchingService = categoryMatchingService;
        _fingerprintGenerator = fingerprintGenerator;
        _alertPolicyService = alertPolicyService;
        _notificationEventRepository = notificationEventRepository;
        _notificationSettingsRepository = notificationSettingsRepository;
    }

    public async Task<IngestNotificationEventResult> HandleAsync(
        IngestNotificationEventCommand command,
        CancellationToken cancellationToken)
    {
        var sanitizedDescription = _sanitizer.SanitizeText(command.Description, MaxTextLength);
        var sanitizedMerchant = _sanitizer.SanitizeText(command.Merchant, MaxTextLength);
        var sanitizedRawText = _sanitizer.SanitizeText(command.RawNotificationText, MaxTextLength);

        var descOrNull = sanitizedDescription.Length > 0 ? sanitizedDescription : null;
        var merchantOrNull = sanitizedMerchant.Length > 0 ? sanitizedMerchant : null;

        var ingestEvent = TransactionEventIngest.Create(
            command.CoupleId,
            command.UserId,
            command.Bank,
            command.Amount,
            command.Currency,
            command.EventTimestamp,
            descOrNull,
            merchantOrNull,
            sanitizedRawText.Length > 0 ? sanitizedRawText : null,
            _dateTimeProvider.UtcNow);

        await _repository.AddIngestEventAsync(ingestEvent, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        // Deduplication via fingerprint
        var fingerprint = _fingerprintGenerator.Generate(
            command.CoupleId, command.Bank, command.Amount,
            command.Currency, command.EventTimestamp, merchantOrNull);

        if (await _transactionRepository.FingerprintExistsAsync(fingerprint, command.CoupleId, cancellationToken))
        {
            ingestEvent.MarkDuplicate();
            await _repository.SaveChangesAsync(cancellationToken);
            return new IngestNotificationEventResult(ingestEvent.Id, "Duplicate");
        }

        // Category matching
        var category = await _categoryMatchingService.MatchCategoryAsync(descOrNull, merchantOrNull, cancellationToken);

        var transaction = Transaction.Create(
            command.CoupleId,
            command.UserId,
            fingerprint,
            command.Bank,
            command.Amount,
            command.Currency,
            command.EventTimestamp,
            descOrNull,
            merchantOrNull,
            category,
            ingestEvent.Id,
            _dateTimeProvider.UtcNow);

        await _transactionRepository.AddTransactionAsync(transaction, cancellationToken);

        try
        {
            await _transactionRepository.SaveChangesAsync(cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Concurrent duplicate: unique constraint on (couple_id, fingerprint) prevented insert.
            ingestEvent.MarkDuplicate();
            await _repository.SaveChangesAsync(cancellationToken);
            return new IngestNotificationEventResult(ingestEvent.Id, "Duplicate");
        }

        // Fire-and-not-propagate: alert policy evaluation after successful transaction persist.
        try
        {
            var nowUtc = _dateTimeProvider.UtcNow;
            var since = nowUtc.AddDays(-30);
            var settings = await _notificationSettingsRepository.GetByUserIdAsync(command.UserId, command.CoupleId, cancellationToken);
            if (settings is not null)
            {
                var recentTransactions = await _transactionRepository.GetRecentByCoupleAsync(command.CoupleId, since, cancellationToken);
                var alertEvents = await _alertPolicyService.EvaluatePostIngestAsync(
                    command.CoupleId,
                    command.UserId,
                    transaction,
                    recentTransactions,
                    settings,
                    nowUtc,
                    cancellationToken);

                if (alertEvents.Count > 0)
                {
                    await _notificationEventRepository.AddRangeAsync(alertEvents, cancellationToken);
                    await _notificationEventRepository.SaveChangesAsync(cancellationToken);
                }
            }
        }
        catch
        {
            // Alert evaluation failures must not fail the ingest response.
        }

        return new IngestNotificationEventResult(ingestEvent.Id, "Accepted");
    }
}
