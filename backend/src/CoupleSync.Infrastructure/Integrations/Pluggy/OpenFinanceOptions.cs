namespace CoupleSync.Infrastructure.Integrations.Pluggy;

public sealed class OpenFinanceOptions
{
    public const string SectionName = "OpenFinance";

    /// <summary>The environment variable (and configuration key) that holds the encryption key. Created only by the owner.</summary>
    public const string EncryptionKeyVariable = "OPENFINANCE_ENCRYPTION_KEY";

    public const string DefaultPluggyBaseUrl = "https://api.pluggy.ai";

    /// <summary>Base address of the Pluggy API (configuration <c>OpenFinance:PluggyBaseUrl</c>).</summary>
    public string PluggyBaseUrl { get; set; } = DefaultPluggyBaseUrl;

    /// <summary>
    /// 32 random bytes in Base64, read only from <see cref="EncryptionKeyVariable"/> (never from the
    /// <c>OpenFinance</c> section, so it cannot end up in an appsettings file). Empty: the feature is unavailable.
    /// There is no default and the code never generates one.
    /// </summary>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>Seconds between two looks of the synchronisation job at its queue (configuration <c>OpenFinance:SyncPollSeconds</c>).</summary>
    public double SyncPollSeconds { get; set; } = 5;

    /// <summary>
    /// Seconds between two looks of the daily scheduler at the clock (configuration
    /// <c>OpenFinance:SchedulerTickSeconds</c>). Zero or less: the scheduler does not run.
    /// </summary>
    public double SchedulerTickSeconds { get; set; } = 60;
}
