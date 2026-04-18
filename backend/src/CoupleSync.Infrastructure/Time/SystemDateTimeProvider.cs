using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Time;

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
