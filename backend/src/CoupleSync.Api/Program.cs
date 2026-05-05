using CoupleSync.Application.AiChat;
using System.Text;
using CoupleSync.Api.Health;
using CoupleSync.Api.Middleware;
using CoupleSync.Api.Validators;
using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Couples;
using CoupleSync.Application.Common.Options;
using CoupleSync.Application.NotificationCapture;
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

var builder = WebApplication.CreateBuilder(args);
const string JwtSecretPlaceholder = "REPLACE_WITH_ENV_JWT_SECRET_32CHARS_MIN";

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.Secret)
    || jwtOptions.Secret.Length < 32
    || string.Equals(jwtOptions.Secret, JwtSecretPlaceholder, StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Invalid JWT secret configuration. Configure Jwt:Secret (or JWT__SECRET) with a non-placeholder value and at least 32 characters.");
}

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddScoped<RegisterCommandHandler>();
builder.Services.AddScoped<LoginCommandHandler>();
builder.Services.AddScoped<RefreshTokenCommandHandler>();
builder.Services.AddScoped<CreateCoupleCommandHandler>();
builder.Services.AddScoped<JoinCoupleCommandHandler>();
builder.Services.AddScoped<GetCoupleMeQueryHandler>();
builder.Services.AddScoped<IngestNotificationEventCommandHandler>();
builder.Services.AddScoped<GetIntegrationStatusQueryHandler>();
builder.Services.AddScoped<GetTransactionsQueryHandler>();
builder.Services.AddScoped<UpdateTransactionCategoryCommandHandler>();
builder.Services.AddScoped<LinkTransactionToGoalCommandHandler>();
builder.Services.AddScoped<CreateManualTransactionCommandHandler>();
builder.Services.AddScoped<DeleteTransactionCommandHandler>();
builder.Services.AddScoped<GetDashboardQueryHandler>();
builder.Services.AddScoped<CreateGoalCommandHandler>();
builder.Services.AddScoped<UpdateGoalCommandHandler>();
builder.Services.AddScoped<ArchiveGoalCommandHandler>();
builder.Services.AddScoped<GetGoalsQueryHandler>();
builder.Services.AddScoped<GetGoalByIdQueryHandler>();
builder.Services.AddScoped<GetGoalProgressQueryHandler>();
builder.Services.AddScoped<IGoalProgressService, GoalProgressService>();
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

builder.Services.AddControllers();
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var seeder = new CategoryRulesSeeder(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    await seeder.SeedAsync();
}

app.UseMiddleware<GlobalExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
}

app.UseAuthentication();
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

public partial class Program;
