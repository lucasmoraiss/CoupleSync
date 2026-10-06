using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Couples;

public sealed class JoinCoupleCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly ILogger<JoinCoupleCommandHandler> _logger;
    private readonly IAuthRepository _authRepository;
    private readonly ITokenHasher _tokenHasher;
    private readonly JwtOptions _jwtOptions;

    public JoinCoupleCommandHandler(
        ICoupleRepository coupleRepository,
        IDateTimeProvider dateTimeProvider,
        IJwtTokenService jwtTokenService,
        INotificationEventRepository notificationEventRepository,
        ILogger<JoinCoupleCommandHandler> logger,
        IAuthRepository authRepository,
        ITokenHasher tokenHasher,
        IOptions<JwtOptions> jwtOptions)
    {
        _authRepository = authRepository;
        _tokenHasher = tokenHasher;
        _jwtOptions = jwtOptions.Value;
        _coupleRepository = coupleRepository;
        _dateTimeProvider = dateTimeProvider;
        _jwtTokenService = jwtTokenService;
        _notificationEventRepository = notificationEventRepository;
        _logger = logger;
    }

    public async Task<JoinCoupleResult> HandleAsync(JoinCoupleCommand command, CancellationToken cancellationToken)
    {
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (user.CoupleId.HasValue)
        {
            throw new ConflictException("USER_ALREADY_IN_COUPLE", "Você já faz parte de um casal.");
        }

        var normalizedJoinCode = command.JoinCode.Trim().ToUpperInvariant();
        var couple = await _coupleRepository.FindByJoinCodeAsync(normalizedJoinCode, cancellationToken);

        if (couple is null)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");
        }

        var now = _dateTimeProvider.UtcNow;

        if (couple.IsJoinCodeExpired(now))
        {
            throw new AppException(
                "JOIN_CODE_EXPIRED",
                "Esse código de convite venceu. Peça um código novo a quem criou o grupo.",
                410);
        }

        couple.AddMember(user, now);

        // A user removed from a group has no refresh token left; give them a working one (null when they already
        // have one) so the session does not die at the next renewal. Saved together with the membership.
        var refreshTokenRaw = await RefreshTokenIssuer.EnsureAsync(
            _authRepository, _tokenHasher, user.Id, now, _jwtOptions.RefreshTokenTtlDays, cancellationToken);
        await _coupleRepository.SaveChangesAsync(cancellationToken);

        // Notify the existing member that a partner has joined.
        var otherMember = couple.Members.FirstOrDefault(m => m.Id != command.UserId);
        if (otherMember is not null)
        {
            try
            {
                var notification = NotificationEvent.Create(
                    couple.Id,
                    otherMember.Id,
                    "PartnerJoined",
                    "Novo parceiro(a)!",
                    $"{user.Name} entrou no seu casal! 💕",
                    now);
                await _notificationEventRepository.AddRangeAsync([notification], cancellationToken);
                await _notificationEventRepository.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PartnerJoined notification dispatch failed for couple {CoupleId}", couple.Id);
            }
        }

        var members = couple.Members
            .Select(member => new CoupleMemberDto(member.Id, member.Name, member.Email))
            .ToArray();

        // Regenerate JWT so the user's couple_id claim is populated immediately.
        // Without this, the app stays in a "no couple" state until re-login.
        var accessToken = _jwtTokenService.GenerateAccessToken(user);

        return new JoinCoupleResult(couple.Id, members, accessToken, refreshTokenRaw);
    }
}
