using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Notification.Commands;

public sealed class RegisterDeviceTokenCommandHandler
{
    private readonly IDeviceTokenRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public RegisterDeviceTokenCommandHandler(
        IDeviceTokenRepository repository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task HandleAsync(RegisterDeviceTokenCommand command, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        await _repository.UpsertAsync(command.UserId, command.CoupleId, command.Token, now, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }
}
