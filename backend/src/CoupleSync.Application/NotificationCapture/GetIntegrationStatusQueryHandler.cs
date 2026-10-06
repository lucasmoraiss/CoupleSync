using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.NotificationCapture;

public sealed class GetIntegrationStatusQueryHandler
{
    private const int ActiveWindowDays = 7;

    private readonly INotificationCaptureRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetIntegrationStatusQueryHandler(
        INotificationCaptureRepository repository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<IntegrationStatusResult> HandleAsync(
        GetIntegrationStatusQuery query,
        CancellationToken cancellationToken)
    {
        var totalAccepted = await _repository.CountByStatusAsync(query.CoupleId, IngestStatus.Accepted, cancellationToken);
        var totalRejected = await _repository.CountByStatusAsync(query.CoupleId, IngestStatus.Rejected, cancellationToken);
        var totalDuplicate = await _repository.CountByStatusAsync(query.CoupleId, IngestStatus.Duplicate, cancellationToken);

        var lastEventTime = await _repository.GetLastEventTimeAsync(query.CoupleId, cancellationToken);
        var lastRejected = await _repository.GetLastByStatusAsync(query.CoupleId, IngestStatus.Rejected, cancellationToken);

        var isActive = lastEventTime.HasValue
            && lastEventTime.Value >= _dateTimeProvider.UtcNow.AddDays(-ActiveWindowDays);

        string? lastErrorMessage = lastRejected?.ErrorMessage;
        DateTime? lastErrorAtUtc = lastRejected?.CreatedAtUtc;
        string? recoveryHint = DetermineRecoveryHint(lastEventTime, lastRejected, isActive);

        return new IntegrationStatusResult(
            isActive,
            lastEventTime,
            lastErrorAtUtc,
            lastErrorMessage,
            recoveryHint,
            totalAccepted,
            totalDuplicate,
            totalRejected);
    }

    private string? DetermineRecoveryHint(
        DateTime? lastEventTime,
        TransactionEventIngest? lastRejected,
        bool isActive)
    {
        if (lastRejected is not null
            && lastRejected.ErrorMessage is not null
            && (lastRejected.ErrorMessage.Contains("validation", StringComparison.OrdinalIgnoreCase)
                || lastRejected.ErrorMessage.Contains("validaç", StringComparison.OrdinalIgnoreCase)))
        {
            return "Verifique o formato das notificações enviadas pelo aplicativo do banco.";
        }

        if (!isActive && lastEventTime.HasValue)
        {
            return "Confira se o acesso do CoupleSync às notificações está ativado nas configurações do Android.";
        }

        if (!isActive && !lastEventTime.HasValue)
        {
            return null; // No events at all — nothing to recover from
        }

        if (lastRejected is not null && lastRejected.ErrorMessage is not null)
        {
            return $"Revise o erro do último evento rejeitado: {lastRejected.ErrorMessage}";
        }

        return null;
    }
}
