using System.Globalization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Base.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Base.Web.Api;

public enum ApiDocumentation
{
    /// <summary>No OpenAPI document or reference page.</summary>
    Off = 0,

    /// <summary>Only for signed-in people and API clients.</summary>
    SignedIn = 1,

    /// <summary>Anyone can read the document and the reference page.</summary>
    Public = 2,
}

/// <summary>Whether this deployment serves the OpenAPI document and reference page (for links in the UI).</summary>
public sealed record ApiDocumentationState(ApiDocumentation Mode)
{
    public bool Enabled => Mode != ApiDocumentation.Off;
}

/// <summary>The <c>Api</c> configuration section.</summary>
public sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>Default: Public in Development, Off elsewhere. Exposing the API's shape is a choice, not a default.</summary>
    public ApiDocumentation? Documentation { get; set; }

    /// <summary>Requests a minute for a signed-in person using the API (API clients use their own limit).</summary>
    public int UserRequestsPerMinute { get; set; } = 1200;

    /// <summary>Requests a minute per IP address for calls that aren't signed in.</summary>
    public int AnonymousRequestsPerMinute { get; set; } = 120;

    /// <summary>Browser origins allowed to call the API (CORS). Empty: none (server-to-server and same-site only).</summary>
    public IList<string> AllowedOrigins { get; } = [];
}

internal static class ApiExtensions
{
    public const string CorsPolicy = "api";

    public static IServiceCollection AddApiHardening(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
        services.Configure<ApiOptions>(configuration.GetSection(ApiOptions.SectionName));
        services.AddSingleton(new ApiDocumentationState(options.Documentation ?? (environment.IsDevelopment() ? ApiDocumentation.Public : ApiDocumentation.Off)));

        services.AddApiVersioning(o =>
            {
                // Unversioned calls are v1, so existing clients keep working; responses list the supported versions.
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.AssumeDefaultVersionWhenUnspecified = true;
                o.ReportApiVersions = true;
                o.ApiVersionReader = ApiVersionReader.Combine(new HeaderApiVersionReader("api-version"), new QueryStringApiVersionReader("api-version"));
            })
            .AddMvc()
            .AddApiExplorer(o => o.GroupNameFormat = "'v'VVV")
            .AddOpenApi(o => o.Document
                .AddDocumentTransformer<ApiDocumentTransformer>()
                .AddOperationTransformer<ApiOperationTransformer>());

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, options));
            o.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ProblemDetails { Status = 429, Title = "Too many requests", Detail = "Slow down and retry after the number of seconds in Retry-After." },
                    options: null,
                    contentType: "application/problem+json",
                    token);
            };
        });

        if (options.AllowedOrigins.Count > 0)
        {
            services.AddCors(o => o.AddPolicy(CorsPolicy, policy => policy
                .WithOrigins([.. options.AllowedOrigins])
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                .WithHeaders("Authorization", "Content-Type", ApiKeyAuthenticationHandler.HeaderName, IdempotencyMiddleware.HeaderName, "api-version", "X-Correlation-ID")
                .WithExposedHeaders("api-supported-versions", "api-deprecated-versions", "Retry-After", "X-Correlation-ID", IdempotencyMiddleware.ReplayedHeader)));
        }

        return services;
    }

    public static WebApplication MapApiDocumentation(this WebApplication app)
    {
        var mode = app.Services.GetRequiredService<ApiDocumentationState>().Mode;
        if (mode == ApiDocumentation.Off)
        {
            return app;
        }

        var document = app.MapOpenApi().WithDocumentPerVersion();
        var reference = app.MapScalarApiReference(o => o.WithTitle("API reference").AddDocument("v1"));
        if (mode == ApiDocumentation.Public)
        {
            document.AllowAnonymous();
            reference.AllowAnonymous();
        }

        return app;
    }

    /// <summary>One bucket per API client, signed-in person or anonymous IP; pages aren't limited here.</summary>
    private static RateLimitPartition<string> Partition(HttpContext context, ApiOptions options)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("pages");
        }

        var caller = ApiCaller.Of(context);
        var limit = int.TryParse(context.User.FindFirst(AppClaimTypes.RateLimit)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var own) ? own
            : caller.StartsWith("user:", StringComparison.Ordinal) ? options.UserRequestsPerMinute
            : options.AnonymousRequestsPerMinute;

        // A minute's worth of burst, refilled every second: steady traffic is never cut off at a window boundary.
        return RateLimitPartition.GetTokenBucketLimiter($"{caller}:{limit}", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit,
            TokensPerPeriod = Math.Max(1, limit / 60),
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}

/// <summary>Titles the document and declares how to authenticate: an API key, or the browser's sign-in cookie.</summary>
internal sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Info.Title = "Base API";
        document.Info.Description =
            "Authenticate with an API key (Administration → API clients) as `Authorization: Bearer bk_…` or `X-Api-Key`. " +
            "Unsafe calls accept an `Idempotency-Key` header and are safe to retry with it. Send `api-version` to pin a version (default 1.0). " +
            "Errors are RFC 9457 problem details. Too many requests get 429 with `Retry-After`.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[ApiKeyAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "bk_{prefix}_{secret}",
            Description = "An API client's key. It acts with that client's permissions in its tenant.",
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, document)] = [] });
        return Task.CompletedTask;
    }
}

/// <summary>Documents the Idempotency-Key header on unsafe operations and the shared error responses.</summary>
internal sealed class ApiOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        var method = context.Description.HttpMethod ?? "GET";
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotencyMiddleware.HeaderName,
                In = ParameterLocation.Header,
                Required = false,
                Description = "A unique value (e.g. a UUID). Retrying with the same key returns the first response instead of doing the work again.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100 },
            });
        }

        operation.Responses ??= [];
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing, invalid, revoked or expired credentials." });
        operation.Responses.TryAdd("429", new OpenApiResponse { Description = "Rate limit reached; retry after Retry-After seconds." });
        return Task.CompletedTask;
    }
}
