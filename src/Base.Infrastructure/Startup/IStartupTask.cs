using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Base.Infrastructure.Startup;

/// <summary>Work that must finish before the app serves requests (migrate, seed). Runs once, in <see cref="Order"/>.</summary>
public interface IStartupTask
{
    int Order { get; }

    Task ExecuteAsync(CancellationToken cancellationToken);
}

public static class StartupTaskExtensions
{
    /// <summary>Runs every registered <see cref="IStartupTask"/> in its own scope. Call before <c>app.Run()</c>.</summary>
    public static async Task RunStartupTasksAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        await using var scope = host.Services.CreateAsyncScope();
        foreach (var task in scope.ServiceProvider.GetServices<IStartupTask>().OrderBy(t => t.Order))
        {
            await task.ExecuteAsync(cancellationToken);
        }
    }
}
