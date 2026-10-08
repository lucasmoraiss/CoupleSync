using CoupleSync.Application.Ai;
using CoupleSync.Application.AiChat;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.AzureDocumentIntelligence;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Integrations.Email;
using CoupleSync.Infrastructure.Integrations.Fcm;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Integrations.Llm;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using CoupleSync.Infrastructure.Integrations.Storage;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Security;
using CoupleSync.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = DatabaseConnectionResolver.Resolve(configuration);

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<ICoupleRepository, CoupleRepository>();
        services.AddScoped<INotificationCaptureRepository, NotificationCaptureRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<ICategoryRuleRepository, CategoryRuleRepository>();
        services.AddScoped<IGoalRepository, GoalRepository>();
        services.AddScoped<IBudgetRepository, BudgetRepository>();
        services.AddScoped<IIncomeSourceRepository, IncomeSourceRepository>();
        services.AddScoped<ICashFlowRepository, CashFlowRepository>();
        services.AddScoped<IDeviceTokenRepository, DeviceTokenRepository>();
        services.AddScoped<INotificationSettingsRepository, NotificationSettingsRepository>();
        services.AddScoped<INotificationEventRepository, NotificationEventRepository>();
        services.AddScoped<IImportJobRepository, ImportJobRepository>();
        services.AddScoped<IReportsRepository, ReportsRepository>();
        services.AddScoped<IBankConnectionRepository, BankConnectionRepository>();
        services.AddScoped<IBankSyncRepository, BankSyncRepository>();
        services.AddScoped<ICategoryMatchingService, CategoryMatchingService>();
        services.AddScoped<ICoupleContext, HttpContextCoupleContext>();
        services.AddScoped<ICoupleMembership, CoupleMembership>();
        services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<ITokenHasher, Sha256TokenHasher>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<ICoupleJoinCodeGenerator, CryptoCoupleJoinCodeGenerator>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<INotificationEventSanitizer, NotificationEventSanitizer>();
        services.AddSingleton<IFingerprintGenerator, TransactionFingerprintGenerator>();

        services.AddSingleton<IVerificationCodeService, VerificationCodeService>();
        services.AddEmailSending(configuration);

        services.Configure<FcmOptions>(configuration.GetSection("Fcm"));
        services.AddSingleton<IFcmAdapter, FcmAdapter>();
        services.AddHostedService<NotificationDispatcherJob>();

        services.AddScoped<IStorageAdapter, LocalFileStorageAdapter>();

        services.AddScoped<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddScoped<BankDetector>();

        // Bank statement parsers — registered as IEnumerable<IBankStatementParser> via multiple scoped registrations
        services.AddScoped<IBankStatementParser, NubankParser>();
        services.AddScoped<IBankStatementParser, InterBankParser>();
        services.AddScoped<IBankStatementParser, BancoBrasilParser>();
        services.AddScoped<IBankStatementParser, ItauParser>();
        services.AddScoped<IBankStatementParser, SantanderParser>();
        services.AddScoped<IBankStatementParser, CaixaParser>();
        services.AddScoped<IBankStatementParser, MercantilParser>();

        services.AddHttpClient("AzureDocumentIntelligence", c => c.Timeout = TimeSpan.FromSeconds(15));

        var useLocalPdf = configuration.GetValue<bool>("USE_LOCAL_PDF_PARSER", true);
        if (useLocalPdf)
            services.AddScoped<IOcrProvider, LocalPdfParserProvider>();
        else
            services.AddScoped<IOcrProvider, AzureDocumentIntelligenceAdapter>();

        services.AddScoped<OcrProcessingService>();
        services.AddHostedService<OcrBackgroundJob>();

        // AI Chat (feature-flagged — registered always so DI resolves, controller checks flag at runtime)
        services.Configure<GeminiOptions>(configuration.GetSection("Gemini"));
        // Apply GEMINI_MODEL env var override
        var geminiModel = configuration["GEMINI_MODEL"];
        if (!string.IsNullOrWhiteSpace(geminiModel))
        {
            services.PostConfigure<GeminiOptions>(opts => opts.Model = geminiModel);
        }
        // Apply GEMINI_API_KEY env var override
        var geminiApiKey = configuration["GEMINI_API_KEY"];
        if (!string.IsNullOrWhiteSpace(geminiApiKey))
        {
            services.PostConfigure<GeminiOptions>(opts => opts.ApiKey = geminiApiKey);
        }
        // Apply AI_CHAT_ENABLED env var override
        var aiChatEnabled = configuration["AI_CHAT_ENABLED"];
        if (!string.IsNullOrWhiteSpace(aiChatEnabled))
        {
            services.PostConfigure<GeminiOptions>(opts =>
                opts.Enabled = string.Equals(aiChatEnabled, "true", StringComparison.OrdinalIgnoreCase));
        }
        services.AddHttpClient("Gemini", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IGeminiAdapter, GeminiChatAdapter>();
        services.AddSingleton<ChatRateLimiter>();

        // AI auto-categorization: use real classifier only when AI_CHAT_ENABLED=true
        if (!string.IsNullOrWhiteSpace(aiChatEnabled) &&
            string.Equals(aiChatEnabled, "true", StringComparison.OrdinalIgnoreCase))
            services.AddScoped<ICategoryClassifier, GeminiCategoryClassifier>();
        else
            services.AddScoped<ICategoryClassifier, NullCategoryClassifier>();

        AddOpenFinance(services);
        AddAiGateway(services, configuration);

        return services;
    }

    /// <summary>
    /// The chain every AI call goes through (issue #37). Options are read from the final configuration when first
    /// used; a provider without a key is in no chain, so with no key at all nothing is ever sent.
    /// </summary>
    private static void AddAiGateway(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>().Configure<IConfiguration>(AiConfiguration.Apply);
        services.AddOptions<LlmProvidersOptions>()
            .Configure<IConfiguration, Microsoft.Extensions.Options.IOptions<GeminiOptions>>(
                (options, config, gemini) => AiConfiguration.Apply(options, config, gemini.Value));

        // One named client per provider. Keys travel in headers: they must not reach the log even with header
        // logging (Trace) on. The time of each call is decided by the gateway; this is only a ceiling above it.
        services.AddHttpClient(GeminiLlmProvider.HttpClientName, c => c.Timeout = LlmHttpTimeout)
            .RedactLoggedHeaders([GeminiLlmProvider.ApiKeyHeader]);
        foreach (var provider in AiConfiguration.CompatibleProviders(configuration))
        {
            services.AddHttpClient(OpenAiCompatibleLlmProvider.HttpClientNameOf(provider.Name), c => c.Timeout = LlmHttpTimeout)
                .RedactLoggedHeaders(["Authorization"]);
        }

        services.AddSingleton<LlmMinuteWindow>();
        services.AddSingleton<ILlmWaiter, SystemLlmWaiter>();
        services.AddScoped<IAiConsentGate, DeviceAiConsentGate>();
        services.AddScoped<ILlmProviderCatalog, LlmProviderCatalog>();
        services.AddScoped<IAiUsageRepository, AiUsageRepository>();
        services.AddScoped<IAiPeopleReader, AiPeopleReader>();
        services.AddScoped<ILlmGateway, LlmGateway>();
    }

    private static readonly TimeSpan LlmHttpTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Open Finance through Meu Pluggy. Registered always so that DI resolves; without a valid
    /// OPENFINANCE_ENCRYPTION_KEY the cipher reports "unavailable" and the routes answer accordingly.
    /// </summary>
    private static void AddOpenFinance(IServiceCollection services)
    {
        // Read from the final configuration when first used (environment variables and test overrides included).
        // The key comes only from its own variable, never from the OpenFinance section of a settings file.
        services.AddOptions<OpenFinanceOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                var baseUrl = config[$"{OpenFinanceOptions.SectionName}:{nameof(OpenFinanceOptions.PluggyBaseUrl)}"];
                if (!string.IsNullOrWhiteSpace(baseUrl)) options.PluggyBaseUrl = baseUrl.Trim();
                options.EncryptionKey = config[OpenFinanceOptions.EncryptionKeyVariable] ?? string.Empty;
                if (TryReadSeconds(config, nameof(OpenFinanceOptions.SyncPollSeconds), out var poll) && poll > 0)
                    options.SyncPollSeconds = poll;
                if (TryReadSeconds(config, nameof(OpenFinanceOptions.SchedulerTickSeconds), out var tick))
                    options.SchedulerTickSeconds = tick;
            });

        services.AddSingleton<ICredentialCipher, AesGcmCredentialCipher>();
        services.AddMemoryCache();
        services.AddHttpClient(PluggyHttpClient.HttpClientName, c => c.Timeout = PluggyHttpClient.RequestTimeout)
            // The API key travels in a header: it must not reach the log even with header logging (Trace) on...
            .RedactLoggedHeaders([PluggyHttpClient.ApiKeyHeader])
            // ...nor follow a redirect to another address: a 3xx is an answer Pluggy does not give, and it fails.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<IPluggyClient, PluggyHttpClient>();

        // The synchronisation queue and the daily scheduler. Both only ever read the queue and the clock: with no
        // key on the server every run ends as failed with a clear error, and nothing here stops the API from starting.
        services.AddHostedService<OpenFinanceSyncJob>();
        services.AddHostedService<OpenFinanceDailyScheduler>();
    }

    /// <summary>A number of seconds of the OpenFinance section, written with a dot whatever the culture of the host.</summary>
    private static bool TryReadSeconds(IConfiguration config, string name, out double seconds)
        => double.TryParse(
            config[$"{OpenFinanceOptions.SectionName}:{name}"],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out seconds);
}
