using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Couples;

public sealed class CreateCoupleCommandHandler
{
    private const int MaxJoinCodeAttempts = 20;

    private readonly ICoupleRepository _coupleRepository;
    private readonly ICoupleJoinCodeGenerator _joinCodeGenerator;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IJwtTokenService _jwtTokenService;

    public CreateCoupleCommandHandler(
        ICoupleRepository coupleRepository,
        ICoupleJoinCodeGenerator joinCodeGenerator,
        IDateTimeProvider dateTimeProvider,
        IJwtTokenService jwtTokenService)
    {
        _coupleRepository = coupleRepository;
        _joinCodeGenerator = joinCodeGenerator;
        _dateTimeProvider = dateTimeProvider;
        _jwtTokenService = jwtTokenService;
    }

    public async Task<CreateCoupleResult> HandleAsync(CreateCoupleCommand command, CancellationToken cancellationToken)
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

        var now = _dateTimeProvider.UtcNow;
        var joinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);
        var couple = Couple.Create(joinCode, now);
        couple.AddMember(user, now);

        await _coupleRepository.AddCoupleAsync(couple, cancellationToken);
        await _coupleRepository.SaveChangesAsync(cancellationToken);

        // Regenerate JWT so the user's couple_id claim reflects the new couple membership.
        // Without this, subsequent authenticated requests would fail COUPLE_REQUIRED checks
        // until the user logs in again.
        var accessToken = _jwtTokenService.GenerateAccessToken(user);

        return new CreateCoupleResult(couple.Id, couple.JoinCode, accessToken);
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

        throw new AppException("COUPLE_CODE_GENERATION_FAILED", "Unable to generate a unique join code.", 500);
    }
}
