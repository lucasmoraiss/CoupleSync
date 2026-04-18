namespace CoupleSync.Api.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Name, string Password);
