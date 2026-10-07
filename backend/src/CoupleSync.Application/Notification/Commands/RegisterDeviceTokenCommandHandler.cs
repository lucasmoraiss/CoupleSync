using CoupleSync.Application.Common;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Notification.Commands;

public sealed class RegisterDeviceTokenCommandHandler
{
    private const int MaxAttempts = 8;

    private readonly IDeviceTokenRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Pause between retries; injectable so tests do not really sleep (default: Task.Delay).</param>
    public RegisterDeviceTokenCommandHandler(
        IDeviceTokenRepository repository,
        IDateTimeProvider dateTimeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
        _delay = delay ?? Task.Delay;
    }

    public async Task HandleAsync(RegisterDeviceTokenCommand command, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // On PostgreSQL the registration is serialised by token and by user (advisory locks), so the retry
                // below is only the fallback for other providers and for the rare cross-user row conflict.
                await using var registration = await _repository.BeginRegistrationAsync(command.UserId, command.Token, cancellationToken);
                await UpsertAndSaveAsync(command, now, cancellationToken);
                await registration.CommitAsync(cancellationToken);
                return;
            }
            catch (DataStoreException ex) when (ex is ConcurrencyConflictException or UniqueViolationException)
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
                await _delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25 * attempt)), cancellationToken);
            }
        }
    }

    private async Task UpsertAndSaveAsync(RegisterDeviceTokenCommand command, DateTime now, CancellationToken cancellationToken)
    {
        await _repository.UpsertAsync(command.UserId, command.CoupleId, command.Token, now, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }

}
