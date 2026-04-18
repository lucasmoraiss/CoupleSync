namespace CoupleSync.Application.Auth;

public sealed record RefreshTokenResult(string AccessToken, string? RefreshToken);
