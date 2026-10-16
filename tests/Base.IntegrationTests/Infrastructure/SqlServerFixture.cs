using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Base.IntegrationTests.Infrastructure.SqlServerFixture))]

namespace Base.IntegrationTests.Infrastructure;

/// <summary>
/// One real SQL Server container for the whole test run; each test class gets its own database. Real SQL, not EF
/// InMemory: InMemory ignores query filters' SQL translation, row versions, transactions and unique indexes.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public string NewDatabase() =>
        new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = $"base_test_{Guid.NewGuid():N}" }.ConnectionString;
}
