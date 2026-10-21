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
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly);
            services.AddScoped<Application.Payments.IPaymentGateway, RawBodyGateway>();
        });
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

/// <summary>
/// A gateway that trusts a notification only if its raw body and its parsed form both reached it intact, as a
/// signature check over the raw body needs.
/// </summary>
internal sealed class RawBodyGateway : Application.Payments.IPaymentGateway
{
    public string Code => "RawBody";

    public string Name => "Raw body";

    public IReadOnlyList<string> Methods { get; } = [];

    public IReadOnlyList<string> Currencies { get; } = [];

    public Task<Application.Payments.CheckoutStart> StartCheckoutAsync(Application.Payments.CheckoutRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Application.Payments.CheckoutStart.Failed("Not for checkouts."));

    public Task<Application.Payments.GatewayNotification> ReadNotificationAsync(Application.Payments.InboundNotification notification, CancellationToken cancellationToken) =>
        Task.FromResult(string.Equals(notification.Body, "reference=PNOBODY12345&note=a+b%26c", StringComparison.Ordinal) && notification.Form.GetValueOrDefault("note") == "a b&c"
            ? new Application.Payments.GatewayNotification(true, "raw-1", "PNOBODY12345", null, Application.Payments.GatewayNotificationKind.Paid, null, null)
            : Application.Payments.GatewayNotification.Rejected($"Body was '{notification.Body}'."));
}
