using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.UnitTests.Persistence;

public sealed class AppDbContextQueryFilterTests
{
    [Fact]
    public void AppDbContext_WithNullCoupleContext_CanBeConstructed()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        // Must not throw — covers design-time and background-job scenarios
        var act = () => new AppDbContext(options, coupleContext: null);

        var exception = Record.Exception(act);
        Assert.Null(exception);
    }

    [Fact]
    public void AppDbContext_WithCoupleContext_CanBeConstructed()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        var coupleContext = new StubCoupleContext(Guid.NewGuid());

        var act = () => new AppDbContext(options, coupleContext);

        var exception = Record.Exception(act);
        Assert.Null(exception);
    }

    [Fact]
    public void ICoupleScoped_InterfaceExists_WithCoupleIdProperty()
    {
        var properties = typeof(CoupleSync.Domain.Interfaces.ICoupleScoped).GetProperties();
        Assert.Contains(properties, p => p.Name == "CoupleId" && p.PropertyType == typeof(Guid));
    }

    private sealed class StubCoupleContext : ICoupleContext
    {
        public StubCoupleContext(Guid? coupleId) => CoupleId = coupleId;
        public Guid? CoupleId { get; }
    }
}
