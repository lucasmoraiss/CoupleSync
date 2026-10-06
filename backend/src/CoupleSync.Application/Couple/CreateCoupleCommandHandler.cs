using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Couples;

public sealed class CreateCoupleCommandHandler
{
    private const int MaxJoinCodeAttempts = 20;

    private readonly ICoupleRepository _coupleRepository;
    private readonly ICoupleJoinCodeGenerator _joinCodeGenerator;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuthRepository _authRepository;
    private readonly ITokenHasher _tokenHasher;
    private readonly JwtOptions _jwtOptions;

    public CreateCoupleCommandHandler(
        ICoupleRepository coupleRepository,
        ICoupleJoinCodeGenerator joinCodeGenerator,
        IDateTimeProvider dateTimeProvider,
        IJwtTokenService jwtTokenService,
        IAuthRepository authRepository,
        ITokenHasher tokenHasher,
        IOptions<JwtOptions> jwtOptions)
    {
        _authRepository = authRepository;
        _tokenHasher = tokenHasher;
        _jwtOptions = jwtOptions.Value;
        _coupleRepository = coupleRepository;
        _joinCodeGenerator = joinCodeGenerator;
        _dateTimeProvider = dateTimeProvider;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<CreateCoupleResult> HandleAsync(CreateCoupleCommand command, CancellationToken cancellationToken)
    {
        // Serialises this user's membership changes, so the limit below cannot be passed by parallel requests.
        await using var change = await _coupleRepository.BeginMembershipChangeAsync(command.UserId, cancellationToken);
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var groups = await _coupleRepository.GetGroupsOfUserAsync(user.Id, cancellationToken);
        GroupLimit.EnsureRoomForOneMore(groups.Count);

        var now = _dateTimeProvider.UtcNow;
        var joinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);
        var couple = Couple.Create(joinCode, now);
        couple.AddMember(user, now);

        await _coupleRepository.AddCoupleAsync(couple, cancellationToken);

        // A user removed from a group has no refresh token left; give them a working one (null when they already
        // have one) so the session does not die at the next renewal. Saved together with the group.
        var refreshTokenRaw = await RefreshTokenIssuer.EnsureAsync(
            _authRepository, _tokenHasher, user.Id, now, _jwtOptions.RefreshTokenTtlDays, cancellationToken);
        await _coupleRepository.SaveChangesAsync(cancellationToken);
        await change.CommitAsync(cancellationToken);

        // The new group is now the user's active one: the access token carries it in the couple_id claim.
        var accessToken = _jwtTokenService.GenerateAccessToken(user);

        return new CreateCoupleResult(couple.Id, couple.JoinCode, accessToken, refreshTokenRaw);
    }

    private async Task<string> GenerateUniqueJoinCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxJoinCodeAttempts; attempt++)
        {
            var joinCode = _joinCodeGenerator.Generate();
            if (!await _coupleRepository.JoinCodeExistsAsync(joinCode, cancellationToken))
            {
                return joinCode;
            }
        }

        throw new AppException("COUPLE_CODE_GENERATION_FAILED", "Não foi possível gerar o código de convite. Tente novamente.", 500);
    }
}
