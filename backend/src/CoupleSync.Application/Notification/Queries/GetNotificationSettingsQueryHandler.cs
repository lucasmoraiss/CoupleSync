using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Notification.Queries;

public sealed class GetNotificationSettingsQueryHandler
{
    private readonly INotificationSettingsRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetNotificationSettingsQueryHandler(
        INotificationSettingsRepository repository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<NotificationSettingsDto> HandleAsync(
        GetNotificationSettingsQuery query,
        CancellationToken cancellationToken)
    {
        var settings = await _repository.GetByUserIdAsync(query.UserId, query.CoupleId, cancellationToken);

        if (settings is null)
        {
            // First call — initialise defaults.
            // Guard against concurrent first-call race: if a unique constraint violation occurs,
            // re-fetch the row that the racing request created.
            try
            {
                settings = await _repository.UpsertAsync(
                    query.UserId,
                    query.CoupleId,
                    null,
                    null,
                    null,
                    _dateTimeProvider.UtcNow,
                    cancellationToken);
                await _repository.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                settings = await _repository.GetByUserIdAsync(query.UserId, query.CoupleId, cancellationToken);
                if (settings is null) throw;
            }
        }

        return new NotificationSettingsDto(
            settings.UserId,
            settings.LowBalanceEnabled,
            settings.LargeTransactionEnabled,
            settings.BillReminderEnabled,
            settings.UpdatedAtUtc);
    }
}
