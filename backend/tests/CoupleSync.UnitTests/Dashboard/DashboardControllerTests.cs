using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CoupleSync.Api.Controllers;
using CoupleSync.Application.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Dashboard;

public class DashboardControllerTests
{
    private readonly DashboardController _controller;

    public DashboardControllerTests()
    {
        var repo = new FakeDashboardRepository();
        var dateTimeProvider = new FixedDateTimeProvider(new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc));
        var handler = new GetDashboardQueryHandler(repo, dateTimeProvider);
        _controller = new DashboardController(handler);
        
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("couple_id", Guid.NewGuid().ToString())
        }, "mock"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    [Fact]
    public async Task GetDashboard_StartDateAfterEndDate_ReturnsBadRequest()
    {
        // Arrange
        var startDate = new DateTime(2023, 02, 01, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(2023, 01, 01, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await _controller.GetDashboard(startDate, endDate, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("startDate must not be after endDate", badRequestResult.Value);
    }
}
