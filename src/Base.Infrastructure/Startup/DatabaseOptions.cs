namespace Base.Infrastructure.Startup;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending migrations at startup. Convenient in development; prefer a deployment step in production.</summary>
    public bool MigrateOnStartup { get; set; }

    public int CommandTimeoutSeconds { get; set; } = 30;

    public int MaxRetryCount { get; set; } = 5;

    /// <summary>Commands slower than this are logged as warnings with their SQL and trace id; 0 turns it off.</summary>
    public int SlowQueryThresholdMs { get; set; } = 500;
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Creates this administrator when the default tenant has no users. Keep the password in user-secrets.</summary>
    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}
