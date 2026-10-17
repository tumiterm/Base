using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Application.Releases;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Application.Users;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Tenancy;

internal sealed partial class TenantAdministrationService(
    AppDbContext db,
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ITenantResolver resolver,
    IPlatformTenant platform,
    ISubscriptionService subscriptions,
    ITenantEntitlements entitlements,
    IServiceScopeFactory scopes,
    IReleaseCatalog releases,
    TimeProvider time,
    IValidator<CreateTenantRequest> createValidator,
    IValidator<UpdateTenantRequest> updateValidator,
    ILogger<TenantAdministrationService> logger) : ITenantAdministration
{
    public async Task<PagedResult<TenantSummary>> SearchAsync(TenantQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var platformId = await platform.GetIdAsync(cancellationToken);

        using var system = tenant.BeginSystemScope();
        var tenants = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            tenants = tenants.Where(t => t.Name.Contains(term) || t.Code.Contains(term) || t.ContactEmail!.Contains(term)
                || t.Domains.Any(d => d.Host.Contains(term)));
        }

        if (query.Status is { } status)
        {
            tenants = tenants.Where(t => db.TenantSubscriptions.Any(s => s.TenantId == t.Id && s.Status == status));
        }

        var total = await tenants.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<TenantSummary>.Empty(page);
        }

        var rows = await Project(tenants.OrderBy(t => t.Name).Skip(page.Skip).Take(page.PageSize), platformId).ToListAsync(cancellationToken);
        return new PagedResult<TenantSummary>(rows, total, page.Page, page.PageSize);
    }

    public async Task<ValidationResponse<TenantDetails>> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var platformId = await platform.GetIdAsync(cancellationToken);
        TenantSummary? summary;
        List<TenantDomainView> domains;
        using (tenant.BeginSystemScope())
        {
            summary = await Project(db.Tenants.AsNoTracking().Where(t => t.Id == tenantId), platformId).FirstOrDefaultAsync(cancellationToken);
            domains = await db.TenantDomains.AsNoTracking().Where(d => d.TenantId == tenantId).OrderBy(d => d.Host)
                .Select(d => new TenantDomainView(d.Id, d.Host, d.IsVerified)).ToListAsync(cancellationToken);
        }

        if (summary is null)
        {
            return ValidationResponse.NotFound("Tenant not found.").As<TenantDetails>();
        }

        var people = await AsTenantAsync(tenantId, summary.Code, async services =>
        {
            var found = await services.GetRequiredService<IUserAdministration>().SearchAsync(new UserQuery(PageSize: 50), cancellationToken);
            return found.Items
                .OrderByDescending(u => u.Roles.Contains(RoleNames.Administrator, StringComparer.Ordinal))
                .ThenBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        });

        return new TenantDetails(
            summary,
            domains,
            await subscriptions.GetAsync(tenantId, cancellationToken),
            await subscriptions.UsageAsync(tenantId, cancellationToken),
            people);
    }

    public async Task<ValidationResponse<Guid>> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await createValidator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation.As<Guid>();
        }

        var code = request.Code.Trim().ToLowerInvariant();
        var host = string.IsNullOrWhiteSpace(request.Host) ? null : TenantDomain.Normalize(request.Host);
        if (await db.Tenants.AnyAsync(t => t.Code == code, cancellationToken))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(CreateTenantRequest.Code), "A tenant with this code already exists.", "tenant.duplicate")).As<Guid>();
        }

        if (host is not null && await db.TenantDomains.AnyAsync(d => d.Host == host, cancellationToken))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(CreateTenantRequest.Host), "Another tenant already uses this host.", "domain.duplicate")).As<Guid>();
        }

        Plan? plan = null;
        if (request.PlanId is { } planId)
        {
            plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, cancellationToken);
            if (plan is null)
            {
                return ValidationResponse.Failure(new ValidationError(nameof(CreateTenantRequest.PlanId), "Choose a plan that is on offer.", "plan.unavailable")).As<Guid>();
            }
        }

        var created = new Tenant(code, request.Name);
        created.ChangeContact(request.AdminEmail);
        created.AdoptRelease(ReleaseVersions.Display(releases.CurrentVersion), time.GetUtcNow()); // starts with everything released so far
        if (host is not null)
        {
            created.AddDomain(host).MarkVerified(); // the operator is adding it, so it is trusted
        }

        using (tenant.BeginSystemScope())
        {
            db.Tenants.Add(created);
            if (plan is not null)
            {
                db.TenantSubscriptions.Add(TenantSubscription.Start(created.Id, plan, request.BillingCycle, time.GetUtcNow(), request.WithTrial));
            }

            await unitOfWork.SaveAsync(cancellationToken);
        }

        await resolver.InvalidateAsync(cancellationToken);
        await entitlements.InvalidateAsync(cancellationToken);

        var provisioned = await ProvisionAsync(created, host, request, cancellationToken);
        return provisioned.IsSuccess
            ? ValidationResponse.Success(created.Id, $"{created.Name} created. An invitation is on its way to {request.AdminEmail.Trim()}.")
            : ValidationResponse.Success(created.Id, $"{created.Name} created, but its administrator wasn't invited: {provisioned.Message} Invite them again from the tenant.");
    }

    public async Task<ValidationResponse> UpdateAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await updateValidator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var target = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (target is null)
        {
            return ValidationResponse.NotFound("Tenant not found.");
        }

        target.Rename(request.Name);
        target.ChangeContact(request.ContactEmail);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Tenant saved.");
    }

    public async Task<ValidationResponse> AddDomainAsync(Guid tenantId, string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || !System.Text.RegularExpressions.Regex.IsMatch(host.Trim(), TenantRules.HostPattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return ValidationResponse.Failure(new ValidationError("host", "Enter a host name such as acme.example.com.", "domain.invalid"));
        }

        var normalized = TenantDomain.Normalize(host);
        var target = await db.Tenants.Include(t => t.Domains).FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (target is null)
        {
            return ValidationResponse.NotFound("Tenant not found.");
        }

        if (await db.TenantDomains.AnyAsync(d => d.Host == normalized, cancellationToken))
        {
            return ValidationResponse.Failure(new ValidationError("host", "This host is already in use.", "domain.duplicate"));
        }

        target.AddDomain(normalized);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"{normalized} added. Verify it once its DNS points at the platform.");
    }

    public Task<ValidationResponse> VerifyDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken = default) =>
        ChangeDomainAsync(tenantId, domainId, domain =>
        {
            domain.MarkVerified();
            return $"{domain.Host} verified; it now opens this tenant.";
        }, cancellationToken);

    public Task<ValidationResponse> RemoveDomainAsync(Guid tenantId, Guid domainId, CancellationToken cancellationToken = default) =>
        ChangeDomainAsync(tenantId, domainId, domain =>
        {
            unitOfWork.Repository<TenantDomain>().Remove(domain);
            return $"{domain.Host} removed.";
        }, cancellationToken);

    public async Task<ValidationResponse> SetActiveAsync(Guid tenantId, bool active, CancellationToken cancellationToken = default)
    {
        if (tenantId == await platform.GetIdAsync(cancellationToken))
        {
            return ValidationResponse.Failure("The platform tenant can't be deactivated.");
        }

        var target = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (target is null)
        {
            return ValidationResponse.NotFound("Tenant not found.");
        }

        if (active)
        {
            target.Activate();
        }
        else
        {
            target.Deactivate();
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await resolver.InvalidateAsync(cancellationToken);
        return ValidationResponse.Success(active ? $"{target.Name} is active again." : $"{target.Name} is deactivated; nobody can reach it.");
    }

    public async Task<ValidationResponse<Guid>> InviteAdministratorAsync(Guid tenantId, string email, string name, CancellationToken cancellationToken = default)
    {
        var target = await FindAsync(tenantId, cancellationToken);
        if (target is null)
        {
            return ValidationResponse.NotFound("Tenant not found.").As<Guid>();
        }

        var invite = await AsTenantAsync(target.Id, target.Code, services => services.GetRequiredService<IUserAdministration>()
            .InviteAsync(new InviteUserRequest(email?.Trim() ?? string.Empty, name?.Trim() ?? string.Empty, [RoleNames.Administrator], Welcome: true), cancellationToken));
        return invite.IsSuccess ? ValidationResponse.Success(invite.Data, $"Invitation on its way to {email!.Trim()}.") : invite;
    }

    public async Task<ValidationResponse> ResendWelcomeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var target = await FindAsync(tenantId, cancellationToken);
        if (target is null)
        {
            return ValidationResponse.NotFound("Tenant not found.");
        }

        return await AsTenantAsync(target.Id, target.Code, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId.ToString());
            if (user is null || user.TenantId != target.Id)
            {
                return ValidationResponse.NotFound("User not found.");
            }

            if (user.HasPassword)
            {
                return ValidationResponse.Conflict($"{user.DisplayName} has already accepted and set a password.");
            }

            // A fresh token is made when the email is sent, so earlier links stop mattering.
            user.Raise(new UserInvited(user.Id, Welcome: true));
            var updated = (await users.UpdateAsync(user)).ToValidationResponse();
            return updated.IsSuccess ? ValidationResponse.Success($"Welcome email sent to {user.Email} again.") : updated;
        });
    }

    private async Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        using (tenant.BeginSystemScope())
        {
            return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        }
    }

    /// <summary>Runs <paramref name="work"/> in a scope that belongs to <paramref name="tenantId"/>, as the current user.</summary>
    private async Task<T> AsTenantAsync<T>(Guid tenantId, string code, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, code);
        return await work(scope.ServiceProvider);
    }

    private IQueryable<TenantSummary> Project(IQueryable<Tenant> tenants, Guid platformId) =>
        tenants.Select(t => new
        {
            Tenant = t,
            Subscription = db.TenantSubscriptions.Where(s => s.TenantId == t.Id).Select(s => new { s.Status, s.CurrentPeriodEnd, s.PlanId }).FirstOrDefault(),
        })
        .Select(x => new TenantSummary(
            x.Tenant.Id,
            x.Tenant.Code,
            x.Tenant.Name,
            x.Tenant.IsActive,
            x.Tenant.Id == platformId,
            x.Tenant.ContactEmail,
            x.Tenant.Domains.Where(d => d.IsVerified).OrderBy(d => d.DateCreated).Select(d => d.Host).FirstOrDefault(),
            db.Plans.Where(p => x.Subscription != null && p.Id == x.Subscription.PlanId).Select(p => p.Name).FirstOrDefault(),
            x.Subscription != null ? x.Subscription.Status : null,
            x.Subscription != null ? x.Subscription.CurrentPeriodEnd : null,
            db.Users.Count(u => u.TenantId == x.Tenant.Id),
            x.Tenant.DateCreated,
            x.Tenant.ReleaseVersion));

    /// <summary>Runs as the new tenant: its public address, then its first administrator's invitation.</summary>
    private async Task<ValidationResponse> ProvisionAsync(Tenant created, string? host, CreateTenantRequest request, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantContext>().SetTenant(created.Id, created.Code);

        if (host is not null)
        {
            var address = await services.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.General.PublicBaseUrl, "https://" + host, cancellationToken);
            if (address.IsError)
            {
                return address;
            }
        }

        var invite = await services.GetRequiredService<IUserAdministration>()
            .InviteAsync(new InviteUserRequest(request.AdminEmail.Trim(), request.AdminName.Trim(), [RoleNames.Administrator], Welcome: true), cancellationToken);
        if (invite.IsError)
        {
            LogInviteFailed(logger, created.Code, invite.Message);
        }

        return invite.IsError ? ValidationResponse.Failure(invite.Message ?? "The invitation failed.") : ValidationResponse.Success();
    }

    private async Task<ValidationResponse> ChangeDomainAsync(Guid tenantId, Guid domainId, Func<TenantDomain, string> change, CancellationToken cancellationToken)
    {
        var domain = await db.TenantDomains.FirstOrDefaultAsync(d => d.Id == domainId && d.TenantId == tenantId, cancellationToken);
        if (domain is null)
        {
            return ValidationResponse.NotFound("Domain not found.");
        }

        var message = change(domain);
        await unitOfWork.SaveAsync(cancellationToken);
        await resolver.InvalidateAsync(cancellationToken);
        return ValidationResponse.Success(message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant {Code} was created but its administrator could not be invited: {Reason}")]
    private static partial void LogInviteFailed(ILogger logger, string code, string? reason);
}
