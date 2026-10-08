using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Couples;

/// <param name="CoupleId">
/// The group to leave: the one named in the route, or the group of the caller's token. Never the stored active
/// group: a token issued before a switch elsewhere must leave the group its holder is looking at.
/// </param>
public sealed record LeaveCoupleCommand(Guid UserId, Guid? CoupleId);

/// <summary>
/// Fresh tokens for the user (the old refresh token no longer works) and the group that is active now:
/// another of the user's groups when the one they left was the active one, or none.
/// </summary>
public sealed record LeaveCoupleResult(string AccessToken, string RefreshToken, Guid? ActiveCoupleId);

/// <summary>
/// The user leaves one of their groups. Transactions they entered stay in the group; the group is kept even
/// when nobody is left in it (it simply becomes unreachable). The old refresh token is replaced, so no session
/// opened before the exit can renew itself into the group. Their Open Finance in that group (connection,
/// credentials, items and accounts) is deleted in the same save.
/// </summary>
public sealed class LeaveCoupleCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly IAuthRepository _authRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly IPluggyClient _pluggy;
    private readonly JwtOptions _jwtOptions;

    public LeaveCoupleCommandHandler(
        ICoupleRepository coupleRepository,
        IAuthRepository authRepository,
        IDateTimeProvider dateTimeProvider,
        IJwtTokenService jwtTokenService,
        ITokenHasher tokenHasher,
        IPluggyClient pluggy,
        IOptions<JwtOptions> jwtOptions)
    {
        _coupleRepository = coupleRepository;
        _authRepository = authRepository;
        _dateTimeProvider = dateTimeProvider;
        _jwtTokenService = jwtTokenService;
        _tokenHasher = tokenHasher;
        _pluggy = pluggy;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<LeaveCoupleResult> HandleAsync(LeaveCoupleCommand command, CancellationToken cancellationToken)
    {
        await using var change = await _coupleRepository.BeginMembershipChangeAsync(command.UserId, cancellationToken);
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var coupleId = command.CoupleId
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Você não está em nenhum grupo no momento.");

        // Members are read after the lock, so two members leaving at once each see the other's exit: exactly one
        // of them is the last to leave (and expires the code), and ownership never passes to someone already gone.
        await change.LockCoupleAsync(coupleId, cancellationToken);
        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, cancellationToken);

        // Same answer for a group that does not exist and for one the user is not in.
        if (couple is null || !couple.HasMember(user.Id))
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Você não faz parte deste grupo.");
        }

        var now = _dateTimeProvider.UtcNow;
        couple.RemoveMember(user, now);

        var remaining = (await _coupleRepository.GetGroupsOfUserAsync(user.Id, cancellationToken))
            .Where(g => g.CoupleId != coupleId)
            .ToList();

        // Leaving the active group: the oldest of the remaining groups becomes active, or none.
        // (Leaving another group changes nothing about which one is active.)
        if (user.ActiveCoupleId == coupleId)
        {
            var next = remaining.FirstOrDefault();
            if (next is null)
            {
                user.ClearActiveCouple();
            }
            else
            {
                user.SetActiveCouple(next.CoupleId, next.JoinedAtUtc);
            }
        }

        await _coupleRepository.StopDeliveriesToMemberAsync(
            user.Id, coupleId, user.ActiveCoupleId ?? remaining.FirstOrDefault()?.CoupleId, cancellationToken);

        var bankConnectionIds = await _coupleRepository.RemoveOpenFinanceOfMemberAsync(user.Id, coupleId, cancellationToken);

        // Their acceptance of the AI analysis stops counting for the group, and their AI preferences there go.
        await _coupleRepository.RemoveAiOfMemberAsync(user.Id, coupleId, now, cancellationToken);

        var (accessToken, refreshToken) = await SessionTokens.ReplaceAsync(
            user, _jwtTokenService, _authRepository, _tokenHasher, now, _jwtOptions.RefreshTokenTtlDays, cancellationToken);

        await _coupleRepository.SaveChangesAsync(cancellationToken);
        await change.CommitAsync(cancellationToken);

        // Only once the connections are really gone: the Pluggy API keys kept for them are dropped.
        foreach (var connectionId in bankConnectionIds)
        {
            _pluggy.ForgetConnection(connectionId);
        }

        return new LeaveCoupleResult(accessToken, refreshToken, user.ActiveCoupleId);
    }
}
