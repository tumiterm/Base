using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Base.Application.Files;
using Base.Application.Operations;
using Base.Application.Settings;
using Base.Infrastructure.Documents;
using Base.Infrastructure.Files;
using Base.Infrastructure.Notifications;
using Base.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Health;

/// <summary>Tag for checks of things the app depends on but can limp along without; only the database gates readiness.</summary>
public static class HealthTags
{
    public const string Ready = "ready";
    public const string Dependency = "dependency";
}

/// <summary>Every storage provider answers a cheap existence query.</summary>
internal sealed class FileStorageHealthCheck(IFileStorageProviderResolver providers) : IHealthCheck
{
    private const string ProbeKey = "health/probe";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var failed = new List<string>();
        foreach (var name in providers.Names.Order(StringComparer.Ordinal))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await providers.Get(name).ExistsAsync(ProbeKey, cancellationToken);
                data[name] = $"OK ({stopwatch.ElapsedMilliseconds} ms)";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                data[name] = exception.Message;
                failed.Add(name);
            }
        }

        return failed.Count == 0
            ? HealthCheckResult.Healthy($"{data.Count} provider(s) reachable.", data)
            : HealthCheckResult.Unhealthy($"Unreachable: {string.Join(", ", failed)}.", data: data);
    }
}

/// <summary>How full each tenant's database file storage is against <c>Files.Database.TenantQuotaMb</c>: degraded at 80%.</summary>
internal sealed class DatabaseFileQuotaHealthCheck(AppDbContext db, IApplicationSettingService settings) : IHealthCheck
{
    internal const double WarnAt = 0.8;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var quotaMb = await settings.GetIntAsync(SettingCodes.Files.DatabaseTenantQuotaMb, cancellationToken);
        var usage = await db.FileContents.GroupBy(c => c.TenantId).Select(g => new { Tenant = g.Key, Bytes = g.Sum(c => c.SizeBytes) }).ToListAsync(cancellationToken);
        var largest = usage.Count == 0 ? 0 : usage.Max(u => u.Bytes);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Quota per tenant"] = quotaMb == 0 ? "none" : $"{quotaMb} MB",
            ["Largest tenant"] = $"{largest / 1024d / 1024d:0.##} MB",
            ["Tenants using it"] = usage.Count,
        };
        if (quotaMb == 0)
        {
            return HealthCheckResult.Healthy("No quota set.", data);
        }

        var ratio = largest / (quotaMb * 1024d * 1024d);
        var description = string.Create(CultureInfo.InvariantCulture, $"Fullest tenant at {ratio * 100:0}% of its quota.");
        return ratio >= 1 ? HealthCheckResult.Unhealthy(description, data: data)
            : ratio >= WarnAt ? HealthCheckResult.Degraded(description, data: data)
            : HealthCheckResult.Healthy(description, data);
    }
}

/// <summary>The providers of every switched-on channel accept a TCP connection (no message is sent, no credentials used).</summary>
internal sealed class NotificationProvidersHealthCheck(NotificationProviderRegistry registry, IOptions<NotificationOptions> options) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var failed = new List<string>();
        var probes = registry.All
            .Where(p => options.Value.For(p.Channel).Enabled)
            .Select(p => (Key: $"{p.Channel}/{p.Name}", Provider: registry.Find(p.Name, p.Channel)))
            .Where(p => p.Provider is IProviderEndpoint)
            .OrderBy(p => p.Key, StringComparer.Ordinal);
        foreach (var (key, provider) in probes)
        {
            var endpoint = ((IProviderEndpoint)provider!).Endpoint;
            var port = endpoint.IsDefaultPort && string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : endpoint.Port;
            try
            {
                var elapsed = await TcpProbe.ConnectAsync(endpoint.Host, port, Timeout, cancellationToken);
                data[key] = $"{endpoint.Host}:{port} OK ({elapsed.TotalMilliseconds:0} ms)";
            }
            catch (Exception exception) when (exception is SocketException or IOException or TimeoutException)
            {
                data[key] = $"{endpoint.Host}:{port} {exception.Message}";
                failed.Add(key);
            }
        }

        if (data.Count == 0)
        {
            return HealthCheckResult.Healthy("No external providers switched on (log and in-app only).");
        }

        // Degraded, not unhealthy: messages are queued and retried, and channels fall back.
        return failed.Count == 0
            ? HealthCheckResult.Healthy($"{data.Count} provider(s) reachable.", data)
            : HealthCheckResult.Degraded($"Unreachable: {string.Join(", ", failed)}.", data: data);
    }
}

internal sealed class GotenbergHealthCheck(IHttpClientFactory httpClients) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClients.CreateClient(GotenbergPdfRenderer.HttpClientName).GetAsync(new Uri("health", UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("PDF rendering is available.")
                : HealthCheckResult.Degraded($"Gotenberg answered {(int)response.StatusCode}; PDFs can't be produced.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Degraded("Gotenberg is unreachable; PDFs can't be produced.", exception);
        }
    }
}

/// <summary>clamd answers PING. Unhealthy: uploads are refused while it is down.</summary>
internal sealed class ClamAvHealthCheck(IOptions<ClamAvOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host!, settings.Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zPING\0"u8.ToArray(), timeout.Token);
            var buffer = new byte[32];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            var reply = Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\0', '\n');
            return string.Equals(reply, "PONG", StringComparison.Ordinal)
                ? HealthCheckResult.Healthy("Virus scanning is available.")
                : HealthCheckResult.Unhealthy($"Unexpected reply '{reply}'; uploads are refused.");
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("ClamAV is unreachable; uploads are refused.", exception);
        }
    }
}

/// <summary>The outbox is keeping up: degraded when messages wait over 15 minutes or some were dead-lettered.</summary>
internal sealed class OutboxHealthCheck(AppDbContext db, TimeProvider time) : IHealthCheck
{
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var pending = db.OutboxMessages.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null);
        var count = await pending.CountAsync(cancellationToken);
        var oldest = count == 0 ? (DateTimeOffset?)null : await pending.MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);
        var deadLettered = await db.OutboxMessages.CountAsync(m => m.DeadLetteredAt != null, cancellationToken);
        var age = oldest is { } o ? now - o : TimeSpan.Zero;
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Pending"] = count,
            ["Oldest pending"] = oldest is null ? "-" : $"{age.TotalMinutes:0} min",
            ["Dead-lettered"] = deadLettered,
        };

        if (deadLettered > 0)
        {
            return HealthCheckResult.Degraded($"{deadLettered} message(s) gave up after every retry and need attention.", data: data);
        }

        return age > MaxWait
            ? HealthCheckResult.Degraded($"Messages have waited {age.TotalMinutes:0} minutes: the processor is behind or stopped.", data: data)
            : HealthCheckResult.Healthy($"{count} pending.", data);
    }
}

internal sealed class ScheduledJobsHealthCheck(IJobScheduler scheduler, TimeProvider time) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var jobs = await scheduler.StatusAsync(cancellationToken);
        var now = time.GetUtcNow();
        var data = jobs.ToDictionary(
            j => j.Name,
            j => (object)(j.LastRun is { } run ? $"{(run.Succeeded ? "OK" : "FAILED")} {run.StartedAt.ToString("u", CultureInfo.InvariantCulture)}" : "not run yet"),
            StringComparer.Ordinal);
        var attention = jobs.Where(j => j.NeedsAttention(now)).Select(j => j.Name).ToList();
        return attention.Count == 0
            ? HealthCheckResult.Healthy($"{jobs.Count} job(s) on schedule.", data)
            : HealthCheckResult.Degraded($"Failed or overdue: {string.Join(", ", attention)}.", data: data);
    }
}

/// <summary>Hangfire's own failed-job count (only when Hangfire is enabled).</summary>
internal sealed class HangfireHealthCheck(JobStorage storage) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var monitoring = storage.GetMonitoringApi();
        var failed = monitoring.FailedCount();
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Failed"] = failed,
            ["Enqueued"] = monitoring.Queues().Sum(q => q.Length),
            ["Servers"] = monitoring.Servers().Count,
        };
        return Task.FromResult(failed > 0
            ? HealthCheckResult.Degraded($"{failed} Hangfire job(s) failed; see the job dashboard.", data: data)
            : HealthCheckResult.Healthy("No failed jobs.", data));
    }
}

internal static class TcpProbe
{
    public static async Task<TimeSpan> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, linked.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer within {timeout.TotalSeconds:0} s.", exception);
        }

        return stopwatch.Elapsed;
    }
}
