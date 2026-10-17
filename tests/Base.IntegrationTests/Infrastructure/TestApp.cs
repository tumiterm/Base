using Base.Application;
using Base.Application.Common.Abstractions;
using Base.Application.Notifications;
using Base.Domain.Notifications;
using Base.Infrastructure;
using Base.Infrastructure.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Base.IntegrationTests.Infrastructure;

/// <summary>The Application + Infrastructure layers wired as in production, against a fresh database, with a fake clock.</summary>
public sealed class TestApp : IAsyncDisposable
{
    private TestApp(ServiceProvider services, FakeTimeProvider time, Guid defaultTenantId, SentMessages sent)
    {
        Sent = sent;
        Services = services;
        Time = time;
        DefaultTenantId = defaultTenantId;
    }

    public ServiceProvider Services { get; }

    public FakeTimeProvider Time { get; }

    public Guid DefaultTenantId { get; }

    /// <summary>Everything sent through the capturing notification providers (all channels are on and use them).</summary>
    public SentMessages Sent { get; }

    public static async Task<TestApp> CreateAsync(
        string connectionString,
        Action<IServiceCollection>? configure = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Database:MigrateOnStartup"] = "true",
                ["Outbox:Enabled"] = "false",
                ["FileStorage:LocalDisk:RootPath"] = Path.Combine(Path.GetTempPath(), "base-tests", Guid.NewGuid().ToString("N")),
                ["Notifications:Channels:Email:Enabled"] = "true",
                ["Notifications:Channels:Email:Provider"] = CapturingNotificationProvider.ProviderName,
                ["Notifications:Channels:Sms:Enabled"] = "true",
                ["Notifications:Channels:Sms:Provider"] = CapturingNotificationProvider.ProviderName,
                ["Notifications:Channels:WhatsApp:Enabled"] = "true",
                ["Notifications:Channels:WhatsApp:Provider"] = CapturingNotificationProvider.ProviderName,
                ["Notifications:Channels:InApp:Enabled"] = "true",
            })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>(StringComparer.Ordinal))
            .Build();

        var sent = new SentMessages();

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(time);
        services.AddApplication().AddInfrastructure(configuration, new TestEnvironment());
        foreach (var channel in new[] { NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp })
        {
            services.AddSingleton<INotificationProvider>(new CapturingNotificationProvider(channel, sent.All, sent.Failures));
        }

        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Guid tenantId;
        await using (var scope = provider.CreateAsyncScope())
        {
            foreach (var task in scope.ServiceProvider.GetServices<IStartupTask>().OrderBy(t => t.Order))
            {
                await task.ExecuteAsync(CancellationToken.None);
            }

            tenantId = scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId;
        }

        return new TestApp(provider, time, tenantId, sent);
    }

    /// <summary>Runs <paramref name="work"/> in a fresh DI scope (a new DbContext) as the given tenant.</summary>
    public async Task<T> InScopeAsync<T>(Guid? tenantId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(id, null);
        }

        return await work(scope.ServiceProvider);
    }

    public Task InScopeAsync(Guid? tenantId, Func<IServiceProvider, Task> work) =>
        InScopeAsync<bool>(tenantId, async sp =>
        {
            await work(sp);
            return true;
        });

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Base.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
