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

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<TransactionEventIngest> TransactionEventIngests => Set<TransactionEventIngest>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    public DbSet<Goal> Goals => Set<Goal>();

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();

    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();

    public DbSet<BudgetPlan> BudgetPlans => Set<BudgetPlan>();

    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

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
            entity.Property(x => x.CoupleId).HasColumnName("couple_id");
            entity.Property(x => x.CoupleJoinedAtUtc).HasColumnName("couple_joined_at_utc");
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            entity.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();

            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.CoupleId);

            entity.HasOne(x => x.Couple)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.CoupleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Couple>(entity =>
        {
            entity.ToTable("couples");

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.JoinCode).HasColumnName("join_code").HasMaxLength(6).IsRequired();
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at").IsRequired();

            entity.HasIndex(x => x.JoinCode).IsUnique();
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
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

            entity.HasIndex(x => new { x.CoupleId, x.Fingerprint }).IsUnique();
            entity.HasIndex(x => x.CoupleId);
            entity.HasIndex(x => new { x.CoupleId, x.EventTimestampUtc });
            entity.HasIndex(x => new { x.CoupleId, x.Category });

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
            entity.HasIndex(x => x.CoupleId);
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

            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasIndex(x => x.CoupleId);
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
                .IsRequired();
            entity.Property(x => x.OcrResultJson)
                .HasColumnName("ocr_result_json")
                .HasColumnType("jsonb");
            entity.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(512);
            entity.Property(x => x.QuotaResetDate).HasColumnName("quota_reset_date");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

            entity.HasIndex(x => x.CoupleId);
            entity.HasIndex(x => new { x.CoupleId, x.Status });
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
