using Base.Application.Common.Abstractions;
using Base.Domain.Auditing;
using Base.Domain.Files;
using Base.Domain.Settings;
using Base.Domain.Tenancy;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence.Conventions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Persistence;

/// <summary>
/// The application's EF Core context. Business code never sees it: Application talks to <c>IUnitOfWork</c>/<c>IRepository</c>.
/// Model configuration lives in <c>IEntityTypeConfiguration</c> classes; cross-cutting rules in <see cref="EntityConventions"/>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Read by the tenant query filter on every query (EF parameterises context members).</summary>
    public Guid CurrentTenantId => tenant.TenantId;

    /// <summary>True inside <see cref="ITenantContext.BeginSystemScope"/>; lifts the tenant filter.</summary>
    public bool IsSystemScope => tenant.IsSystemScope;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Last, so conventions see every entity: row version, audit defaults, named soft-delete + tenant filters.
        builder.ApplyEntityConventions(this);

        // Audit entries aren't ITenantOwned (platform changes have no tenant), so they get their own Tenant filter:
        // a tenant sees its own entries; platform entries (TenantId null) are visible only in system scope.
        builder.Entity<AuditEntry>().HasQueryFilter(QueryFilters.Tenant, e => IsSystemScope || e.TenantId == CurrentTenantId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(4000);
    }
}
