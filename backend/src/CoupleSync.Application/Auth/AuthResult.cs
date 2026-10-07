namespace CoupleSync.Application.Auth;

public sealed record AuthenticatedUserDto(Guid Id, string Email, string Name, bool EmailVerified = false);

public sealed record AuthResult(AuthenticatedUserDto User, string AccessToken, string RefreshToken);
