using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoupleSync.PostgresTests;

/// <summary>The PostgreSQL test host also runs in "Testing" (never loads appsettings.Development.json).</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresHostEnvironmentTests
{
    private readonly PostgresServer _server;

    public PostgresHostEnvironmentTests(PostgresServer server) => _server = server;

    [PostgresFact]
    public async Task ThePostgresHost_RunsInTheTestingEnvironment()
    {
        await using var database = await _server.CreateDatabaseAsync();
        await using var factory = new PostgresApiFactory(database);

        Assert.Equal("Testing", factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
    }
}
