using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Notification.Commands;

public sealed class UpdateNotificationSettingsCommandHandler
{
    private readonly INotificationSettingsRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public UpdateNotificationSettingsCommandHandler(
        INotificationSettingsRepository repository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task HandleAsync(UpdateNotificationSettingsCommand command, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        await _repository.UpsertAsync(
            command.UserId,
            command.CoupleId,
            command.LowBalanceEnabled,
            command.LargeTransactionEnabled,
            command.BillReminderEnabled,
            now,
            cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }
}
