using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using CoupleSync.Api.Errors;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CoupleSync.Api.RateLimiting;

/// <summary>Names of the rate-limiting policies applied with <c>[EnableRateLimiting]</c>.</summary>
public static class RateLimitPolicies
{
    public const string AuthLogin = "auth-login";
    public const string AuthRegister = "auth-register";
    public const string CoupleJoin = "couple-join";
    public const string AuthChangePassword = "auth-change-password";
    public const string AuthLogout = "auth-logout";
    public const string AuthRefresh = "auth-refresh";
    public const string AuthForgotPassword = "auth-forgot-password";
    public const string AuthResetPassword = "auth-reset-password";
    public const string AuthConfirmEmail = "auth-confirm-email";
    public const string AuthResendEmailVerification = "auth-resend-email-verification";
    public const string OpenFinanceCredentials = "openfinance-credentials";
    public const string AppVersion = "app-version";
}

/// <summary>Bound to the <c>RateLimiting</c> configuration section (env: <c>RATELIMITING__AUTH__PERMITLIMIT</c> etc.).</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>POST /auth/login and POST /auth/register — per client IP, one budget per endpoint.</summary>
    public FixedWindowSettings Auth { get; set; } = new();

    /// <summary>POST /couples/join — per authenticated user.</summary>
    public FixedWindowSettings CoupleJoin { get; set; } = new();

    /// <summary>
    /// POST /auth/refresh — per client IP. Far above normal use (an app refreshes about every 15 minutes per
    /// device, and several people may share one address) and still a ceiling for someone guessing tokens.
    /// </summary>
    public FixedWindowSettings Refresh { get; set; } = new() { PermitLimit = 60 };

    /// <summary>
    /// The Open Finance routes that send credentials to Pluggy (test and connect) — per authenticated user, one
    /// budget for both, so that this API cannot be used to try client secrets freely.
    /// </summary>
    public FixedWindowSettings OpenFinance { get; set; } = new();

    /// <summary>
    /// GET /app/version — per client IP. Anonymous and asked by every installed app when it opens (several phones
    /// may share one address); the answer comes from memory, so the ceiling only stops someone hammering the route.
    /// </summary>
    public FixedWindowSettings AppVersion { get; set; } = new() { PermitLimit = 30 };
}

public sealed class FixedWindowSettings
{
    public int PermitLimit { get; set; } = 5;

    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimitingSetup
{
    public const string ForwardedHeadersSectionName = "ForwardedHeaders";

    public static IServiceCollection AddCoupleSyncRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRejectionAsync;

            limiter.AddPolicy(RateLimitPolicies.AuthLogin, context =>
                CreatePartition($"ip:{GetClientIp(context)}", GetOptions(context).Auth));

            // Own bucket: signing out must not eat the login budget (and vice versa).
            limiter.AddPolicy(RateLimitPolicies.AuthLogout, context =>
                CreatePartition($"ip:{GetClientIp(context)}", GetOptions(context).Auth));

            // Anonymous like login, but called by every signed-in app in the background: its own, larger budget.
            limiter.AddPolicy(RateLimitPolicies.AuthRefresh, context =>
                CreatePartition($"refresh:ip:{GetClientIp(context)}", GetOptions(context).Refresh));

            limiter.AddPolicy(RateLimitPolicies.AuthRegister, context =>
                CreatePartition($"ip:{GetClientIp(context)}", GetOptions(context).Auth));

            // The current password is checked here, so a stolen access token must not be able to guess it freely.
            limiter.AddPolicy(RateLimitPolicies.AuthChangePassword, context =>
            {
                var userId = context.User.FindFirstValue("user_id");
                var key = string.IsNullOrWhiteSpace(userId) ? $"ip:{GetClientIp(context)}" : $"user:{userId}";
                return CreatePartition($"change-password:{key}", GetOptions(context).Auth);
            });

            // Password reset: per client IP, one bucket per route (the per-address cap lives in CodeRequestThrottle).
            limiter.AddPolicy(RateLimitPolicies.AuthForgotPassword, context =>
                CreatePartition($"forgot-password:ip:{GetClientIp(context)}", GetOptions(context).Auth));

            limiter.AddPolicy(RateLimitPolicies.AuthResetPassword, context =>
                CreatePartition($"reset-password:ip:{GetClientIp(context)}", GetOptions(context).Auth));

            // E-mail confirmation belongs to the signed-in user: per user, falling back to the IP.
            limiter.AddPolicy(RateLimitPolicies.AuthConfirmEmail, context =>
                CreatePartition($"confirm-email:{UserOrIpKey(context)}", GetOptions(context).Auth));

            limiter.AddPolicy(RateLimitPolicies.AuthResendEmailVerification, context =>
                CreatePartition($"resend-email-verification:{UserOrIpKey(context)}", GetOptions(context).Auth));

            limiter.AddPolicy(RateLimitPolicies.OpenFinanceCredentials, context =>
                CreatePartition($"openfinance-credentials:{UserOrIpKey(context)}", GetOptions(context).OpenFinance));

            limiter.AddPolicy(RateLimitPolicies.AppVersion, context =>
                CreatePartition($"app-version:ip:{GetClientIp(context)}", GetOptions(context).AppVersion));

            limiter.AddPolicy(RateLimitPolicies.CoupleJoin, context =>
            {
                // Runs after authentication. Anonymous callers are rejected by [Authorize] anyway,
                // but they still get an IP-scoped bucket so they cannot starve a real user.
                var userId = context.User.FindFirstValue("user_id");
                var key = string.IsNullOrWhiteSpace(userId) ? $"ip:{GetClientIp(context)}" : $"user:{userId}";
                return CreatePartition(key, GetOptions(context).CoupleJoin);
            });
        });

        // Options are resolved lazily from the final IConfiguration so that environment variables
        // (and test overrides) are honoured.
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, config) => ConfigureForwardedHeaders(options, config));

        return services;
    }

    /// <summary>
    /// X-Forwarded-For is only honoured when the request comes from a proxy we were told to trust.
    /// With nothing configured the framework default applies (loopback only), which is the safe
    /// choice: an untrusted caller cannot pick its own rate-limit bucket by forging the header.
    /// </summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        var section = configuration.GetSection(ForwardedHeadersSectionName);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = section.GetValue<int?>("ForwardLimit") ?? 1;

        // Behind a CDN the hosting proxy appends to X-Forwarded-For, so its last entry is an edge
        // address that changes between requests. When the CDN provides a single-valued header with
        // the real client address (e.g. CF-Connecting-IP on Cloudflare), read the client from it.
        var clientIpHeader = section["ClientIpHeader"];
        if (!string.IsNullOrWhiteSpace(clientIpHeader))
            options.ForwardedForHeaderName = clientIpHeader.Trim();

        foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(proxy)) continue;
            if (!IPAddress.TryParse(proxy.Trim(), out var address))
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains an invalid IP address: '{proxy}'.");
            options.KnownProxies.Add(address);
        }

        foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(network)) continue;
            var parts = network.Trim().Split('/');
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var prefix)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains an invalid CIDR range: '{network}'.");
            }

            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
        }

        // Opt-in for platforms where the container is reachable ONLY through the provider's
        // reverse proxy and its address is not stable (e.g. PaaS ingress).
        if (section.GetValue<bool>("TrustAllProxies"))
        {
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
        }
    }

    private static string UserOrIpKey(HttpContext context)
    {
        var userId = context.User.FindFirstValue("user_id");
        return string.IsNullOrWhiteSpace(userId) ? $"ip:{GetClientIp(context)}" : $"user:{userId}";
    }

    private static RateLimitingOptions GetOptions(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

    private static RateLimitPartition<string> CreatePartition(string key, FixedWindowSettings settings)
        => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, settings.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds)),
            QueueLimit = 0,
            AutoReplenishment = true
        });

    private static string GetClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return "unknown";
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        if (response.HasStarted) return;

        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var (code, message) = ApiErrors.DescribeStatus(StatusCodes.Status429TooManyRequests);
        await ApiErrors.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, code, message,
            cancellationToken: cancellationToken);
    }
}
