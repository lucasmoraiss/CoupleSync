namespace CoupleSync.Application.Common.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Secret { get; set; } = string.Empty;

    public string Issuer { get; set; } = "CoupleSync";

    public string Audience { get; set; } = "CoupleSync.Mobile";

    public int AccessTokenTtlMinutes { get; set; } = 15;

    public int RefreshTokenTtlDays { get; set; } = 7;
}
