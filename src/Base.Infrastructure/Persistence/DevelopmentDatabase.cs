using Microsoft.Data.SqlClient;

namespace Base.Infrastructure.Persistence;

/// <summary>
/// The database used in Development when no connection string is configured, so the app runs with no setup:
/// SQL Server LocalDB on Windows (installed with Visual Studio), the docker-compose SQL Server elsewhere.
/// Never used outside Development.
/// </summary>
public static class DevelopmentDatabase
{
    public const string LocalDbServer = @"(localdb)\MSSQLLocalDB";
    public const string DockerServer = "localhost,1433";

    /// <summary>The docker-compose container's sa password; a local throwaway, not a secret.</summary>
    public const string DockerPassword = "Base_Dev_Password1!";

    /// <summary>
    /// The project's name ("Base", or "Acme.Payroll" in a project created from the base), so projects sharing one
    /// LocalDB instance get separate databases.
    /// </summary>
    public static string DatabaseName { get; } =
        typeof(DevelopmentDatabase).Assembly.GetName().Name!.Replace(".Infrastructure", string.Empty, StringComparison.Ordinal);

    public static string Default => For(OperatingSystem.IsWindows(), DatabaseName);

    public static string For(bool windows, string database) =>
        windows ? LocalDb(database) : Docker(database);

    public static string LocalDb(string database) => new SqlConnectionStringBuilder
    {
        DataSource = LocalDbServer,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
    }.ConnectionString;

    public static string Docker(string database) => new SqlConnectionStringBuilder
    {
        DataSource = DockerServer,
        InitialCatalog = database,
        UserID = "sa",
        Password = DockerPassword,
        TrustServerCertificate = true,
    }.ConnectionString;
}
