namespace Base.Infrastructure.Jobs;

/// <summary>Optional Hangfire module (SQL Server storage). Off by default; enable per project.</summary>
public sealed class HangfireOptions
{
    public const string SectionName = "Hangfire";

    public bool Enabled { get; set; }

    public string ServerName { get; set; } = "base";

    /// <summary>0 = Hangfire default (processor count × 5, capped).</summary>
    public int WorkerCount { get; set; }

    public IReadOnlyList<string> Queues { get; set; } = ["default"];

    public string DashboardPath { get; set; } = "/jobs";
}
