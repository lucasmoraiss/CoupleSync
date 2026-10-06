using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

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
        try
        {
            await UpsertAndSaveAsync(command, now, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two registrations of the same token (or user) raced and the other one won the unique index.
            // Redo once from a clean state: the second pass sees the winner's row and updates it instead.
            _repository.DiscardPendingChanges();
            await UpsertAndSaveAsync(command, now, cancellationToken);
        }
    }

    private async Task UpsertAndSaveAsync(RegisterDeviceTokenCommand command, DateTime now, CancellationToken cancellationToken)
    {
        await _repository.UpsertAsync(command.UserId, command.CoupleId, command.Token, now, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase);
    }
}
