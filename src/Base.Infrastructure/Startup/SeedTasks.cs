using System.Security.Claims;
using Base.Application.Common.Abstractions;
using Base.Application.MasterData;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Settings;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Startup;

internal sealed partial class MigrateDatabaseTask(AppDbContext db, IOptions<DatabaseOptions> options, ILogger<MigrateDatabaseTask> logger) : IStartupTask
{
    public int Order => 0;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.Database.GetConnectionString());
        LogDatabase(logger, connection.DataSource, connection.InitialCatalog);

        if (!options.Value.MigrateOnStartup)
        {
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            LogMigrating(logger, pending.Count, pending);
            await db.Database.MigrateAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database: {Server} / {Database}")]
    private static partial void LogDatabase(ILogger logger, string server, string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} migration(s): {Migrations}")]
    private static partial void LogMigrating(ILogger logger, int count, IReadOnlyList<string> migrations);
}

/// <summary>Ensures the default tenant exists and makes it the tenant for the remaining startup tasks.</summary>
internal sealed class DefaultTenantSeedTask(AppDbContext db, ITenantContext tenant, IOptions<TenancyOptions> options) : IStartupTask
{
    public int Order => 10;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var code = settings.DefaultTenantCode.Trim().ToLowerInvariant();
        var existing = await db.Tenants.FirstOrDefaultAsync(t => t.Code == code, cancellationToken);
        if (existing is null)
        {
            existing = new Tenant(code, settings.DefaultTenantName);
            db.Tenants.Add(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        tenant.SetTenant(existing.Id, existing.Code);
    }
}

/// <summary>
/// Inserts a platform row for every catalogued setting and refreshes defaults nobody has changed.
/// Never overwrites a value an administrator has set.
/// </summary>
internal sealed class SettingsSeedTask(AppDbContext db) : IStartupTask
{
    public int Order => 20;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ApplicationSettings.IgnoreQueryFilters()
            .Where(s => s.TenantId == null)
            .ToDictionaryAsync(s => s.Code, StringComparer.Ordinal, cancellationToken);

        foreach (var definition in SettingCatalog.All)
        {
            if (rows.TryGetValue(definition.Code, out var row))
            {
                row.ResetToDefault(definition.DefaultValue);
            }
            else
            {
                db.ApplicationSettings.Add(new ApplicationSetting(definition.Code, definition.DefaultValue, tenantId: null));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Seeds roles. Administrators always hold every catalogued permission; other roles receive their defaults only
/// when first created, so later admin changes stick. Optionally creates the first administrator account.
/// </summary>
internal sealed partial class IdentitySeedTask(
    RoleManager<ApplicationRole> roles,
    UserManager<ApplicationUser> users,
    IOptions<SeedOptions> seed,
    ILogger<IdentitySeedTask> logger) : IStartupTask
{
    public int Order => 30;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var roleName in RoleNames.Seeded)
        {
            var role = await roles.FindByNameAsync(roleName);
            var isNew = role is null;
            if (role is null)
            {
                role = new ApplicationRole(roleName) { Description = RoleNames.DescriptionOf(roleName) };
                EnsureSucceeded(await roles.CreateAsync(role), $"create role {roleName}");
            }
            else if (role.Description is null)
            {
                role.Description = RoleNames.DescriptionOf(roleName);
                EnsureSucceeded(await roles.UpdateAsync(role), $"describe role {roleName}");
            }

            var isAdministrator = string.Equals(roleName, RoleNames.Administrator, StringComparison.Ordinal);
            if (!isNew && !isAdministrator)
            {
                continue;
            }

            var current = (await roles.GetClaimsAsync(role))
                .Where(c => string.Equals(c.Type, PermissionCatalog.ClaimType, StringComparison.Ordinal))
                .Select(c => c.Value)
                .ToHashSet(StringComparer.Ordinal);

            var wanted = isAdministrator
                ? PermissionCatalog.All.Select(p => p.Key)
                : PermissionCatalog.DefaultsFor(roleName);

            foreach (var permission in wanted.Where(p => !current.Contains(p)))
            {
                EnsureSucceeded(await roles.AddClaimAsync(role, new Claim(PermissionCatalog.ClaimType, permission)), $"grant {permission}");
            }
        }

        await SeedAdministratorAsync();
    }

    /// <summary>Idempotent: creates the configured administrator if missing and makes sure it holds the Administrator role.</summary>
    private async Task SeedAdministratorAsync()
    {
        var options = seed.Value;
        if (string.IsNullOrWhiteSpace(options.AdminEmail))
        {
            return;
        }

        var admin = await users.FindByEmailAsync(options.AdminEmail);
        if (admin is null)
        {
            if (string.IsNullOrWhiteSpace(options.AdminPassword))
            {
                return;
            }

            admin = new ApplicationUser
            {
                UserName = options.AdminEmail,
                Email = options.AdminEmail,
                EmailConfirmed = true,
                DisplayName = "Administrator",
            };

            EnsureSucceeded(await users.CreateAsync(admin, options.AdminPassword), "create administrator");
            LogAdminCreated(logger, options.AdminEmail);
        }

        if (!await users.IsInRoleAsync(admin, RoleNames.Administrator))
        {
            EnsureSucceeded(await users.AddToRoleAsync(admin, RoleNames.Administrator), "assign Administrator role");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Identity seed failed to {action}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seeded administrator account {Email}. Change its password after first sign-in.")]
    private static partial void LogAdminCreated(ILogger logger, string email);
}

/// <summary>Example plans so the subscription screens have something to show. Only when <c>Seed:SamplePlans</c> is on and no plan exists.</summary>
internal sealed class SamplePlansSeedTask(AppDbContext db, IOptions<SeedOptions> seed) : IStartupTask
{
    public int Order => 40;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!seed.Value.SamplePlans || await db.Plans.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        db.Plans.AddRange(
            Build("starter", "Starter", "For small teams getting started.", 499m, 4990m, 14, 10, users: 5, storageMb: 1024, dataExchange: false, branding: false),
            Build("professional", "Professional", "Everything a growing organisation needs.", 1499m, 14990m, 14, 20, users: 25, storageMb: 10240, dataExchange: true, branding: true),
            Build("enterprise", "Enterprise", "No limits, for large organisations.", 4999m, 49990m, 30, 30, users: null, storageMb: null, dataExchange: true, branding: true));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Plan Build(string code, string name, string description, decimal monthly, decimal annual, int trialDays, int order, long? users, long? storageMb, bool dataExchange, bool branding)
    {
        var plan = new Plan(code, name, "ZAR", monthly, annual, trialDays);
        plan.Update(name, description, "ZAR", monthly, annual, trialDays, order);
        plan.SetEntitlement(FeatureCodes.Documents, 1);
        plan.SetEntitlement(FeatureCodes.DataExchange, dataExchange ? 1 : 0);
        plan.SetEntitlement(FeatureCodes.CustomBranding, branding ? 1 : 0);
        plan.SetEntitlement(FeatureCodes.Api, branding ? 1 : 0);
        plan.SetEntitlement(LimitCodes.Users, users);
        plan.SetEntitlement(LimitCodes.StorageMb, storageMb);
        return plan;
    }
}

/// <summary>Gives every tenant the lookup catalogue's starting values it doesn't have yet (new lists, new defaults).</summary>
internal sealed class LookupDefaultsSeedTask(AppDbContext db, ILookupDefaults defaults) : IStartupTask
{
    public int Order => 45;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var tenantId in await db.Tenants.Select(t => t.Id).ToListAsync(cancellationToken))
        {
            await defaults.EnsureAsync(tenantId, cancellationToken);
        }
    }
}
