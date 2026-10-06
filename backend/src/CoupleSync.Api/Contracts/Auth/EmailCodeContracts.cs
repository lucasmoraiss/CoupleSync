namespace CoupleSync.Api.Contracts.Auth;

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Code, string NewPassword);

public sealed record ConfirmEmailRequest(string Code);

/// <summary>Same answer for every address, registered or not.</summary>
public sealed record ForgotPasswordResponse(string Message);

public sealed record CurrentUserResponse(Guid Id, string Email, string Name, bool EmailVerified);
