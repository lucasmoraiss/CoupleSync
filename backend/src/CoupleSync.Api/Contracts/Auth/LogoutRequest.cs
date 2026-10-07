namespace CoupleSync.Api.Contracts.Auth;

/// <param name="DeviceToken">Optional push token of this device; when it belongs to the user being signed out it is unregistered.</param>
public sealed record LogoutRequest(string RefreshToken, string? DeviceToken = null);
