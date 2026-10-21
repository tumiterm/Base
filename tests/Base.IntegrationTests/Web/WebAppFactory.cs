using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Web;

/// <summary>The real web host (Program.cs) against a fresh database, with a seeded administrator.</summary>
public sealed class WebAppFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@tests.local";
    public const string AdminPassword = "Correct-Horse-Battery-9";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Outbox:Enabled", "false");
        builder.UseSetting("Payments:Gateways:Test:Enabled", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("FileStorage:LocalDisk:RootPath", Path.Combine(Path.GetTempPath(), "base-tests", Guid.NewGuid().ToString("N")));
        builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly));
    }
}

/// <summary>Test-only endpoint that fails, to exercise the global exception handler.</summary>
[ApiController]
[Route("api/test/throw")]
public sealed class ThrowingController : ControllerBase
{
    [HttpGet]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "MVC actions must be instance methods.")]
    public IActionResult Get() => throw new InvalidOperationException("Deliberate failure with secret=hunter2");
}
