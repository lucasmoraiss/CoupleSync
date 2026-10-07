using System.Globalization;
using System.Text.Json;
using CoupleSync.Application.OcrImport;

namespace CoupleSync.PostgresTests;

/// <summary>
/// Rows shaped like the production data that existed before this series of changes (schema of the migration
/// 20260510170608_AddTransactionSourceAndRetryCount): free-text categories in every spelling, "brl"/"BRL"/other
/// currencies, colliding allocations in one plan, one device token under several users, 6-character join codes,
/// groups of several members, import jobs in every status, refresh tokens and notification events.
/// </summary>
internal sealed class LegacyDataSeed
{
    public const string LastLegacyMigration = "20260510170608_AddTransactionSourceAndRetryCount";
    public const string Password = "SecurePass123!";

    // Group 1: three members. U1 joined first even though U2 has the oldest account: U1 must become the owner.
    public Guid Couple1 { get; } = Guid.NewGuid();
    public Guid U1 { get; } = Guid.NewGuid();
    public Guid U2 { get; } = Guid.NewGuid();
    public Guid U3 { get; } = Guid.NewGuid();

    // Group 2: two members with no recorded join date; the older account (U5) becomes the owner.
    public Guid Couple2 { get; } = Guid.NewGuid();
    public Guid U4 { get; } = Guid.NewGuid();
    public Guid U5 { get; } = Guid.NewGuid();

    // Group 3: nobody left in it. A user with no group at all.
    public Guid Couple3 { get; } = Guid.NewGuid();
    public Guid U6 { get; } = Guid.NewGuid();

    public Guid Plan1 { get; } = Guid.NewGuid();
    public Guid Plan2 { get; } = Guid.NewGuid();

    public Guid JobPending { get; } = Guid.NewGuid();
    public Guid JobProcessing { get; } = Guid.NewGuid();
    public Guid JobReady { get; } = Guid.NewGuid();
    public Guid JobFailed { get; } = Guid.NewGuid();
    public Guid JobConfirmed { get; } = Guid.NewGuid();

    public Guid Ghost { get; } = Guid.NewGuid();

    private int _fingerprint;

    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Ts(int month, int day, int hour = 12) =>
        Q(string.Create(CultureInfo.InvariantCulture, $"2026-{month:00}-{day:00}T{hour:00}:00:00Z"));

    public async Task SeedAsync(TestDatabase db, bool includeOrphans)
    {
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4);

        await db.ExecuteAsync($"""
            INSERT INTO couples (id, created_at, join_code, status) VALUES
              ('{Couple1}', {Ts(1, 1)}, 'ABC123', 'Active'),
              ('{Couple2}', {Ts(2, 1)}, 'XYZ789', 'Active'),
              ('{Couple3}', {Ts(3, 1)}, 'EMPTY1', 'Dissolved');

            INSERT INTO users (id, couple_id, couple_joined_at_utc, created_at_utc, email, is_active, name, password_hash) VALUES
              ('{U1}', '{Couple1}', {Ts(1, 3)}, {Ts(1, 1)},  'u1@legacy.test', true, 'Ana',   {Q(passwordHash)}),
              ('{U2}', '{Couple1}', {Ts(1, 5)}, {Ts(12, 1).Replace("2026-12", "2025-12")}, 'u2@legacy.test', true, 'Bruno', {Q(passwordHash)}),
              ('{U3}', '{Couple1}', {Ts(1, 4)}, {Ts(1, 2)},  'u3@legacy.test', true, 'Carla', {Q(passwordHash)}),
              ('{U4}', '{Couple2}', NULL,       {Ts(2, 2)},  'u4@legacy.test', true, 'Davi',  {Q(passwordHash)}),
              ('{U5}', '{Couple2}', NULL,       {Ts(2, 1)},  'u5@legacy.test', true, 'Eva',   {Q(passwordHash)}),
              ('{U6}', NULL,        NULL,       {Ts(3, 1)},  'u6@legacy.test', true, 'Fabio', {Q(passwordHash)});

            INSERT INTO refresh_tokens (id, created_at_utc, expires_at_utc, token_hash, updated_at_utc, user_id) VALUES
              ('{Guid.NewGuid()}', {Ts(9, 1)}, {Ts(12, 1)}, 'hash-u1', {Ts(9, 1)}, '{U1}'),
              ('{Guid.NewGuid()}', {Ts(9, 1)}, {Ts(12, 1)}, 'hash-u4', {Ts(9, 1)}, '{U4}');
            """);

        await SeedTransactionsAsync(db);
        await SeedBudgetAsync(db);
        await SeedGoalsIncomesRulesAsync(db);
        await SeedDeviceTokensAndNotificationsAsync(db);
        await SeedImportJobsAsync(db);

        if (includeOrphans)
        {
            await SeedOrphansAsync(db);
        }
    }

    private async Task AddTransactionAsync(TestDatabase db, Guid couple, Guid user, string category, string currency, decimal amount, int day)
    {
        var ingest = Guid.NewGuid();
        var fingerprint = $"fp-{++_fingerprint}";
        await db.ExecuteAsync($"""
            INSERT INTO transaction_event_ingests (id, amount, bank, couple_id, created_at_utc, currency, event_timestamp_utc, status, user_id, description)
            VALUES ('{ingest}', {amount.ToString(CultureInfo.InvariantCulture)}, 'Nubank', '{couple}', {Ts(9, day)}, {Q(currency)}, {Ts(9, day)}, 'Accepted', '{user}', {Q("compra " + fingerprint)});
            INSERT INTO transactions (id, amount, bank, category, couple_id, created_at_utc, currency, event_timestamp_utc, fingerprint, ingest_event_id, source, user_id, description)
            VALUES ('{Guid.NewGuid()}', {amount.ToString(CultureInfo.InvariantCulture)}, 'Nubank', {Q(category)}, '{couple}', {Ts(9, day)}, {Q(currency)}, {Ts(9, day)}, '{fingerprint}', '{ingest}', 0, '{user}', {Q("compra " + fingerprint)});
            """);
    }

    private async Task SeedTransactionsAsync(TestDatabase db)
    {
        // (stored category, stored currency) -> expected (ALIMENTACAO..., BRL...) is asserted by the tests.
        await AddTransactionAsync(db, Couple1, U1, "Alimentação", "BRL", 10m, 1);
        await AddTransactionAsync(db, Couple1, U1, "alimentacao", "brl", 11m, 2);
        await AddTransactionAsync(db, Couple1, U2, "  ALIMENTAÇÃO ", "bRL", 12m, 3);
        await AddTransactionAsync(db, Couple1, U2, "Saúde", "BRL", 20m, 4);
        await AddTransactionAsync(db, Couple1, U3, "saude", "Brl", 21m, 5);
        await AddTransactionAsync(db, Couple1, U3, "Transporte", "BRL", 30m, 6);
        await AddTransactionAsync(db, Couple1, U1, "Mercado do bairro", "BRL", 40m, 7);
        await AddTransactionAsync(db, Couple1, U1, "Outros", "USD", 50m, 8);
        await AddTransactionAsync(db, Couple1, U2, "lazer", "EUR", 60m, 9);
        await AddTransactionAsync(db, Couple1, U2, "Casa", "BRL", 70m, 10);
        await AddTransactionAsync(db, Couple2, U4, "MORADIA", "BRL", 100m, 11);
        await AddTransactionAsync(db, Couple2, U5, "moradía", "brl", 110m, 12);
        await AddTransactionAsync(db, Couple2, U5, "Educação", "BRL", 120m, 13);
    }

    private async Task AddAllocationAsync(TestDatabase db, Guid plan, string category, decimal amount, string currency, int hour)
    {
        await db.ExecuteAsync($"""
            INSERT INTO budget_allocations (id, allocated_amount, budget_plan_id, category, created_at_utc, currency)
            VALUES ('{Guid.NewGuid()}', {amount.ToString(CultureInfo.InvariantCulture)}, '{plan}', {Q(category)}, {Ts(9, 1, hour)}, {Q(currency)});
            """);
    }

    private async Task SeedBudgetAsync(TestDatabase db)
    {
        await db.ExecuteAsync($"""
            INSERT INTO budget_plans (id, couple_id, created_at_utc, currency, gross_income, month, updated_at_utc) VALUES
              ('{Plan1}', '{Couple1}', {Ts(9, 1)}, 'brl', 5000.00, '2026-09', {Ts(9, 1)}),
              ('{Plan2}', '{Couple2}', {Ts(9, 1)}, 'BRL', 3000.00, '2026-09', {Ts(9, 1)});
            """);

        // Plan 1: three spellings of ALIMENTACAO in BRL (175 in total), two of LAZER in USD (15) next to a LAZER in BRL
        // (kept apart: another currency), and two free-text categories that both become OUTROS (70).
        await AddAllocationAsync(db, Plan1, "Alimentação", 100m, "brl", 1);
        await AddAllocationAsync(db, Plan1, "alimentacao", 50m, "BRL", 2);
        await AddAllocationAsync(db, Plan1, "ALIMENTACAO", 25m, "BRL", 3);
        await AddAllocationAsync(db, Plan1, "Saúde", 200m, "BRL", 4);
        await AddAllocationAsync(db, Plan1, "Lazer ", 10m, "USD", 5);
        await AddAllocationAsync(db, Plan1, "lazer", 5m, "USD", 6);
        await AddAllocationAsync(db, Plan1, "Lazer", 20m, "BRL", 7);
        await AddAllocationAsync(db, Plan1, "Viagem", 30m, "BRL", 8);
        await AddAllocationAsync(db, Plan1, "Presentes", 40m, "BRL", 9);
        // Plan 2: a single row, already needing only a case change.
        await AddAllocationAsync(db, Plan2, "alimentacao", 300m, "brl", 1);
    }

    private async Task SeedGoalsIncomesRulesAsync(TestDatabase db)
    {
        await db.ExecuteAsync($"""
            INSERT INTO goals (id, couple_id, created_at_utc, created_by_user_id, currency, current_amount, deadline, status, target_amount, title, updated_at_utc) VALUES
              ('{Guid.NewGuid()}', '{Couple1}', {Ts(8, 1)}, '{U1}', 'brl', 150.00, {Ts(12, 31)}, 'Active', 1000.00, 'Viagem', {Ts(8, 1)}),
              ('{Guid.NewGuid()}', '{Couple2}', {Ts(8, 1)}, '{U4}', 'BRL', 0.00,   {Ts(12, 31)}, 'Archived', 500.00, 'Carro', {Ts(8, 1)});

            INSERT INTO income_sources (id, amount, couple_id, created_at_utc, currency, is_recurring, is_shared, month, name, updated_at_utc, user_id) VALUES
              ('{Guid.NewGuid()}', 5000.00, '{Couple1}', {Ts(9, 1)}, 'brl', true,  false, '2026-09', 'Salário Ana', {Ts(9, 1)}, '{U1}'),
              ('{Guid.NewGuid()}', 400.00,  '{Couple1}', {Ts(9, 1)}, 'EUR', false, true,  '2026-09', 'Freela',      {Ts(9, 1)}, '{U2}');

            INSERT INTO category_rules (id, category, is_active, keyword, priority) VALUES
              ('{Guid.NewGuid()}', 'alimentação', true, 'ifood', 10),
              ('{Guid.NewGuid()}', 'Transporte',  true, 'uber', 10),
              ('{Guid.NewGuid()}', 'Saúde ',      true, 'drogaria', 10),
              ('{Guid.NewGuid()}', 'xyz',         true, 'misterio', 5),
              ('{Guid.NewGuid()}', 'LAZER',       false, 'cinema', 5);
            """);
    }

    private async Task SeedDeviceTokensAndNotificationsAsync(TestDatabase db)
    {
        // "tok-shared" is registered under three users (a phone that switched accounts): U2 has the newest last_seen.
        await db.ExecuteAsync($"""
            INSERT INTO device_tokens (id, couple_id, created_at_utc, last_seen_at_utc, platform, token, user_id) VALUES
              ('{Guid.NewGuid()}', '{Couple1}', {Ts(9, 1, 8)}, {Ts(9, 20, 10)}, 'android', 'tok-shared', '{U1}'),
              ('{Guid.NewGuid()}', '{Couple1}', {Ts(9, 1, 9)}, {Ts(9, 20, 12)}, 'android', 'tok-shared', '{U2}'),
              ('{Guid.NewGuid()}', '{Couple1}', {Ts(9, 1, 7)}, {Ts(9, 20, 11)}, 'android', 'tok-shared', '{U3}'),
              ('{Guid.NewGuid()}', '{Couple2}', {Ts(9, 1, 7)}, {Ts(9, 20, 11)}, 'android', 'tok-own',    '{U4}');

            INSERT INTO notification_settings (id, bill_reminder_enabled, couple_id, large_transaction_enabled, low_balance_enabled, updated_at_utc, user_id) VALUES
              ('{Guid.NewGuid()}', true, '{Couple1}', true,  false, {Ts(9, 1)}, '{U1}'),
              ('{Guid.NewGuid()}', false, '{Couple2}', true, true,  {Ts(9, 1)}, '{U4}');

            INSERT INTO notification_events (id, alert_type, body, couple_id, created_at_utc, status, title, user_id) VALUES
              ('{Guid.NewGuid()}', 'LargeTransaction', 'Compra grande', '{Couple1}', {Ts(9, 2)}, 'Delivered', 'Alerta', '{U1}'),
              ('{Guid.NewGuid()}', 'LowBalance', 'Saldo baixo', '{Couple1}', {Ts(9, 3)}, 'Pending', 'Saldo', '{U2}'),
              ('{Guid.NewGuid()}', 'LargeTransaction', 'Outra', '{Couple2}', {Ts(9, 4)}, 'Failed', 'Alerta', '{U4}');
            """);
    }

    private async Task SeedImportJobsAsync(TestDatabase db)
    {
        var candidates = JsonSerializer.Serialize(new[]
        {
            new OcrCandidate { Index = 0, Date = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), Description = "Mercado", Amount = 80m, Currency = "BRL", Confidence = 1.0, Fingerprint = "legacy-fp-0" },
            new OcrCandidate { Index = 1, Date = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), Description = "Farmácia", Amount = 35m, Currency = "BRL", Confidence = 1.0, Fingerprint = "legacy-fp-1" },
        });

        await db.ExecuteAsync($"""
            INSERT INTO import_jobs (id, couple_id, created_at_utc, error_code, error_message, file_mime_type, ocr_result_json, quota_reset_date, retry_count, status, storage_path, updated_at_utc, user_id) VALUES
              ('{JobPending}',    '{Couple1}', {Ts(9, 5)}, NULL, NULL, 'image/jpeg', NULL, NULL, 0, 'Pending',    'legacy/pending',    {Ts(9, 5)}, '{U1}'),
              ('{JobProcessing}', '{Couple1}', {Ts(9, 5)}, NULL, NULL, 'image/jpeg', NULL, NULL, 1, 'Processing', 'legacy/processing', {Ts(9, 5)}, '{U1}'),
              ('{JobReady}',      '{Couple1}', {Ts(9, 5)}, NULL, NULL, 'image/jpeg', {Q(candidates)}::jsonb, NULL, 0, 'Ready', 'legacy/ready', {Ts(9, 5)}, '{U1}'),
              ('{JobFailed}',     '{Couple1}', {Ts(9, 5)}, 'OCR_FAILED', 'Não foi possível ler.', 'image/jpeg', NULL, {Ts(10, 1)}, 3, 'Failed', 'legacy/failed', {Ts(9, 5)}, '{U2}'),
              ('{JobConfirmed}',  '{Couple2}', {Ts(9, 5)}, NULL, NULL, 'application/pdf', {Q(candidates)}::jsonb, NULL, 0, 'Confirmed', 'legacy/confirmed', {Ts(9, 6)}, '{U4}');
            """);
    }

    private async Task SeedOrphansAsync(TestDatabase db)
    {
        // Rows that point to a group/user that does not exist (the application alone guaranteed the relation).
        var ingest = Guid.NewGuid();
        await db.ExecuteAsync($"""
            INSERT INTO device_tokens (id, couple_id, created_at_utc, last_seen_at_utc, platform, token, user_id)
              VALUES ('{Guid.NewGuid()}', '{Couple1}', {Ts(9, 1)}, {Ts(9, 1)}, 'android', 'tok-ghost-user', '{Ghost}');
            INSERT INTO device_tokens (id, couple_id, created_at_utc, last_seen_at_utc, platform, token, user_id)
              VALUES ('{Guid.NewGuid()}', '{Ghost}', {Ts(9, 1)}, {Ts(9, 1)}, 'android', 'tok-ghost-couple', '{U5}');
            INSERT INTO notification_settings (id, bill_reminder_enabled, couple_id, large_transaction_enabled, low_balance_enabled, updated_at_utc, user_id)
              VALUES ('{Guid.NewGuid()}', true, '{Ghost}', true, true, {Ts(9, 1)}, '{Ghost}');
            INSERT INTO notification_events (id, alert_type, body, couple_id, created_at_utc, status, title, user_id)
              VALUES ('{Guid.NewGuid()}', 'LowBalance', 'x', '{Ghost}', {Ts(9, 1)}, 'Pending', 'x', '{U1}');

            INSERT INTO transaction_event_ingests (id, amount, bank, couple_id, created_at_utc, currency, event_timestamp_utc, status, user_id)
              VALUES ('{ingest}', 99.90, 'Nubank', '{Ghost}', {Ts(9, 1)}, 'BRL', {Ts(9, 1)}, 'Accepted', '{Ghost}');
            INSERT INTO transactions (id, amount, bank, category, couple_id, created_at_utc, currency, event_timestamp_utc, fingerprint, ingest_event_id, source, user_id)
              VALUES ('{Guid.NewGuid()}', 99.90, 'Nubank', 'LAZER', '{Ghost}', {Ts(9, 1)}, 'BRL', {Ts(9, 1)}, 'fp-orphan', '{ingest}', 0, '{Ghost}');
            INSERT INTO goals (id, couple_id, created_at_utc, created_by_user_id, currency, current_amount, deadline, status, target_amount, title, updated_at_utc)
              VALUES ('{Guid.NewGuid()}', '{Couple1}', {Ts(9, 1)}, '{Ghost}', 'BRL', 0, {Ts(12, 1)}, 'Active', 10, 'Meta de quem saiu', {Ts(9, 1)});
            INSERT INTO income_sources (id, amount, couple_id, created_at_utc, currency, is_recurring, is_shared, month, name, updated_at_utc, user_id)
              VALUES ('{Guid.NewGuid()}', 10, '{Couple1}', {Ts(9, 1)}, 'BRL', false, false, '2026-08', 'Renda de quem saiu', {Ts(9, 1)}, '{Ghost}');
            INSERT INTO import_jobs (id, couple_id, created_at_utc, file_mime_type, retry_count, status, storage_path, updated_at_utc, user_id)
              VALUES ('{Guid.NewGuid()}', '{Ghost}', {Ts(9, 1)}, 'image/jpeg', 0, 'Failed', 'legacy/ghost', {Ts(9, 1)}, '{Ghost}');
            """);
    }
}
