namespace CoupleSync.Application.Auth;

public sealed record AuthenticatedUserDto(Guid Id, string Email, string Name);

public sealed record AuthResult(AuthenticatedUserDto User, string AccessToken, string RefreshToken);
