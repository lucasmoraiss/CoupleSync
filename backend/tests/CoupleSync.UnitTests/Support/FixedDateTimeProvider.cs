using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.UnitTests.Support;

public sealed class FixedDateTimeProvider : IDateTimeProvider
{
    public FixedDateTimeProvider(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; }
}
