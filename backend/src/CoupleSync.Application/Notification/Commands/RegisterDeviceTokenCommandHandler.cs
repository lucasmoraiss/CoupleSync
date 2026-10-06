using CoupleSync.Application.Common;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Notification.Commands;

public sealed class RegisterDeviceTokenCommandHandler
{
    private const int MaxAttempts = 8;

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
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await UpsertAndSaveAsync(command, now, cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
            {
                // Concurrent registrations of the same token (or user) raced: another one won a unique index, or already deleted
                // the row this pass meant to hand over (a concurrency exception).
                // Redo from a clean state: the next pass sees the winner's row and updates or replaces it. Several
                // devices can race at once, so a single retry is not enough (found on real PostgreSQL); the number of
                // passes is bounded and an exhausted budget is a retryable conflict, never a 500.
                _repository.DiscardPendingChanges();
                if (attempt >= MaxAttempts)
                {
                    throw new ConflictException(
                        "DEVICE_TOKEN_CONFLICT",
                        "Não foi possível registrar o dispositivo agora. Tente novamente.");
                }

                // A short random pause breaks the lockstep of requests that keep colliding on the same rows.
                await Task.Delay(Random.Shared.Next(5, 25 * attempt), cancellationToken);
            }
        }
    }

    private async Task UpsertAndSaveAsync(RegisterDeviceTokenCommand command, DateTime now, CancellationToken cancellationToken)
    {
        await _repository.UpsertAsync(command.UserId, command.CoupleId, command.Token, now, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }

}
