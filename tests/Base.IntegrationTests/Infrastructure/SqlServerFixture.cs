using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Base.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Base.IntegrationTests.Infrastructure.SqlServerFixture))]

namespace Base.IntegrationTests.Infrastructure;

/// <summary>
/// The SQL Server the integration tests run on. Each test class gets its own database. Real SQL, not EF InMemory:
/// InMemory ignores query filters' SQL translation, row versions, transactions and unique indexes.
/// </summary>
/// <remarks>
/// Server, in order: the <c>BASE_TEST_SQLSERVER</c> connection string if set; a Testcontainers SQL Server (Docker);
/// on Windows without Docker, SQL Server LocalDB (installed with Visual Studio). Databases on a shared server are
/// dropped when the run ends.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ServerVariable = "BASE_TEST_SQLSERVER";

    private readonly ConcurrentBag<string> _databases = [];
    private MsSqlContainer? _container;
    private string _server = string.Empty;

    /// <summary>Where the tests run, for diagnostics: "container", "LocalDB" or "BASE_TEST_SQLSERVER".</summary>
    public string Source { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } configured)
        {
            (_server, Source) = (configured, ServerVariable);
            return;
        }

        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            (_server, Source) = (_container.GetConnectionString(), "container");
        }
        catch (DockerUnavailableException) when (OperatingSystem.IsWindows())
        {
            _container = null;
            (_server, Source) = (DevelopmentDatabase.LocalDb("master"), "LocalDB");
        }
    }

    public string NewDatabase()
    {
        var name = $"base_test_{Guid.NewGuid():N}";
        _databases.Add(name);
        return new SqlConnectionStringBuilder(_server) { InitialCatalog = name }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync(); // the databases go with the container
            return;
        }

        await DropDatabasesAsync();
    }

    [SuppressMessage("Security", "CA2100", Justification = "DDL can't take parameters; names are generated here as base_test_{guid} and bracket-quoted.")]
    private async Task DropDatabasesAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        foreach (var name in _databases)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await command.ExecuteNonQueryAsync();
        }
    }
}
