using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Couples;

public sealed record SwitchCoupleCommand(Guid UserId, Guid CoupleId);

public sealed record SwitchCoupleResult(Guid CoupleId, string AccessToken, string RefreshToken);

/// <summary>
/// Makes another of the user's groups the active one and returns tokens for it. The target must be a group
/// the user belongs to right now; anything else gets the same "not found" answer, whether or not it exists.
/// </summary>
public sealed class SwitchCoupleCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly IAuthRepository _authRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly JwtOptions _jwtOptions;

    public SwitchCoupleCommandHandler(
        ICoupleRepository coupleRepository,
        IAuthRepository authRepository,
        IDateTimeProvider dateTimeProvider,
        IJwtTokenService jwtTokenService,
        ITokenHasher tokenHasher,
        IOptions<JwtOptions> jwtOptions)
    {
        _coupleRepository = coupleRepository;
        _authRepository = authRepository;
        _dateTimeProvider = dateTimeProvider;
        _jwtTokenService = jwtTokenService;
        _tokenHasher = tokenHasher;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<SwitchCoupleResult> HandleAsync(SwitchCoupleCommand command, CancellationToken cancellationToken)
    {
        // A removal of this user from the target group takes the same lock, so the membership read below
        // cannot be overtaken by it.
        await using var change = await _coupleRepository.BeginMembershipChangeAsync(command.UserId, cancellationToken);
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var target = (await _coupleRepository.GetGroupsOfUserAsync(user.Id, cancellationToken))
            .FirstOrDefault(g => g.CoupleId == command.CoupleId)
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Você não faz parte deste grupo.");

        var now = _dateTimeProvider.UtcNow;
        user.SetActiveCouple(target.CoupleId, target.JoinedAtUtc);

        var (accessToken, refreshToken) = await SessionTokens.ReplaceAsync(
            user, _jwtTokenService, _authRepository, _tokenHasher, now, _jwtOptions.RefreshTokenTtlDays, cancellationToken);

        await _coupleRepository.SaveChangesAsync(cancellationToken);
        await change.CommitAsync(cancellationToken);

        return new SwitchCoupleResult(target.CoupleId, accessToken, refreshToken);
    }
}
