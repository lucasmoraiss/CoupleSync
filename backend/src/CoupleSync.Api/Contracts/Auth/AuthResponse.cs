namespace CoupleSync.Api.Contracts.Auth;

public sealed record AuthUserResponse(Guid Id, string Email, string Name);

public sealed record AuthResponse(AuthUserResponse User, string AccessToken, string RefreshToken);
