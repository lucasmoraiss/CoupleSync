using System.Diagnostics;
using System.Runtime.CompilerServices;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CoupleSync.PostgresTests;

/// <summary>
/// SAFETY: these tests only ever talk to the PostgreSQL container they start themselves. The machine's DATABASE_URL
/// (which on a developer machine can point to the production cloud database) is replaced by an unreachable value when the assembly loads (TestDatabaseIsolation), nothing
/// reads .env or appsettings.Development.json (the host runs in the "Testing" environment), and every connection
/// string handed to a host or a DbContext goes through <see cref="PostgresServer.Guard"/> first.
/// </summary>
internal static class PostgresTestEnvironment
{
    /// <summary>Set to 1 (CI does) to turn "Docker is not available" from a skip into a failure.</summary>
    public const string RequireVariable = "COUPLESYNC_REQUIRE_POSTGRES_TESTS";

    [ModuleInitializer]
    internal static void Isolate()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
    }

    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "1", StringComparison.Ordinal);

    private static readonly Lazy<string?> DockerProblem = new(CheckDocker);

    /// <summary>Why the container tests cannot run here, or null when Docker answers.</summary>
    public static string? UnavailableReason => Required ? null : DockerProblem.Value;

    private static string? CheckDocker()
    {
        try
        {
            var info = new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info)!;
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return "Docker did not answer in time; the PostgreSQL (Testcontainers) tests were skipped.";
            }

            return process.ExitCode == 0
                ? null
                : "Docker is not running on this machine; the PostgreSQL (Testcontainers) tests were skipped.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Docker is not installed on this machine; the PostgreSQL (Testcontainers) tests were skipped.";
        }
    }
}

/// <summary>A [Fact] that is skipped, with a clear reason, when Docker is not available.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() => Skip = PostgresTestEnvironment.UnavailableReason;
}

/// <summary>A [Theory] that is skipped, with a clear reason, when Docker is not available.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = PostgresTestEnvironment.UnavailableReason;
}

/// <summary>One PostgreSQL container for the whole test run (same major version as production: 16).</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string Image = "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    public string Host { get; private set; } = "";

    public int Port { get; private set; }

    public async Task InitializeAsync()
    {
        if (PostgresTestEnvironment.UnavailableReason is not null)
        {
            return; // every test is skipped; nothing to start
        }

        _container = new PostgreSqlBuilder(Image)
            .WithUsername("couplesync_test")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
        await _container.StartAsync();
        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(5432);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Fails fast unless the connection string points at this container (never at any other database).</summary>
    public void Guard(string connectionString)
    {
        if (_container is null)
        {
            throw new InvalidOperationException("The PostgreSQL test container is not running.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Host, Host, StringComparison.OrdinalIgnoreCase) || builder.Port != Port)
        {
            throw new InvalidOperationException(
                $"Refusing to connect to {builder.Host}:{builder.Port}: the tests may only use their own container at {Host}:{Port}.");
        }
        // Exact host and port of the container: that already excludes every other server, cloud databases included.
    }

    /// <summary>A new, empty database inside the container (each test gets its own).</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(_container!.GetConnectionString());
        Guard(admin.ConnectionString);
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name}";
            await command.ExecuteNonQueryAsync();
        }

        admin.Database = name;
        admin.Pooling = true;
        admin.MaxPoolSize = 40;
        Guard(admin.ConnectionString);
        return new TestDatabase(this, name, admin.ConnectionString);
    }
}

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly PostgresServer _server;
    private readonly string _name;

    internal TestDatabase(PostgresServer server, string name, string connectionString)
    {
        _server = server;
        _name = name;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public PostgresServer Server => _server;

    public async Task<NpgsqlConnection> OpenAsync()
    {
        _server.Guard(ConnectionString);
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Pooling = false };
            _server.Guard(admin.ConnectionString);
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {_name} WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // The container is removed at the end of the run anyway.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresServer>
{
    public const string Name = "postgres";
}
