using System.Globalization;
using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39 — the bench of the rule "proof proportional to the movement": made-up groups (what is truly recurring
/// next to the places where the group only shops), generated from a seed, run through the real detector, and
/// counted the way the screen counts (what is in the total, what is only "possible"). Nothing here is real data:
/// every establishment is made up, every amount and date comes from the seed.
/// </summary>
internal static class RecurrenceProofBench
{
    public static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly DateOnly From = Today.AddMonths(-13);
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bruno = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>How the purchases of the shopping places are drawn.</summary>
    /// <param name="Realistic">
    /// False: days uniform in the month, amounts as drawn (the generator the rule was studied with). True: purchases
    /// gathered on weekends and in the first ten days of the month, part of the amounts rounded (R$ 5, R$ 10), and
    /// 26 months of history in the shopping places.
    /// </param>
    public sealed record Shape(bool Realistic)
    {
        public static readonly Shape Uniform = new(false);
        public static readonly Shape Lifelike = new(true);
    }

    public sealed record Item(
        string Key, string Kind, string Cadence, decimal Amount, decimal Monthly, bool Visible, bool InTotal, bool Possible, bool PriceIncrease, int Occurrences);

    // ------------------------------------------------------------------ the real detector, counted as the screen does

    public static List<Item> Detect(List<RecurrenceRow> rows, RecurrenceOptions options, DateOnly? today = null)
        => RecurrenceDetector.Detect(rows, today ?? Today, options).Streams.Select(s =>
        {
            var f = s.Facts;
            var visible = f.Status != RecurringStatuses.Stopped;
            // "Possible": a pattern without proof. (A probable instalment is also of low confidence, and is not this.)
            var possible = visible && f.Confidence == RecurringConfidences.Low && f.Kind != RecurringKinds.Installment;
            // The rule of the total (RecurringStreamRepository.GetTotalsAsync), without corrections of the person.
            var inTotal = visible && f.Confidence != RecurringConfidences.Low && f.Kind != RecurringKinds.Habit;
            var monthly = s.Cadence == RecurringCadences.Monthly ? f.MedianAmount : f.AnnualCost / 12m;
            return new Item(s.MerchantKey, f.Kind, s.Cadence, f.MedianAmount, monthly, visible, inTotal, possible,
                f.Flags.Contains(RecurringFlags.PriceIncrease), f.Occurrences);
        }).ToList();

    private static bool OfKey(Item item, string key) => item.Key == key || item.Key.StartsWith(key + "#", StringComparison.Ordinal);

    // ------------------------------------------------------------------ generator

    private static RecurrenceRow Row(Random rng, string merchant, decimal amount, DateOnly date, string category, Guid? user = null)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        var who = user ?? (rng.Next(2) == 0 ? Ana : Bruno);
        var at = date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc).AddMinutes(rng.Next(0, 480));
        return new RecurrenceRow(new Guid(bytes), at, amount, merchant, null, category, who, TransactionSource.Notification);
    }

    private static decimal Money(double value) => decimal.Round((decimal)value, 2, MidpointRounding.AwayFromZero);

    private static decimal LogUniform(Random rng, double lo, double hi)
        => Money(Math.Exp(Math.Log(lo) + rng.NextDouble() * (Math.Log(hi) - Math.Log(lo))));

    private static decimal Uniform(Random rng, double lo, double hi) => Money(lo + rng.NextDouble() * (hi - lo));

    private static decimal Fuel(Random rng)
    {
        if (rng.NextDouble() < 0.55)
        {
            var p = rng.NextDouble();
            return p < 0.30 ? 100m : p < 0.55 ? 150m : p < 0.80 ? 200m : p < 0.90 ? 250m : 300m;
        }

        return Uniform(rng, 100, 300);
    }

    /// <param name="Months">Months of history.</param>
    /// <param name="Chance">Chance of any purchase in a month.</param>
    public sealed record Profile(string Label, string Name, string Category, int Min, int Max, Func<Random, decimal> Amount, int Months = 13, double Chance = 1.0);

    private static readonly Profile Market = new("mercado (4–8/mês, R$ 80–600)", "Supermercado Exemplo", "ALIMENTACAO", 4, 8, r => LogUniform(r, 80, 600));
    private static readonly Profile Restaurant = new("restaurante (2–8/mês, R$ 25–180)", "Restaurante Exemplo", "ALIMENTACAO", 2, 8, r => LogUniform(r, 25, 180));
    private static readonly Profile FuelStation = new("posto (2–5/mês, R$ 100–300, redondos)", "Posto Exemplo", "TRANSPORTE", 2, 5, Fuel);
    private static readonly Profile Bakery = new("padaria (8–20/mês, R$ 8–40)", "Padaria Exemplo", "ALIMENTACAO", 8, 20, r => LogUniform(r, 8, 40));
    private static readonly Profile Pharmacy = new("farmácia (1–3/mês, R$ 15–250, SAUDE)", "Farmacia Exemplo", "SAUDE", 1, 3, r => LogUniform(r, 15, 250));
    private static readonly Profile PharmacyOther = new("farmácia em OUTROS", "Drogaria Exemplo", "OUTROS", 1, 3, r => LogUniform(r, 15, 250));
    private static readonly Profile Rides = new("app de transporte (5–20/mês, R$ 8–60)", "Corridas App Exemplo", "TRANSPORTE", 5, 20, r => LogUniform(r, 8, 60));
    private static readonly Profile Occasional = new("loja ocasional, 26 meses (compra em 25% dos meses)", "Loja Ocasional Exemplo", "COMPRAS", 1, 1, r => LogUniform(r, 50, 400), 26, 0.25);
    private static readonly Profile Market26 = Market with { Label = "mercado, 26 meses de histórico", Months = 26 };
    private static readonly Profile Pharmacy26 = Pharmacy with { Label = "farmácia (SAUDE), 26 meses de histórico", Months = 26 };

    /// <summary>A service of one price once a month, on any day (a barber): the price is drawn once per history.</summary>
    private static Profile Service(Random rng)
    {
        var price = Money(Math.Round(40 + rng.NextDouble() * 80));
        return new Profile("serviço 1×/mês, preço fixo, dia qualquer", "Barbearia Exemplo", "OUTROS", 1, 1, _ => price);
    }

    /// <summary>The shopping places measured one by one. The order is part of the seeds.</summary>
    public static readonly (string Label, Func<Random, Profile> Make)[] Profiles =
    [
        (Market.Label, _ => Market), (Restaurant.Label, _ => Restaurant), (FuelStation.Label, _ => FuelStation), (Bakery.Label, _ => Bakery),
        (Pharmacy.Label, _ => Pharmacy), (PharmacyOther.Label, _ => PharmacyOther), (Rides.Label, _ => Rides),
        ("serviço 1×/mês, preço fixo, dia qualquer", Service),
        (Occasional.Label, _ => Occasional), (Market26.Label, _ => Market26), (Pharmacy26.Label, _ => Pharmacy26),
    ];

    /// <summary>The day of a purchase. Lifelike: 45% on a weekend, 25% in the first ten days of the month, the rest on any day.</summary>
    private static DateOnly PurchaseDay(Random rng, DateOnly month, Shape shape)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        if (!shape.Realistic) return new DateOnly(month.Year, month.Month, rng.Next(1, days + 1));

        var p = rng.NextDouble();
        if (p < 0.45)
        {
            var weekend = Enumerable.Range(1, days)
                .Select(d => new DateOnly(month.Year, month.Month, d))
                .Where(d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                .ToList();
            return weekend[rng.Next(weekend.Count)];
        }

        return new DateOnly(month.Year, month.Month, p < 0.70 ? rng.Next(1, 11) : rng.Next(1, days + 1));
    }

    /// <summary>Lifelike: 35% of the amounts are round (a multiple of R$ 5, or of R$ 10 above R$ 200).</summary>
    private static decimal PurchaseAmount(Random rng, Profile p, Shape shape)
    {
        var amount = p.Amount(rng);
        if (!shape.Realistic || rng.NextDouble() >= 0.35) return amount;
        var step = amount > 200m ? 10m : 5m;
        return Math.Max(step, decimal.Round(amount / step, 0, MidpointRounding.AwayFromZero) * step);
    }

    public static List<RecurrenceRow> Habit(Random rng, Profile p, Shape shape, string? name = null)
    {
        var rows = new List<RecurrenceRow>();
        var from = p.Months == 13 ? From : Today.AddMonths(-p.Months);
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= Today; month = month.AddMonths(1))
        {
            if (p.Chance < 1.0 && rng.NextDouble() >= p.Chance) continue;
            var visits = rng.Next(p.Min, p.Max + 1);
            for (var v = 0; v < visits; v++)
            {
                var date = PurchaseDay(rng, month, shape);
                var amount = PurchaseAmount(rng, p, shape);
                var row = Row(rng, name ?? p.Name, amount, date, p.Category);
                if (date >= from && date <= Today) rows.Add(row);
            }
        }

        return rows;
    }

    private static List<DateOnly> MonthlyDates(Random rng, int n, int day, int jitter)
    {
        var dates = new List<DateOnly>();
        var month = new DateOnly(Today.Year, Today.Month, 1);
        while (dates.Count < n)
        {
            var dim = DateTime.DaysInMonth(month.Year, month.Month);
            var date = new DateOnly(month.Year, month.Month, Math.Min(day, dim)).AddDays(jitter == 0 ? 0 : rng.Next(-jitter, jitter + 1));
            if (date < From) break;
            if (date <= Today) dates.Add(date);
            month = month.AddMonths(-1);
        }

        dates.Reverse();
        return dates;
    }

    /// <param name="Kind">The kind the item must have; null = anything but an instalment.</param>
    public sealed record Truth(string Label, string Name, List<RecurrenceRow> Rows, string Cadence, string? Kind, decimal Amount, decimal Tolerance)
    {
        public string Key => MerchantKey.Normalize(Name);
    }

    private static Truth Fixed(Random rng, string label, string name, string category, double lo, double hi, int jitter, int? day = null, int minCharges = 4, Guid? user = null, int? exactly = null)
    {
        var amount = Uniform(rng, lo, hi);
        var n = exactly ?? rng.Next(minCharges, 14);
        var dates = MonthlyDates(rng, n, day ?? rng.Next(1, 29), jitter);
        var who = user ?? Ana;
        return new Truth(label, name, dates.Select(d => Row(rng, name, amount, d, category, who)).ToList(), RecurringCadences.Monthly, null, amount, 0.05m);
    }

    private static Truth WithIncrease(Random rng, string label, string name, string category, double lo, double hi, Guid? user = null)
    {
        var old = Uniform(rng, lo, hi);
        var now = Money((double)old * (1.08 + rng.NextDouble() * 0.22));
        var dates = MonthlyDates(rng, rng.Next(5, 14), rng.Next(1, 29), 0);
        var step = rng.Next(2, dates.Count - 1);
        var who = user ?? Ana;
        return new Truth(label, name, dates.Select((d, i) => Row(rng, name, i < step ? old : now, d, category, who)).ToList(), RecurringCadences.Monthly, null, now, 0.05m);
    }

    private static Truth Variable(Random rng, string label, string name, string category, double lo, double hi, Guid? user = null, int jitter = 3, double spread = 0.2)
    {
        var centre = lo + rng.NextDouble() * (hi - lo);
        var dates = MonthlyDates(rng, rng.Next(4, 14), rng.Next(1, 29), jitter);
        var who = user ?? Ana;
        return new Truth(label, name, dates.Select(d => Row(rng, name, Money(centre * (1 - spread + rng.NextDouble() * 2 * spread)), d, category, who)).ToList(),
            RecurringCadences.Monthly, null, Money(centre), 0.25m);
    }

    private static Truth Yearly(Random rng, string label, string name, string category, double lo, double hi, double increase = 0)
    {
        var amount = Uniform(rng, lo, hi);
        var last = Today.AddDays(-rng.Next(5, 301));
        var before = last.AddDays(-(365 + rng.Next(-5, 6)));
        var now = increase == 0 ? amount : Money((double)amount * (1 + increase));
        return new Truth(label, name, [Row(rng, name, amount, before, category, Ana), Row(rng, name, now, last, category, Ana)], RecurringCadences.Yearly, null, now, 0.08m);
    }

    private static Truth Weekly(Random rng, string label, string name, string category, double lo, double hi)
    {
        var amount = Uniform(rng, lo, hi);
        var n = rng.Next(6, 41);
        var last = Today.AddDays(-rng.Next(0, 7));
        var rows = Enumerable.Range(0, n).Select(i => Row(rng, name, amount, last.AddDays(-7 * (n - 1 - i)), category, Ana)).ToList();
        return new Truth(label, name, rows, RecurringCadences.Weekly, null, amount, 0.05m);
    }

    private static Truth Installments(Random rng, string label, string name, string category, double lo, double hi)
    {
        var amount = Uniform(rng, lo, hi);
        var total = rng.Next(3, 13);
        var paid = rng.Next(2, total);
        var dates = MonthlyDates(rng, paid, rng.Next(1, 29), 0);
        var rows = dates.Select((d, i) => Row(rng, string.Create(Inv, $"{name} {i + 1:D2}/{total:D2}"), amount, d, category, Ana)).ToList();
        return new Truth(label, name, rows, RecurringCadences.Monthly, RecurringKinds.Installment, amount, 0.05m);
    }

    private static Truth SubscriptionPlusLoosePurchases(Random rng, string label, string name, Shape shape, int jitter = 0, int? exactly = null, bool second = false)
    {
        var truth = Fixed(rng, label, name, "LAZER", 14.9, 59.9, jitter, exactly: exactly);
        if (second)
        {
            // A second subscription in the same shop, at another price and on another day.
            var other = Fixed(rng, label, name, "LAZER", 70, 120, 0);
            truth.Rows.AddRange(other.Rows);
        }

        var loose = Habit(rng, new Profile(string.Empty, name, "LAZER", 0, 3, r => LogUniform(r, 5, 150)), shape);
        foreach (var row in loose) truth.Rows.Add(row with { UserId = Ana });
        return truth;
    }

    /// <summary>What is truly recurring, measured one by one. The order is part of the seeds.</summary>
    public static readonly (string Label, Func<Random, Shape, Truth> Make)[] Scenarios =
    [
        ("assinatura, valor fixo, dia fixo", (r, _) => Fixed(r, "", "Streaming Exemplo", "LAZER", 14.9, 119.9, 0)),
        ("assinatura, dia ± 2", (r, _) => Fixed(r, "", "Streaming Exemplo", "LAZER", 14.9, 119.9, 2)),
        ("assinatura, último dia do mês", (r, _) => Fixed(r, "", "Streaming Exemplo", "LAZER", 14.9, 119.9, 0, 31)),
        ("assinatura com um reajuste", (r, _) => WithIncrease(r, "", "Streaming Exemplo", "LAZER", 14.9, 119.9)),
        ("assinatura + 0–3 compras avulsas/mês na loja", (r, s) => SubscriptionPlusLoosePurchases(r, "", "Loja de Aplicativos Exemplo", s)),
        ("conta variável, nome de utilidade, MORADIA", (r, _) => Variable(r, "", "Energia Exemplo", "MORADIA", 90, 400)),
        ("conta variável, nome genérico, MORADIA", (r, _) => Variable(r, "", "Companhia Exemplo", "MORADIA", 90, 400)),
        ("conta variável, nome genérico, OUTROS", (r, _) => Variable(r, "", "Operadora Exemplo", "OUTROS", 60, 200)),
        ("aluguel/condomínio", (r, _) => Fixed(r, "", "Imobiliaria Exemplo", "MORADIA", 700, 3500, 1)),
        ("mensalidade fixa (escola/academia)", (r, _) => Fixed(r, "", "Escola Exemplo", "OUTROS", 250, 1500, 2)),
        ("anual (2 cobranças)", (r, _) => Yearly(r, "", "Seguro Exemplo", "OUTROS", 120, 900)),
        ("plano semanal de valor fixo", (r, _) => Weekly(r, "", "Plano Semanal Exemplo", "LAZER", 15, 80)),
        ("parcelas n/N", (r, _) => Installments(r, "", "Loja Exemplo", "COMPRAS", 40, 500)),
        ("assinatura + avulsas, dia ± 2", (r, s) => SubscriptionPlusLoosePurchases(r, "", "Loja de Aplicativos Exemplo", s, 2)),
        ("assinatura + avulsas, só 3 cobranças até hoje", (r, s) => SubscriptionPlusLoosePurchases(r, "", "Loja de Aplicativos Exemplo", s, 0, 3)),
        ("duas assinaturas na mesma loja + avulsas", (r, s) => SubscriptionPlusLoosePurchases(r, "", "Loja de Aplicativos Exemplo", s, 0, null, true)),
        ("assinatura + avulsas em loja de nome de lista", (r, s) => SubscriptionPlusLoosePurchases(r, "", "Google Play", s)),
        ("assinatura em moeda estrangeira (valor ± 4%)", (r, _) => Variable(r, "", "Software Exemplo", "OUTROS", 20, 120, null, 1, 0.04)),
        ("conta variável paga em dia irregular (± 8), nome genérico", (r, _) => Variable(r, "", "Operadora Exemplo", "OUTROS", 60, 200, null, 8)),
        ("conta variável paga em dia irregular (± 8), nome de utilidade", (r, _) => Variable(r, "", "Energia Exemplo", "MORADIA", 90, 400, null, 8)),
        ("anual com reajuste de 10%", (r, _) => Yearly(r, "", "Seguro Exemplo", "OUTROS", 120, 900, 0.10)),
        ("assinatura nova (3 cobranças), loja sem outras compras", (r, _) => Fixed(r, "", "Streaming Exemplo", "LAZER", 14.9, 119.9, 0, exactly: 3)),
    ];

    // ------------------------------------------------------------------ measures

    /// <summary>Of <paramref name="Histories"/> histories of one shopping place: how many show something in the total, or as possible.</summary>
    public sealed record ProfileResult(string Label, int Histories, int WithItemInTotal, int WithPossible, int ItemsInTotal, int ItemsPossible, decimal FalseMonthlyAverage);

    public static ProfileResult MeasureProfile(int profile, int seedBase, int histories, Shape shape, RecurrenceOptions options)
    {
        int anyTotal = 0, anyPossible = 0, itemsTotal = 0, itemsPossible = 0;
        var money = 0m;
        for (var i = 0; i < histories; i++)
        {
            var rng = new Random(seedBase + 10_000 * profile + i);
            var p = Profiles[profile].Make(rng);
            var items = Detect(Habit(rng, p, shape), options).Where(x => x.Visible).ToList();
            var counted = items.Where(x => x.InTotal).ToList();
            var possible = items.Count(x => x.Possible);
            if (counted.Count > 0) anyTotal++;
            if (possible > 0) anyPossible++;
            itemsTotal += counted.Count;
            itemsPossible += possible;
            money += counted.Sum(x => x.Monthly);
        }

        return new ProfileResult(Profiles[profile].Label, histories, anyTotal, anyPossible, itemsTotal, itemsPossible, money / histories);
    }

    /// <summary>
    /// The place a person goes to every week (fuel every Saturday, R$ 100) and where, once a month on any day, they
    /// also make a bigger purchase (R$ 190 to 250). The weekly series is a habit; the bigger purchases are loose.
    /// </summary>
    /// <param name="onTheSameWeekday">False: the same purchases, each up to three days away from its Saturday (no weekly rhythm).</param>
    public static List<RecurrenceRow> WeeklyHabitWithLoosePurchases(Random rng, bool onTheSameWeekday)
    {
        const string name = "Posto Semanal Exemplo";
        var rows = new List<RecurrenceRow>();
        var saturday = Today;
        while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(-1);
        for (var date = saturday; date >= From; date = date.AddDays(-7))
        {
            var day = onTheSameWeekday ? date : date.AddDays(rng.Next(-3, 4));
            if (day <= Today) rows.Add(Row(rng, name, 100m, day, "TRANSPORTE", Ana));
        }

        for (var month = new DateOnly(From.Year, From.Month, 1); month <= Today; month = month.AddMonths(1))
        {
            var date = new DateOnly(month.Year, month.Month, rng.Next(1, DateTime.DaysInMonth(month.Year, month.Month) + 1));
            var amount = Uniform(rng, 190, 250);
            if (date >= From && date <= Today) rows.Add(Row(rng, name, amount, date, "TRANSPORTE", Ana));
        }

        return rows;
    }

    /// <summary>The same measure as <see cref="MeasureProfile"/>, for <see cref="WeeklyHabitWithLoosePurchases"/>.</summary>
    public static ProfileResult MeasureWeeklyHabit(int seedBase, int histories, bool onTheSameWeekday, RecurrenceOptions options)
    {
        int anyTotal = 0, anyPossible = 0, itemsTotal = 0, itemsPossible = 0;
        var money = 0m;
        for (var i = 0; i < histories; i++)
        {
            var items = Detect(WeeklyHabitWithLoosePurchases(new Random(seedBase + i), onTheSameWeekday), options).Where(x => x.Visible).ToList();
            var counted = items.Where(x => x.InTotal).ToList();
            var possible = items.Count(x => x.Possible);
            if (counted.Count > 0) anyTotal++;
            if (possible > 0) anyPossible++;
            itemsTotal += counted.Count;
            itemsPossible += possible;
            money += counted.Sum(x => x.Monthly);
        }

        var label = onTheSameWeekday ? "hábito semanal (todo sábado) + 1 compra maior por mês" : "as mesmas compras sem ritmo semanal + 1 compra maior por mês";
        return new ProfileResult(label, histories, anyTotal, anyPossible, itemsTotal, itemsPossible, money / histories);
    }

    /// <summary>Of <paramref name="Histories"/> histories of one true recurrence: confirmed (in the total), possible, listed only as a habit, not found.</summary>
    public sealed record ScenarioResult(string Label, int Histories, int Confirmed, int Possible, int HabitOnly, int NotFound, int ExtraInTotal, int ExtraPossible);

    public static ScenarioResult MeasureScenario(int scenario, int seedBase, int histories, Shape shape, RecurrenceOptions options)
    {
        int confirmed = 0, possible = 0, habit = 0, none = 0, extraTotal = 0, extraPossible = 0;
        for (var i = 0; i < histories; i++)
        {
            var rng = new Random(seedBase + 10_000 * scenario + i);
            var truth = Scenarios[scenario].Make(rng, shape);
            var items = Detect(truth.Rows, options).Where(x => x.Visible && OfKey(x, truth.Key)).ToList();
            var match = items
                .Where(x => x.Cadence == truth.Cadence && (truth.Kind is null ? x.Kind != RecurringKinds.Installment : x.Kind == truth.Kind))
                .OrderBy(x => x.InTotal ? 0 : x.Possible ? 1 : 2)
                .ThenBy(x => Math.Abs(x.Amount - truth.Amount)).FirstOrDefault();
            // An item of another price is not the one measured.
            if (match is not null && Math.Abs(match.Amount - truth.Amount) > 0.30m * truth.Amount) match = null;
            extraTotal += items.Count(x => x.InTotal && !ReferenceEquals(x, match));
            extraPossible += items.Count(x => x.Possible && !ReferenceEquals(x, match));
            if (match is null) none++;
            else if (match.InTotal) confirmed++;
            else if (match.Possible) possible++;
            else habit++;
        }

        return new ScenarioResult(Scenarios[scenario].Label, histories, confirmed, possible, habit, none, extraTotal, extraPossible);
    }

    /// <summary>One made-up group: what is truly recurring, and the places where it only shops.</summary>
    /// <param name="wide">
    /// The same group with the hard cases: 26 months in the shopping places, ten shops visited now and then, a
    /// service of one price on any day, and three more true ones (a subscription in a shop with loose purchases, a
    /// variable bill of a generic name out of MORADIA, a weekly plan).
    /// </param>
    public static (List<Truth> True, List<(string Label, string Key, List<RecurrenceRow> Rows)> Habits) Group(int seed, bool wide, Shape shape)
    {
        var rng = new Random(seed);
        var truths = new List<Truth>
        {
            Fixed(rng, "assinatura (lista)", "Netflix", "LAZER", 20.9, 59.9, 0, minCharges: 5, user: Ana),
            WithIncrease(rng, "assinatura (lista) com reajuste", "Spotify", "LAZER", 17.9, 34.9, Bruno),
            Fixed(rng, "assinatura genérica", "Streaming Exemplo", "LAZER", 14.9, 59.9, 1, minCharges: 5, user: Ana),
            Fixed(rng, "assinatura genérica", "Nuvem Exemplo", "OUTROS", 4.9, 19.9, 0, minCharges: 5, user: Bruno),
            Fixed(rng, "assinatura genérica", "Clube de Leitura Exemplo", "COMPRAS", 39.9, 79.9, 2, minCharges: 5, user: Ana),
            Variable(rng, "conta variável", "Energia Exemplo", "MORADIA", 120, 380, Ana),
            Fixed(rng, "conta fixa", "Internet Exemplo", "MORADIA", 89.9, 149.9, 1, minCharges: 5, user: Bruno),
            Fixed(rng, "aluguel", "Imobiliaria Exemplo", "MORADIA", 900, 3000, 1, minCharges: 5, user: Ana),
            Fixed(rng, "mensalidade", "Escola Exemplo", "OUTROS", 300, 1200, 2, minCharges: 5, user: Bruno),
            Yearly(rng, "anual", "Seguro Exemplo", "OUTROS", 300, 900),
            Installments(rng, "parcelas", "Loja de Moveis Exemplo", "COMPRAS", 80, 400),
            Installments(rng, "parcelas", "Magazine Exemplo", "COMPRAS", 40, 250),
        };

        if (wide)
        {
            truths.Add(SubscriptionPlusLoosePurchases(rng, "assinatura em loja com compra avulsa", "Loja de Aplicativos Exemplo", shape));
            truths.Add(Variable(rng, "conta variável, nome genérico, OUTROS", "Operadora Exemplo", "OUTROS", 60, 200));
            truths.Add(Weekly(rng, "plano semanal", "Plano Semanal Exemplo", "LAZER", 15, 80));
        }

        var months = wide || shape.Realistic ? 26 : 13;
        var habits = new List<(string Name, Profile Profile)>
        {
            ("Supermercado Exemplo", Market with { Months = months }),
            ("Mercadinho Exemplo", Market with { Min = 2, Max = 6, Amount = r => LogUniform(r, 20, 200), Months = months }),
            ("Restaurante Exemplo", Restaurant with { Months = months }),
            ("Pizzaria Exemplo", Restaurant with { Min = 1, Max = 4, Amount = r => LogUniform(r, 50, 180), Months = months }),
            ("Lanchonete Exemplo", Restaurant with { Min = 2, Max = 8, Amount = r => LogUniform(r, 15, 70), Months = months }),
            ("Entregas App Exemplo", Restaurant with { Min = 4, Max = 10, Amount = r => LogUniform(r, 25, 120), Months = months }),
            ("Posto Exemplo", FuelStation with { Months = months }),
            ("Padaria Exemplo", Bakery with { Months = months }),
            ("Farmacia Exemplo", Pharmacy with { Months = months }),
            ("Corridas App Exemplo", Rides with { Months = months }),
            ("Carona App Exemplo", Rides with { Min = 2, Max = 8, Months = months }),
            ("Loja de Roupas Exemplo", new Profile(string.Empty, string.Empty, "COMPRAS", 0, 2, r => LogUniform(r, 50, 400), months)),
            ("Petshop Exemplo", new Profile(string.Empty, string.Empty, "OUTROS", 1, 3, r => LogUniform(r, 40, 250), months)),
        };

        if (wide)
        {
            for (var i = 1; i <= 10; i++) habits.Add(($"Loja Ocasional {(char)('A' + i - 1)} Exemplo", Occasional));
            habits.Add(("Barbearia Exemplo", Service(rng)));
        }

        var list = habits.Select(h => (h.Name, MerchantKey.Normalize(h.Name), Habit(rng, h.Profile, shape, h.Name))).ToList();
        return (truths, list);
    }

    /// <summary>The numbers of a set of groups, as the person would see them.</summary>
    /// <param name="FalseInTotalAverage">Items in the total that are not one of the true ones, per group.</param>
    /// <param name="CleanGroups">Groups whose total has no false item.</param>
    /// <param name="TrueConfirmedAverage">True ones in the total, per group (of <paramref name="TrueCount"/>).</param>
    /// <param name="PossibleAverage">Items shown as possible, per group (true and false).</param>
    public sealed record GroupResult(
        int Groups,
        int TrueCount,
        decimal FalseInTotalAverage,
        int FalseInTotalWorst,
        int CleanGroups,
        decimal FalseShareOfTotalAverage,
        decimal TrueConfirmedAverage,
        decimal TruePossibleAverage,
        decimal TrueNotFoundAverage,
        decimal PossibleAverage,
        int PossibleWorst,
        decimal FalsePossibleAverage,
        IReadOnlyDictionary<string, int> FalseInTotalByPlace,
        IReadOnlyDictionary<string, (int Confirmed, int Possible)> TrueByPlace);

    public static GroupResult MeasureGroups(int seedBase, int groups, bool wide, Shape shape, RecurrenceOptions options)
    {
        var falseCount = new List<decimal>();
        var share = new List<decimal>();
        var trueConfirmed = new List<decimal>();
        var truePossible = new List<decimal>();
        var trueMissing = new List<decimal>();
        var possibleCount = new List<decimal>();
        var falsePossible = new List<decimal>();
        var falseByPlace = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var trueByPlace = new SortedDictionary<string, (int Confirmed, int Possible)>(StringComparer.Ordinal);
        var trueCount = 0;
        for (var s = 0; s < groups; s++)
        {
            var (truths, habits) = Group(seedBase + s, wide, shape);
            trueCount = truths.Count;
            var rows = truths.SelectMany(t => t.Rows).Concat(habits.SelectMany(h => h.Rows)).ToList();
            var items = Detect(rows, options).Where(x => x.Visible).ToList();
            var matched = new HashSet<Item>(ReferenceEqualityComparer.Instance);
            int confirmed = 0, possible = 0, listed = 0;
            foreach (var t in truths)
            {
                var match = items
                    .Where(x => OfKey(x, t.Key) && !matched.Contains(x) && x.Cadence == t.Cadence && (t.Kind is null ? x.Kind != RecurringKinds.Installment : x.Kind == t.Kind))
                    .Where(x => Math.Abs(x.Amount - t.Amount) <= 0.30m * t.Amount)
                    .OrderBy(x => x.InTotal ? 0 : x.Possible ? 1 : 2)
                    .ThenBy(x => Math.Abs(x.Amount - t.Amount)).FirstOrDefault();
                if (match is null) continue;
                matched.Add(match);
                var state = trueByPlace.GetValueOrDefault(t.Name);
                if (match.InTotal)
                {
                    confirmed++;
                    state.Confirmed++;
                }
                else if (match.Possible)
                {
                    possible++;
                    state.Possible++;
                }
                else
                {
                    listed++;
                }

                trueByPlace[t.Name] = state;
            }

            string NameOf(Item x) => habits.Where(h => OfKey(x, h.Key)).Select(h => h.Label).FirstOrDefault() ?? $"(a mais em verdadeiro: {x.Key})";
            var falseItems = items.Where(x => x.InTotal && !matched.Contains(x)).ToList();
            foreach (var x in falseItems) falseByPlace[NameOf(x)] = falseByPlace.GetValueOrDefault(NameOf(x)) + 1;
            var possibleItems = items.Where(x => x.Possible).ToList();

            var total = items.Where(x => x.InTotal).Sum(x => x.Monthly);
            var bad = falseItems.Sum(x => x.Monthly);
            falseCount.Add(falseItems.Count);
            share.Add(total == 0 ? 0 : 100m * bad / total);
            trueConfirmed.Add(confirmed);
            truePossible.Add(possible);
            trueMissing.Add(truths.Count - confirmed - possible - listed);
            possibleCount.Add(possibleItems.Count);
            falsePossible.Add(possibleItems.Count(x => !matched.Contains(x)));
        }

        return new GroupResult(
            groups,
            trueCount,
            falseCount.Average(),
            (int)falseCount.Max(),
            falseCount.Count(x => x == 0),
            share.Average(),
            trueConfirmed.Average(),
            truePossible.Average(),
            trueMissing.Average(),
            possibleCount.Average(),
            (int)possibleCount.Max(),
            falsePossible.Average(),
            falseByPlace,
            trueByPlace);
    }
}
