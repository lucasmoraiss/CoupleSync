using CoupleSync.Application.AiChat;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.AzureDocumentIntelligence;
using CoupleSync.Infrastructure.Integrations.Fcm;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;
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
        services.AddScoped<ICashFlowRepository, CashFlowRepository>();
        services.AddScoped<IDeviceTokenRepository, DeviceTokenRepository>();
        services.AddScoped<INotificationSettingsRepository, NotificationSettingsRepository>();
        services.AddScoped<INotificationEventRepository, NotificationEventRepository>();
        services.AddScoped<IImportJobRepository, ImportJobRepository>();
        services.AddScoped<IReportsRepository, ReportsRepository>();
        services.AddScoped<ICategoryMatchingService, CategoryMatchingService>();
        services.AddScoped<ICoupleContext, HttpContextCoupleContext>();
        services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<ITokenHasher, Sha256TokenHasher>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<ICoupleJoinCodeGenerator, CryptoCoupleJoinCodeGenerator>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<INotificationEventSanitizer, NotificationEventSanitizer>();
        services.AddSingleton<IFingerprintGenerator, TransactionFingerprintGenerator>();

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

        return services;
    }
}
