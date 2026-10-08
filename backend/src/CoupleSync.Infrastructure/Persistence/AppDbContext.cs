using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using ICoupleScoped = CoupleSync.Domain.Interfaces.ICoupleScoped;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    private readonly ICoupleContext? _coupleContext;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICoupleContext? coupleContext = null)
        : base(options)
    {
        _coupleContext = coupleContext;
    }

    // THIS PROPERTY IS INTENTIONAL — EF Core query filter expressions must
    // capture 'this' (the DbContext instance) to evaluate lazily per-query.
    // Do NOT inline _coupleContext?.CoupleId directly into HasQueryFilter.
    private Guid? CurrentCoupleId => _coupleContext?.CoupleId;

    public DbSet<User> Users => Set<User>();

    public DbSet<Couple> Couples => Set<Couple>();

    public DbSet<CoupleMember> CoupleMembers => Set<CoupleMember>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();

    public DbSet<TransactionEventIngest> TransactionEventIngests => Set<TransactionEventIngest>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    public DbSet<Goal> Goals => Set<Goal>();

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();

    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();

    public DbSet<BudgetPlan> BudgetPlans => Set<BudgetPlan>();

    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    public DbSet<IncomeSource> IncomeSources => Set<IncomeSource>();

    public DbSet<BankConnection> BankConnections => Set<BankConnection>();

    public DbSet<BankItem> BankItems => Set<BankItem>();

    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();

    public DbSet<AiUsage> AiUsages => Set<AiUsage>();

    // BudgetAllocation is NOT exposed as a top-level DbSet.
    // All allocation access must go through BudgetPlan.Allocations navigation
    // to ensure couple-level data isolation via ICoupleScoped query filter on BudgetPlan.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            // The active group (a pointer; membership lives in couple_members). Column names predate that table.
            entity.Property(x => x.ActiveCoupleId).HasColumnName("couple_id");
            entity.Property(x => x.ActiveCoupleJoinedAtUtc).HasColumnName("couple_joined_at_utc");
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            entity.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            entity.Property(x => x.EmailVerified).HasColumnName("email_verified").IsRequired().HasDefaultValue(false);

            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.ActiveCoupleId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.ActiveCoupleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Couple>(entity =>
        {
            entity.ToTable("couples");

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.JoinCode).HasColumnName("join_code").HasMaxLength(8).IsRequired();
            entity.Property(x => x.JoinCodeExpiresAtUtc).HasColumnName("join_code_expires_at_utc").IsRequired();
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at").IsRequired();

            entity.HasIndex(x => x.JoinCode).IsUnique();

            // ClientCascade: a member taken out of the collection is deleted by EF (the row IS the membership);
            // in the database the key stays restrictive, like every other key (a group with members cannot be deleted).
            entity.HasMany(x => x.Members)
                .WithOne()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.ClientCascade);
        });

        modelBuilder.Entity<CoupleMember>(entity =>
        {
            entity.ToTable("couple_members");

            // One row per user and group: this row is the membership.
            entity.HasKey(x => new { x.CoupleId, x.UserId });
            entity.Property(x => x.CoupleId).HasColumnName("couple_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Role)
                .HasColumnName("role")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(x => x.JoinedAtUtc).HasColumnName("joined_at_utc").IsRequired();

            entity.HasIndex(x => x.UserId);

            // At most one owner per group, guaranteed by the database.
            entity.HasIndex(x => x.CoupleId)
                .IsUnique()
                .HasFilter("role = 'Owner'")
                .HasDatabaseName("IX_couple_members_one_owner_per_couple");

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasIndex(x => x.TokenHash).IsUnique();

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailCode>(entity =>
        {
            entity.ToTable("email_codes");

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(32).IsRequired();
            entity.Property(x => x.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
            entity.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.Attempts).HasColumnName("attempts").IsRequired();
            entity.Property(x => x.IssueWindowStartedAtUtc).HasColumnName("issue_window_started_at_utc").IsRequired();
            entity.Property(x => x.IssueCount).HasColumnName("issue_count").IsRequired();

            // One live code per user and purpose: a new request replaces the previous one.
            entity.HasIndex(x => new { x.UserId, x.Purpose }).IsUnique();

            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TransactionEventIngest>(entity =>
        {
            entity.ToTable("transaction_event_ingests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.Bank).HasColumnName("bank").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.EventTimestamp).HasColumnName("event_timestamp_utc").IsRequired();
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            entity.Property(x => x.Merchant).HasColumnName("merchant").HasMaxLength(512);
            entity.Property(x => x.RawNotificationTextRedacted).HasColumnName("raw_notification_text_redacted").HasMaxLength(512);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(512);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            entity.HasIndex(x => x.CoupleId);
            entity.HasIndex(x => new { x.CoupleId, x.EventTimestamp });
            entity.HasIndex(x => new { x.CoupleId, x.CreatedAtUtc });

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.ToTable("transactions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Bank).HasColumnName("bank").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.EventTimestampUtc).HasColumnName("event_timestamp_utc").IsRequired();
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            entity.Property(x => x.Merchant).HasColumnName("merchant").HasMaxLength(512);
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(64).IsRequired();
            entity.Property(x => x.IngestEventId).HasColumnName("ingest_event_id").IsRequired();
            entity.Property(x => x.GoalId).HasColumnName("goal_id");
            entity.Property(x => x.Source).HasColumnName("source").HasDefaultValue(TransactionSource.Manual).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            entity.HasIndex(x => new { x.CoupleId, x.Fingerprint }).IsUnique();
            entity.HasIndex(x => x.CoupleId);
            entity.HasIndex(x => new { x.CoupleId, x.EventTimestampUtc });
            entity.HasIndex(x => new { x.CoupleId, x.Category });

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<TransactionEventIngest>()
                .WithMany()
                .HasForeignKey(x => x.IngestEventId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Goal>()
                .WithMany()
                .HasForeignKey(x => x.GoalId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        modelBuilder.Entity<CategoryRule>(entity =>
        {
            entity.ToTable("category_rules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Keyword).HasColumnName("keyword").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Priority).HasColumnName("priority").IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();

            entity.HasIndex(x => x.Keyword).IsUnique();
        });

        modelBuilder.Entity<Goal>(entity =>
        {
            entity.ToTable("goals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            entity.Property(x => x.TargetAmount).HasColumnName("target_amount").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.CurrentAmount).HasColumnName("current_amount").HasPrecision(18, 2).IsRequired().HasDefaultValue(0m);
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.Deadline).HasColumnName("deadline").IsRequired();
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            entity.HasIndex(x => x.CoupleId);
            entity.HasIndex(x => new { x.CoupleId, x.Status });

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeviceToken>(entity =>
        {
            entity.ToTable("device_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.Token).HasColumnName("token").HasMaxLength(512).IsRequired();
            entity.Property(x => x.Platform).HasColumnName("platform").HasMaxLength(16).IsRequired();
            entity.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc").IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            entity.HasIndex(x => new { x.UserId, x.Platform }).IsUnique();
            entity.HasIndex(x => x.Token).IsUnique();
            entity.HasIndex(x => x.CoupleId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NotificationSettings>(entity =>
        {
            entity.ToTable("notification_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.LowBalanceEnabled).HasColumnName("low_balance_enabled").IsRequired();
            entity.Property(x => x.LargeTransactionEnabled).HasColumnName("large_transaction_enabled").IsRequired();
            entity.Property(x => x.BillReminderEnabled).HasColumnName("bill_reminder_enabled").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            // A user has one set of alert preferences per group they belong to.
            entity.HasIndex(x => new { x.UserId, x.CoupleId }).IsUnique();
            entity.HasIndex(x => x.CoupleId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NotificationEvent>(entity =>
        {
            entity.ToTable("notification_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.AlertType).HasColumnName("alert_type").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Title).HasColumnName("title").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Body).HasColumnName("body").HasMaxLength(512).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.DeliveredAtUtc).HasColumnName("delivered_at_utc");

            entity.HasIndex(x => new { x.CoupleId, x.Status });

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BudgetPlan>(entity =>
        {
            entity.ToTable("budget_plans");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.Month).HasColumnName("month").HasMaxLength(7).IsRequired();
            entity.Property(x => x.GrossIncome).HasColumnName("gross_income").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            entity.HasIndex(x => new { x.CoupleId, x.Month }).IsUnique();

            entity.HasOne(x => x.Couple)
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(x => x.Allocations)
                .WithOne(x => x.BudgetPlan)
                .HasForeignKey(x => x.BudgetPlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BudgetAllocation>(entity =>
        {
            entity.ToTable("budget_allocations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.BudgetPlanId).HasColumnName("budget_plan_id").IsRequired();
            entity.Property(x => x.Category).HasColumnName("category").HasMaxLength(64).IsRequired();
            entity.Property(x => x.AllocatedAmount).HasColumnName("allocated_amount").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            entity.HasIndex(x => new { x.BudgetPlanId, x.Category });
        });

        modelBuilder.Entity<IncomeSource>(entity =>
        {
            entity.ToTable("income_sources");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.Month).HasColumnName("month").HasMaxLength(7).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            entity.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            entity.Property(x => x.IsShared).HasColumnName("is_shared").IsRequired();
            entity.Property(x => x.IsRecurring).HasColumnName("is_recurring").IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            entity.HasIndex(x => new { x.CoupleId, x.Month });
            entity.HasIndex(x => new { x.UserId, x.Month });
            entity.HasIndex(x => new { x.CoupleId, x.UserId, x.Month, x.Name }).IsUnique();

            entity.HasOne(x => x.Couple)
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportJob>(entity =>
        {
            entity.ToTable("import_jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.StoragePath).HasColumnName("storage_path").HasMaxLength(1024).IsRequired();
            entity.Property(x => x.FileMimeType).HasColumnName("file_mime_type").HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired()
                .IsConcurrencyToken();
            entity.Property(x => x.OcrResultJson)
                .HasColumnName("ocr_result_json")
                .HasColumnType("jsonb");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(512);
            entity.Property(x => x.QuotaResetDate).HasColumnName("quota_reset_date");
            entity.Property(x => x.RetryCount).HasColumnName("retry_count").HasDefaultValue(0).IsRequired();
            // Status and the line states are concurrency tokens: a write made on a stale copy of the job (the worker
            // finishing a job that recovery already failed, two confirmations of different lines) is rejected.
            entity.Property(x => x.LineStatesJson).HasColumnName("line_states_json").HasColumnType("text").IsConcurrencyToken();
            entity.Property(x => x.AiCategorizationConsent).HasColumnName("ai_categorization_consent").IsRequired();
            entity.Property(x => x.SourceFileName).HasColumnName("source_file_name").HasMaxLength(ImportJob.MaxFileNameLength);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            entity.HasIndex(x => x.CoupleId);
            entity.HasIndex(x => new { x.CoupleId, x.Status });

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankConnection>(entity =>
        {
            entity.ToTable("bank_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(16).IsRequired();
            entity.Property(x => x.Label).HasColumnName("label").HasMaxLength(BankConnection.MaxLabelLength).IsRequired();
            // Encrypted (AES-256-GCM); null once the person disconnected.
            entity.Property(x => x.ClientIdEncrypted).HasColumnName("client_id_encrypted").HasMaxLength(1024);
            // A concurrency token: every encryption gives another text, so a write made on a copy read before the
            // person disconnected (or connected again) is rejected instead of bringing the old state back.
            entity.Property(x => x.ClientSecretEncrypted).HasColumnName("client_secret_encrypted").HasMaxLength(1024).IsConcurrencyToken();
            entity.Property(x => x.ClientIdHint).HasColumnName("client_id_hint").HasMaxLength(BankConnection.ClientIdHintLength);
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(x => x.LastSyncAtUtc).HasColumnName("last_sync_at_utc");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(64);
            entity.Property(x => x.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(BankConnection.MaxErrorMessageLength);
            entity.Property(x => x.HistoryMonths).HasColumnName("history_months").IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            entity.Ignore(x => x.HasCredentials);

            // One connection per person and group, guaranteed by the database.
            entity.HasIndex(x => new { x.CoupleId, x.UserId }).IsUnique();
            entity.HasIndex(x => x.UserId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankItem>(entity =>
        {
            entity.ToTable("bank_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.ConnectionId).HasColumnName("connection_id").IsRequired();
            entity.Property(x => x.PluggyItemId).HasColumnName("pluggy_item_id").HasMaxLength(BankItem.MaxPluggyIdLength).IsRequired();
            entity.Property(x => x.ConnectorName).HasColumnName("connector_name").HasMaxLength(BankItem.MaxConnectorNameLength).IsRequired();
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(BankItem.MaxStatusLength).IsRequired();
            entity.Property(x => x.ExecutionStatus).HasColumnName("execution_status").HasMaxLength(BankItem.MaxStatusLength);
            entity.Property(x => x.LastUpdatedAtUtc).HasColumnName("last_updated_at_utc");
            entity.Property(x => x.LastErrorMessage).HasColumnName("last_error_message").HasMaxLength(BankItem.MaxErrorMessageLength);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            // A Pluggy item belongs to one connection in the whole database.
            entity.HasIndex(x => x.PluggyItemId).IsUnique();
            entity.HasIndex(x => x.ConnectionId);
            entity.HasIndex(x => x.CoupleId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<BankConnection>()
                .WithMany()
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankAccount>(entity =>
        {
            entity.ToTable("bank_accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.ItemId).HasColumnName("item_id").IsRequired();
            entity.Property(x => x.PluggyAccountId).HasColumnName("pluggy_account_id").HasMaxLength(BankAccount.MaxPluggyIdLength).IsRequired();
            entity.Property(x => x.Type).HasColumnName("type").HasMaxLength(BankAccount.MaxTypeLength).IsRequired();
            entity.Property(x => x.Subtype).HasColumnName("subtype").HasMaxLength(BankAccount.MaxTypeLength);
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(BankAccount.MaxNameLength).IsRequired();
            entity.Property(x => x.MarketingName).HasColumnName("marketing_name").HasMaxLength(BankAccount.MaxNameLength);
            entity.Property(x => x.NumberMasked).HasColumnName("number_masked").HasMaxLength(BankAccount.MaskedNumberLength);
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(BankAccount.MaxCurrencyLength).IsRequired();
            entity.Property(x => x.Balance).HasColumnName("balance").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.BalanceAtUtc).HasColumnName("balance_at_utc").IsRequired();
            entity.Property(x => x.CreditLimit).HasColumnName("credit_limit").HasPrecision(18, 2);
            entity.Property(x => x.AvailableCreditLimit).HasColumnName("available_credit_limit").HasPrecision(18, 2);
            entity.Property(x => x.BalanceCloseDate).HasColumnName("balance_close_date");
            entity.Property(x => x.BalanceDueDate).HasColumnName("balance_due_date");
            entity.Property(x => x.MinimumPayment).HasColumnName("minimum_payment").HasPrecision(18, 2);
            entity.Property(x => x.Brand).HasColumnName("brand").HasMaxLength(BankAccount.MaxBrandLength);
            entity.Property(x => x.SyncEnabled).HasColumnName("sync_enabled").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            // A Pluggy account is stored once in the whole database.
            entity.HasIndex(x => x.PluggyAccountId).IsUnique();
            entity.HasIndex(x => x.ItemId);
            entity.HasIndex(x => x.CoupleId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<BankItem>()
                .WithMany()
                .HasForeignKey(x => x.ItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SyncRun>(entity =>
        {
            entity.ToTable("sync_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.ConnectionId).HasColumnName("connection_id").IsRequired();
            // A concurrency token: a run is taken (Pending → Running) and finished by whoever still sees the status it
            // read. Two workers (two instances during a deploy) never execute the same run, and a run failed by the
            // recovery of another process keeps that verdict.
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired().IsConcurrencyToken();
            entity.Property(x => x.TriggeredBy).HasColumnName("triggered_by").HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.ForceItemUpdate).HasColumnName("force_item_update").IsRequired();
            entity.Property(x => x.AiCategorizationConsent).HasColumnName("ai_categorization_consent").IsRequired();
            entity.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            entity.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");
            entity.Property(x => x.TransactionsNew).HasColumnName("transactions_new").IsRequired();
            entity.Property(x => x.TransactionsUpdated).HasColumnName("transactions_updated").IsRequired();
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(SyncRun.MaxErrorCodeLength);
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(SyncRun.MaxErrorMessageLength);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Ignore(x => x.IsOpen);

            entity.HasIndex(x => new { x.ConnectionId, x.CreatedAtUtc });
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CoupleId);
            // At most one run waiting or running per connection, guaranteed by the database: the queue is serial
            // per connection even when two requests (or a request and the scheduler) enqueue at the same moment.
            entity.HasIndex(x => x.ConnectionId, "IX_sync_runs_one_open_per_connection")
                .IsUnique()
                .HasFilter("status IN ('Pending', 'Running')");

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<BankConnection>()
                .WithMany()
                .HasForeignKey(x => x.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.ToTable("bank_transactions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CoupleId).HasColumnName("couple_id").IsRequired();
            entity.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(x => x.BankAccountId).HasColumnName("bank_account_id").IsRequired();
            entity.Property(x => x.PluggyTransactionId).HasColumnName("pluggy_transaction_id").HasMaxLength(BankTransaction.MaxPluggyIdLength).IsRequired();
            entity.Property(x => x.Date).HasColumnName("date").IsRequired();
            entity.Property(x => x.LocalDate).HasColumnName("local_date").IsRequired();
            entity.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            entity.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(BankTransaction.MaxCurrencyLength).IsRequired();
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(BankTransaction.MaxDescriptionLength);
            entity.Property(x => x.DescriptionRaw).HasColumnName("description_raw").HasMaxLength(BankTransaction.MaxDescriptionLength);
            entity.Property(x => x.PluggyCategory).HasColumnName("pluggy_category").HasMaxLength(BankTransaction.MaxCategoryLength);
            entity.Property(x => x.PluggyCategoryId).HasColumnName("pluggy_category_id").HasMaxLength(BankTransaction.MaxCategoryIdLength);
            entity.Property(x => x.MerchantName).HasColumnName("merchant_name").HasMaxLength(BankTransaction.MaxMerchantNameLength);
            entity.Property(x => x.MerchantCnpj).HasColumnName("merchant_cnpj").HasMaxLength(BankTransaction.MaxCnpjLength);
            entity.Property(x => x.MerchantCategory).HasColumnName("merchant_category").HasMaxLength(BankTransaction.MaxCategoryLength);
            entity.Property(x => x.PaymentMethod).HasColumnName("payment_method").HasMaxLength(BankTransaction.MaxPaymentMethodLength);
            entity.Property(x => x.InstallmentNumber).HasColumnName("installment_number");
            entity.Property(x => x.InstallmentTotal).HasColumnName("installment_total");
            entity.Property(x => x.BillId).HasColumnName("bill_id").HasMaxLength(BankTransaction.MaxPluggyIdLength);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.BalanceAfter).HasColumnName("balance_after").HasPrecision(18, 2);
            // The token: a review written over a state it did not read (confirm and discard of the same line at the
            // same moment) is refused, so a line is never left discarded with a transaction created from it.
            entity.Property(x => x.ReviewState).HasColumnName("review_state").HasConversion<string>().HasMaxLength(16).IsRequired().IsConcurrencyToken();
            entity.Property(x => x.LinkedTransactionId).HasColumnName("linked_transaction_id");
            entity.Property(x => x.LinkedIncomeSourceId).HasColumnName("linked_income_source_id");
            entity.Property(x => x.MatchedTransactionId).HasColumnName("matched_transaction_id");
            entity.Property(x => x.AutoReason).HasColumnName("auto_reason").HasMaxLength(BankTransaction.MaxAutoReasonLength);
            entity.Property(x => x.SuggestedCategory).HasColumnName("suggested_category").HasMaxLength(BankTransaction.MaxSuggestedCategoryLength);
            entity.Property(x => x.ReviewedAtUtc).HasColumnName("reviewed_at_utc");
            // The transaction exactly as Pluggy sent it. Never logged, never returned by the API.
            entity.Property(x => x.RawJson).HasColumnName("raw_json").HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.SyncRunId).HasColumnName("sync_run_id");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            entity.Ignore(x => x.IsExpense);
            entity.Ignore(x => x.AbsoluteAmount);

            // A Pluggy transaction is mirrored once in the whole database: synchronising again never duplicates.
            entity.HasIndex(x => x.PluggyTransactionId).IsUnique();
            entity.HasIndex(x => new { x.CoupleId, x.LocalDate });
            entity.HasIndex(x => new { x.CoupleId, x.ReviewState });
            entity.HasIndex(x => x.BankAccountId);
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.LinkedTransactionId);
            entity.HasIndex(x => x.LinkedIncomeSourceId);
            entity.HasIndex(x => x.SyncRunId);

            entity.HasOne<Couple>()
                .WithMany()
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<BankAccount>()
                .WithMany()
                .HasForeignKey(x => x.BankAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // Deleting the transaction (or the income) never takes the line of the mirror with it: the link is cleared.
            entity.HasOne<Transaction>()
                .WithMany()
                .HasForeignKey(x => x.LinkedTransactionId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            entity.HasOne<IncomeSource>()
                .WithMany()
                .HasForeignKey(x => x.LinkedIncomeSourceId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            entity.HasOne<SyncRun>()
                .WithMany()
                .HasForeignKey(x => x.SyncRunId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        modelBuilder.Entity<AiUsage>(entity =>
        {
            // Accounting of the AI calls: metadata only, never the prompt nor the answer. Not ICoupleScoped (there
            // are calls without a group) and with no foreign key: every read by group names the group.
            entity.ToTable("ai_usage");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.DayUtc).HasColumnName("day_utc").IsRequired();
            entity.Property(x => x.DayBrt).HasColumnName("day_brt").IsRequired();
            entity.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(AiUsage.MaxProviderLength).IsRequired();
            entity.Property(x => x.Model).HasColumnName("model").HasMaxLength(AiUsage.MaxModelLength).IsRequired();
            entity.Property(x => x.CoupleId).HasColumnName("couple_id");
            entity.Property(x => x.Feature).HasColumnName("feature").HasMaxLength(AiUsage.MaxFeatureLength).IsRequired();
            entity.Property(x => x.InputTokens).HasColumnName("input_tokens").IsRequired();
            entity.Property(x => x.OutputTokens).HasColumnName("output_tokens").IsRequired();
            entity.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(AiUsage.MaxOutcomeLength).IsRequired();
            entity.Property(x => x.LatencyMs).HasColumnName("latency_ms").IsRequired();
            entity.Property(x => x.RetryAtUtc).HasColumnName("retry_at_utc");

            entity.HasIndex(x => new { x.DayUtc, x.Provider, x.Model });
            entity.HasIndex(x => new { x.CoupleId, x.DayBrt });
        });

        ApplyCoupleQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Filter semantics:
    ///   - CurrentCoupleId == null (background job, migration, seeder, CLI): no filter — all records returned.
    ///   - CurrentCoupleId set (HTTP request with authenticated couple): only that couple's records returned.
    ///
    /// T-005+ usage: implement ICoupleScoped on your entity — the filter is applied automatically here.
    /// </summary>
    private void ApplyCoupleQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ICoupleScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var method = typeof(AppDbContext)
                .GetMethod(nameof(ApplyCoupleFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType);

            method.Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyCoupleFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ICoupleScoped
    {
        modelBuilder.Entity<TEntity>()
            .HasQueryFilter(e => CurrentCoupleId == null || e.CoupleId == CurrentCoupleId.GetValueOrDefault());
    }
}
