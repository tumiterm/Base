using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Base.Web.Observability;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Serilog for logs (console + rolling file, configured in appsettings "Serilog"), OpenTelemetry for traces and
    /// metrics. Set <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> to ship them to any OTLP backend (Aspire dashboard, Grafana, Seq…).
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", builder.Environment.ApplicationName));

        builder.Services.AddOptions<ObservabilityLinks>().BindConfiguration(ObservabilityLinks.SectionName);

        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddSource("Microsoft.EntityFrameworkCore"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Microsoft.EntityFrameworkCore", "Base")); // EF Core's own metrics; Base: slow commands

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
