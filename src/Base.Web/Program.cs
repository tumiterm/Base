using System.Globalization;
using Base.Application;
using Base.Infrastructure;
using Base.Infrastructure.Startup;
using Base.Web;
using Base.Web.Observability;
using Serilog;

// Composition root only: each layer registers its own services.
Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddObservability();
    builder.Services
        .AddWeb(builder.Configuration, builder.Environment)
        .AddApplication()
        .AddInfrastructure(builder.Configuration, builder.Environment);

    var app = builder.Build();
    app.UseWeb();

    await app.RunStartupTasksAsync();
    await app.RunAsync();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "Host terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker for <c>WebApplicationFactory&lt;Program&gt;</c> in integration tests.</summary>
public partial class Program;
