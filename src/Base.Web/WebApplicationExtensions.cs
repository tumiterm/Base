using System.Threading.RateLimiting;
using Base.Application.Common.Abstractions;
using Base.Infrastructure.Jobs;
using Base.Web.Errors;
using Base.Web.Http;
using Base.Web.Security;
using Base.Web.Tenancy;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;

namespace Base.Web;

internal static class WebApplicationExtensions
{
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddWeb(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Cookies are HTTPS-only everywhere except plain-HTTP local development and tests.
        var cookieSecurity = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddControllersWithViews(options =>
        {
            // Every unsafe request (POST/PUT/PATCH/DELETE) needs an antiforgery token, API calls included,
            // because the API is authenticated by the same cookie. site.js sends it as a header.
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "base.csrf";
            options.Cookie.SecurePolicy = cookieSecurity;
        });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
            ctx.ProblemDetails.Extensions["correlationId"] = CorrelationIdMiddleware.Get(ctx.HttpContext));
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddSingleton<ErrorAlertQueue>();
        services.AddHostedService<ErrorAlertSender>();
        services.AddMemoryCache();

        AddAuth(services, cookieSecurity);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            var proxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
            }
        });

        services.AddOpenApi();
        return services;
    }

    private static void AddAuth(IServiceCollection services, CookieSecurePolicy cookieSecurity)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "base.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = cookieSecurity;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";

            // APIs answer 401/403; only pages redirect to the login screen.
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = ctx => ApiAwareRedirect(ctx, StatusCodes.Status401Unauthorized),
                OnRedirectToAccessDenied = ctx => ApiAwareRedirect(ctx, StatusCodes.Status403Forbidden),
            };
        });

        // Re-issues the principal (roles, permissions) from the database at this interval: permission changes take
        // effect without a logout, and locked or deleted users are signed out.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()); // secure by default
    }

    private static Task ApiAwareRedirect(RedirectContext<CookieAuthenticationOptions> context, int apiStatusCode)
    {
        if (GlobalExceptionHandler.IsApiRequest(context.Request))
        {
            context.Response.StatusCode = apiStatusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }

    /// <summary>The middleware pipeline. Order matters; see docs/architecture.md.</summary>
    public static WebApplication UseWeb(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandlingPath = "/error",
            SuppressDiagnosticsCallback = _ => true, // GlobalExceptionHandler already logged it once, with the ticket
        });
        app.UseStatusCodePagesWithReExecute("/error/{0}");
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();
        app.MapStaticAssets();

        app.UseRouting();
        app.UseRateLimiter();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference().AllowAnonymous();
        }

        var hangfire = app.Services.GetRequiredService<IOptions<HangfireOptions>>().Value;
        if (hangfire.Enabled)
        {
            app.UseHangfireDashboard(hangfire.DashboardPath, new DashboardOptions { Authorization = [new HangfireDashboardAuthorization()] });
        }

        app.MapControllers();
        app.MapDefaultControllerRoute().WithStaticAssets();
        return app;
    }
}
