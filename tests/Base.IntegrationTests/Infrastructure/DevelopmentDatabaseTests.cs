using Base.Infrastructure;
using Base.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Base.IntegrationTests.Infrastructure;

public sealed class DevelopmentDatabaseTests
{
    [Fact]
    public void Windows_uses_localdb_with_windows_authentication()
    {
        var connection = new SqlConnectionStringBuilder(DevelopmentDatabase.For(windows: true, "Base"));

        connection.DataSource.ShouldBe(@"(localdb)\MSSQLLocalDB");
        connection.InitialCatalog.ShouldBe("Base");
        connection.IntegratedSecurity.ShouldBeTrue();
        connection.Password.ShouldBeEmpty();
    }

    [Fact]
    public void Elsewhere_uses_the_docker_compose_server()
    {
        var connection = new SqlConnectionStringBuilder(DevelopmentDatabase.For(windows: false, "Base"));

        connection.DataSource.ShouldBe("localhost,1433");
        connection.UserID.ShouldBe("sa");
    }

    [Fact]
    public void The_database_is_named_after_the_project() => DevelopmentDatabase.DatabaseName.ShouldBe("Base");

    [Fact]
    public void A_configured_connection_string_always_wins() =>
        DependencyInjection.ResolveConnectionString(Config("Server=prod;Database=App"), new Env("Development")).ShouldBe("Server=prod;Database=App");

    [Fact]
    public void Development_falls_back_when_nothing_is_configured() =>
        DependencyInjection.ResolveConnectionString(Config(""), new Env("Development")).ShouldBe(DevelopmentDatabase.Default);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Other_environments_never_fall_back(string environment) =>
        Should.Throw<InvalidOperationException>(() => DependencyInjection.ResolveConnectionString(Config(""), new Env(environment)))
            .Message.ShouldContain("ConnectionStrings__Default");

    private static IConfiguration Config(string connectionString) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["ConnectionStrings:Default"] = connectionString }).Build();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Base.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
