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
        // Serialises this user's membership changes (the limit) and, below, this group's (join against the last leave).
        await using var change = await _coupleRepository.BeginMembershipChangeAsync(command.UserId, cancellationToken);
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var normalizedJoinCode = command.JoinCode.Trim().ToUpperInvariant();
        var coupleId = await _coupleRepository.FindIdByJoinCodeAsync(normalizedJoinCode, cancellationToken)
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");

        // Only what is read after the lock counts: while this request waited, the code may have been renewed
        // or the last member may have left (which expires the code).
        await change.LockCoupleAsync(coupleId, cancellationToken);
        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, cancellationToken);

        if (couple is null || couple.JoinCode != normalizedJoinCode)
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

        if (couple.HasMember(user.Id))
        {
            throw new ConflictException("USER_ALREADY_IN_COUPLE", "Você já faz parte deste grupo.");
        }

        var groups = await _coupleRepository.GetGroupsOfUserAsync(user.Id, cancellationToken);
        GroupLimit.EnsureRoomForOneMore(groups.Count);

        couple.AddMember(user, now);

        // A user removed from a group has no refresh token left; give them a working one (null when they already
        // have one) so the session does not die at the next renewal. Saved together with the membership.
        var refreshTokenRaw = await RefreshTokenIssuer.EnsureAsync(
            _authRepository, _tokenHasher, user.Id, now, _jwtOptions.RefreshTokenTtlDays, cancellationToken);
        await _coupleRepository.SaveChangesAsync(cancellationToken);
        await change.CommitAsync(cancellationToken);

        // Notify the existing member that a partner has joined.
        var otherMember = couple.Members
            .OrderBy(m => m.JoinedAtUtc)
            .ThenBy(m => m.UserId)
            .FirstOrDefault(m => m.UserId != command.UserId);
        if (otherMember is not null)
        {
            try
            {
                var notification = NotificationEvent.Create(
                    couple.Id,
                    otherMember.UserId,
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
            .OrderBy(member => member.JoinedAtUtc)
            .ThenBy(member => member.UserId)
            .Select(member => new CoupleMemberDto(member.UserId, member.User.Name, member.User.Email))
            .ToArray();

        // The joined group is now the user's active one: the access token carries it in the couple_id claim.
        var accessToken = _jwtTokenService.GenerateAccessToken(user);

        return new JoinCoupleResult(couple.Id, members, accessToken, refreshTokenRaw);
    }
}
