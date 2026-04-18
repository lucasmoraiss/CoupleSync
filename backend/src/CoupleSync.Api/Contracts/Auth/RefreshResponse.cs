namespace CoupleSync.Api.Contracts.Auth;

public sealed record RefreshResponse(string AccessToken, string? RefreshToken);
