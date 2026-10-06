namespace CoupleSync.Domain.ValueObjects;

/// <summary>
/// The single place where "which month/day is it" is decided: the calendar is Brasília time
/// (America/Sao_Paulo), while everything is stored in UTC. A transaction at 22:00 of the last day
/// of a month in Brasília belongs to that month even though it is already the next day in UTC.
/// </summary>
public static class BrazilTime
{
    public const string IanaId = "America/Sao_Paulo";
    private const string WindowsId = "E. South America Standard Time";

    private static readonly TimeZoneInfo Zone = ResolveZone();

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { IanaId, WindowsId })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // Minimal containers (e.g. Alpine without tzdata) have no zone database. Brazil has had
        // no daylight saving time since 2019, so a fixed UTC-3 is exact for current data.
        return TimeZoneInfo.CreateCustomTimeZone(IanaId, TimeSpan.FromHours(-3), IanaId, IanaId);
    }

    /// <summary>The Brasília wall-clock time of a UTC instant (Kind Unspecified).</summary>
    public static DateTime ToLocal(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(asUtc, Zone);
    }

    /// <summary>The UTC instant of a Brasília wall-clock time.</summary>
    public static DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone), DateTimeKind.Utc);
    }

    /// <summary>"yyyy-MM" of the Brasília month that contains the UTC instant.</summary>
    public static string MonthOf(DateTime utc)
    {
        var local = ToLocal(utc);
        return $"{local.Year:D4}-{local.Month:D2}";
    }

    /// <summary>First instant (UTC) of the Brasília month.</summary>
    public static DateTime MonthStartUtc(int year, int month)
        => ToUtc(new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified));

    /// <summary>First instant (UTC) of the Brasília month containing the UTC instant.</summary>
    public static DateTime MonthStartUtc(DateTime utc)
    {
        var local = ToLocal(utc);
        return MonthStartUtc(local.Year, local.Month);
    }

    /// <summary>[start, end) in UTC of a "yyyy-MM" Brasília month; end is the start of the next month.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) MonthRangeUtc(string month)
    {
        var (year, monthNumber) = ParseMonth(month);
        var start = MonthStartUtc(year, monthNumber);
        var next = new DateTime(year, monthNumber, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
        return (start, MonthStartUtc(next.Year, next.Month));
    }

    /// <summary>"yyyy-MM" shifted by a number of months (negative goes back).</summary>
    public static string AddMonths(string month, int delta)
    {
        var (year, monthNumber) = ParseMonth(month);
        var shifted = new DateTime(year, monthNumber, 1).AddMonths(delta);
        return $"{shifted.Year:D4}-{shifted.Month:D2}";
    }

    private static (int Year, int Month) ParseMonth(string month)
    {
        var parts = month.Split('-');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}
