using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Couples;

public sealed record LeaveCoupleCommand(Guid UserId);

/// <summary>Fresh tokens for the user, who is no longer in any group (the old refresh token no longer works).</summary>
public sealed record LeaveCoupleResult(string AccessToken, string RefreshToken);

/// <summary>
/// The user leaves their group. Transactions they entered stay in the group; the group is kept even when
/// nobody is left in it (it simply becomes unreachable). The old refresh token is replaced, so no session
/// opened before the exit can renew itself into the group, and the response carries a token pair with no group.
/// </summary>
public sealed class LeaveCoupleCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly IAuthRepository _authRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly JwtOptions _jwtOptions;

    public LeaveCoupleCommandHandler(
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

    public async Task<LeaveCoupleResult> HandleAsync(LeaveCoupleCommand command, CancellationToken cancellationToken)
    {
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (!user.CoupleId.HasValue)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Você não faz parte de nenhum grupo.");
        }

        var coupleId = user.CoupleId.Value;
        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, cancellationToken)
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");

        var now = _dateTimeProvider.UtcNow;
        couple.RemoveMember(user, now);
        await _coupleRepository.StopDeliveriesToMemberAsync(user.Id, coupleId, cancellationToken);

        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshTokenRaw = RefreshTokenGenerator.Generate();
        var refreshToken = RefreshToken.CreateForUser(
            user.Id,
            _tokenHasher.Hash(refreshTokenRaw),
            now.AddDays(_jwtOptions.RefreshTokenTtlDays),
            now);
        await _authRepository.UpsertRefreshTokenAsync(refreshToken, cancellationToken);

        await _coupleRepository.SaveChangesAsync(cancellationToken);

        return new LeaveCoupleResult(accessToken, refreshTokenRaw);
    }
}
