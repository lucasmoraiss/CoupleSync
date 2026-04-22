using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Couples;

public sealed class JoinCoupleCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;

    public JoinCoupleCommandHandler(
        ICoupleRepository coupleRepository,
        IDateTimeProvider dateTimeProvider,
        IJwtTokenService jwtTokenService)
    {
        _coupleRepository = coupleRepository;
        _dateTimeProvider = dateTimeProvider;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<JoinCoupleResult> HandleAsync(JoinCoupleCommand command, CancellationToken cancellationToken)
    {
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired session.");
        }

        if (user.CoupleId.HasValue)
        {
            throw new ConflictException("USER_ALREADY_IN_COUPLE", "User is already in a couple.");
        }

        var normalizedJoinCode = command.JoinCode.Trim().ToUpperInvariant();
        var couple = await _coupleRepository.FindByJoinCodeAsync(normalizedJoinCode, cancellationToken);

        if (couple is null)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Couple was not found.");
        }

        if (couple.Members.Count >= 2)
        {
            throw new ConflictException("COUPLE_FULL", "Couple already has two members.");
        }

        var now = _dateTimeProvider.UtcNow;
        couple.AddMember(user, now);
        await _coupleRepository.SaveChangesAsync(cancellationToken);

        var members = couple.Members
            .Select(member => new CoupleMemberDto(member.Id, member.Name, member.Email))
            .ToArray();

        // Regenerate JWT so the user's couple_id claim is populated immediately.
        // Without this, the app stays in a "no couple" state until re-login.
        var accessToken = _jwtTokenService.GenerateAccessToken(user);

        return new JoinCoupleResult(couple.Id, members, accessToken);
    }
}
