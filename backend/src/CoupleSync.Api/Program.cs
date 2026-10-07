using CoupleSync.Application.AiChat;
using System.Text;
using CoupleSync.Api.Errors;
using CoupleSync.Api.Health;
using CoupleSync.Api.Middleware;
using CoupleSync.Api.RateLimiting;
using CoupleSync.Api.Security;
using CoupleSync.Api.Serialization;
using CoupleSync.Api.Validators;
using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Couples;
using CoupleSync.Application.Common.Options;
using CoupleSync.Application.NotificationCapture;
using Microsoft.EntityFrameworkCore;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Application.Transactions.Queries;
using CoupleSync.Application.Notification.Commands;
using CoupleSync.Application.Notification.Queries;
using CoupleSync.Application.Goals.Commands;
using CoupleSync.Application.Goals.Queries;
using CoupleSync.Application.Goals;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Income;
using CoupleSync.Application.Dashboard;
using CoupleSync.Application.CashFlow.Queries;
using CoupleSync.Application.Notification;
using CoupleSync.Application.OcrImport;
using CoupleSync.Application.Reports;
using CoupleSync.Infrastructure;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.Infrastructure.Persistence.Seeders;
using CoupleSync.Infrastructure.Security;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

// The API executable is also the PDF reading worker: started by ChildProcessPdfTextExtractor with this first argument
// it reads one PDF from the standard input, answers on the standard output and ends, without building the web host.
if (args.Length > 0 && args[0] == CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker.PdfWorkerHost.Command)
    return CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker.PdfWorkerHost.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
JwtSecretGuard.EnsureValid(jwtOptions.Secret);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddScoped<RegisterCommandHandler>();
builder.Services.AddScoped<LoginCommandHandler>();
builder.Services.AddScoped<RefreshTokenCommandHandler>();
builder.Services.AddScoped<LogoutCommandHandler>();
builder.Services.AddScoped<ChangePasswordCommandHandler>();
builder.Services.AddScoped<EmailCodeFlow>();
builder.Services.AddScoped<RequestPasswordResetCommandHandler>();
builder.Services.AddScoped<ResetPasswordCommandHandler>();
builder.Services.AddScoped<ConfirmEmailCommandHandler>();
builder.Services.AddScoped<ResendEmailVerificationCommandHandler>();
builder.Services.AddScoped<GetCurrentUserQueryHandler>();
builder.Services.AddScoped<CreateCoupleCommandHandler>();
builder.Services.AddScoped<JoinCoupleCommandHandler>();
builder.Services.AddScoped<LeaveCoupleCommandHandler>();
builder.Services.AddScoped<SwitchCoupleCommandHandler>();
builder.Services.AddScoped<GetMyGroupsQueryHandler>();
builder.Services.AddScoped<RemoveCoupleMemberCommandHandler>();
builder.Services.AddScoped<RegenerateJoinCodeCommandHandler>();
builder.Services.AddScoped<GetCoupleMeQueryHandler>();
builder.Services.AddScoped<IngestNotificationEventCommandHandler>();
builder.Services.AddScoped<GetIntegrationStatusQueryHandler>();
builder.Services.AddScoped<GetTransactionsQueryHandler>();
builder.Services.AddScoped<UpdateTransactionCategoryCommandHandler>();
builder.Services.AddScoped<LinkTransactionToGoalCommandHandler>();
builder.Services.AddScoped<CreateManualTransactionCommandHandler>();
builder.Services.AddScoped<DeleteTransactionCommandHandler>();
builder.Services.AddScoped<UpdateTransactionCommandHandler>();
builder.Services.AddScoped<GetDashboardQueryHandler>();
builder.Services.AddScoped<CreateGoalCommandHandler>();
builder.Services.AddScoped<UpdateGoalCommandHandler>();
builder.Services.AddScoped<DeleteGoalCommandHandler>();
builder.Services.AddScoped<ArchiveGoalCommandHandler>();
builder.Services.AddScoped<GetGoalsQueryHandler>();
builder.Services.AddScoped<GetGoalByIdQueryHandler>();
builder.Services.AddScoped<GetGoalProgressQueryHandler>();
builder.Services.AddScoped<GetGoalsProgressSummaryQueryHandler>();
builder.Services.AddScoped<IGoalProgressService, GoalProgressService>();
builder.Services.AddScoped<GoalProgressReader>();
builder.Services.AddScoped<GetCashFlowQueryHandler>();
builder.Services.AddScoped<RegisterDeviceTokenCommandHandler>();
builder.Services.AddScoped<UpdateNotificationSettingsCommandHandler>();
builder.Services.AddScoped<GetNotificationSettingsQueryHandler>();
builder.Services.AddScoped<IAlertPolicyService, AlertPolicyService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<IncomeService>();
builder.Services.AddScoped<ImportJobService>();
builder.Services.AddScoped<ReportsService>();
builder.Services.AddScoped<ChatContextService>();
builder.Services.AddScoped<GeminiChatService>();

ValidationLocalization.Configure();
builder.Services.AddControllers(ValidationLocalization.ConfigureModelBinding)
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()))
    // Binding/validation failures (400 of [ApiController]) use the same error format as everything else.
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create);
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddCoupleSyncRateLimiting(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Auto-apply pending EF Core migrations on startup — production (PostgreSQL) only.
    // Integration tests use SQLite with EnsureCreated(), so migrations are skipped there
    // to avoid "table already exists" conflicts.
    if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) != true)
    {
        await db.Database.MigrateAsync();
    }
    var seeder = new CategoryRulesSeeder(db);
    await seeder.SeedAsync();
}

// Must run first so that Connection.RemoteIpAddress is the real client behind the reverse proxy
// (only applied for proxies listed in the ForwardedHeaders configuration section).
app.UseForwardedHeaders();

app.UseMiddleware<GlobalExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
}

app.UseAuthentication();
// After authentication (the couples/join policy is partitioned by user) and before authorization.
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Name == "database"
});

app.Run();
return 0;

public partial class Program;
