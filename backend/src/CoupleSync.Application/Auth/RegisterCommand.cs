namespace CoupleSync.Application.Auth;

public sealed record RegisterCommand(string Email, string Name, string Password);
