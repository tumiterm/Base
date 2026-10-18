using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Validation;
using Base.Domain.Subscriptions;
using FluentValidation;

namespace Base.Application.Subscriptions;

/// <summary>The plans tenants subscribe to. Platform data: callers need <c>Platform.Plans.Manage</c>.</summary>
public interface IPlanAdministration
{
    Task<IReadOnlyList<PlanSummary>> ListAsync(bool includeRetired = true, CancellationToken cancellationToken = default);

    Task<ValidationResponse<PlanSummary>> GetAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<ValidationResponse<Guid>> CreateAsync(SavePlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes prices, trial and entitlements. The code can't change; existing subscribers get the new entitlements.</summary>
    Task<ValidationResponse> UpdateAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops offering the plan to new subscriptions; current subscribers keep it.</summary>
    Task<ValidationResponse> SetOfferedAsync(Guid planId, bool offered, CancellationToken cancellationToken = default);

    /// <summary>Saves the plans-and-features grid: each change sets one feature or limit of one plan, all in one save.</summary>
    Task<ValidationResponse> SetEntitlementsAsync(IReadOnlyList<PlanEntitlementChange> changes, CancellationToken cancellationToken = default);

    /// <summary>Every plan takes the suggestion (Standard in, Premium out, a limit's default) for what it hasn't decided yet.</summary>
    Task<ValidationResponse> AcceptSuggestionsAsync(CancellationToken cancellationToken = default);
}

internal sealed class PlanAdministration(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    IValidator<SavePlanRequest> validator) : IPlanAdministration
{
    public async Task<IReadOnlyList<PlanSummary>> ListAsync(bool includeRetired = true, CancellationToken cancellationToken = default)
    {
        var plans = await unitOfWork.Repository<Plan>().GetAllAsync(
            includeRetired ? null : p => p.IsActive,
            [p => p.Entitlements],
            q => q.OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPrice).ThenBy(p => p.Name),
            asNoTracking: true,
            cancellationToken);

        // Subscriptions belong to tenants; counting them across tenants is platform work.
        IReadOnlyList<Guid> planIds;
        using (tenant.BeginSystemScope())
        {
            planIds = await unitOfWork.Repository<TenantSubscription>().GetAllAsync(s => s.PlanId, cancellationToken: cancellationToken);
        }

        var counts = planIds.CountBy(id => id).ToDictionary(c => c.Key, c => c.Value);
        return plans.Select(p => ToSummary(p, counts.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<ValidationResponse<PlanSummary>> GetAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var plans = await ListAsync(includeRetired: true, cancellationToken);
        return plans.FirstOrDefault(p => p.Id == planId) is { } plan
            ? plan
            : ValidationResponse.NotFound("Plan not found.").As<PlanSummary>();
    }

    public async Task<ValidationResponse<Guid>> CreateAsync(SavePlanRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation.As<Guid>();
        }

        var repository = unitOfWork.Repository<Plan>();
        var code = request.Code.Trim().ToLowerInvariant();
        if (await repository.ExistsAsync(p => p.Code == code, cancellationToken))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(SavePlanRequest.Code), "A plan with this code already exists.", "plan.duplicate")).As<Guid>();
        }

        var plan = new Plan(code, request.Name, request.Currency, request.MonthlyPrice, request.AnnualPrice, request.TrialDays);
        Apply(plan, request);
        await repository.AddAsync(plan, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return plan.Id;
    }

    public async Task<ValidationResponse> UpdateAsync(Guid planId, SavePlanRequest request, CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateToResponseAsync(request, cancellationToken);
        if (validation.IsError)
        {
            return validation;
        }

        var plan = await unitOfWork.Repository<Plan>().GetAsync(p => p.Id == planId, [p => p.Entitlements], cancellationToken: cancellationToken);
        if (plan is null)
        {
            return ValidationResponse.NotFound("Plan not found.");
        }

        Apply(plan, request);
        await unitOfWork.SaveAsync(cancellationToken);
        await entitlements.InvalidateAsync(cancellationToken);
        return ValidationResponse.Success($"{plan.Name} saved.");
    }

    public async Task<ValidationResponse> SetOfferedAsync(Guid planId, bool offered, CancellationToken cancellationToken = default)
    {
        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(planId, cancellationToken);
        if (plan is null)
        {
            return ValidationResponse.NotFound("Plan not found.");
        }

        if (offered)
        {
            plan.Offer();
        }
        else
        {
            plan.Retire();
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(offered ? $"{plan.Name} is offered again." : $"{plan.Name} is no longer offered to new subscriptions.");
    }

    public async Task<ValidationResponse> SetEntitlementsAsync(IReadOnlyList<PlanEntitlementChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            var definition = EntitlementCatalog.Find(change.Code);
            if (definition is null)
            {
                return ValidationResponse.Failure($"{change.Code} isn't a feature or limit.");
            }

            if (definition.Kind == EntitlementKind.Feature ? change.Value is not (0 or 1) : change.Value < 0)
            {
                return ValidationResponse.Failure(definition.Kind == EntitlementKind.Feature
                    ? $"{definition.Name} is either included or not."
                    : $"{definition.Name} can't be negative.");
            }
        }

        var planIds = changes.Select(c => c.PlanId).Distinct().ToList();
        var plans = await unitOfWork.Repository<Plan>().GetAllAsync(p => planIds.Contains(p.Id), [p => p.Entitlements], cancellationToken: cancellationToken);
        if (plans.Count != planIds.Count)
        {
            return ValidationResponse.NotFound("Plan not found.");
        }

        var changed = new HashSet<Guid>();
        foreach (var change in changes)
        {
            var plan = plans.Single(p => p.Id == change.PlanId);
            var current = plan.Entitlements.FirstOrDefault(e => string.Equals(e.Code, change.Code, StringComparison.Ordinal));
            if (current is null || current.Value != change.Value)
            {
                plan.SetEntitlement(change.Code, change.Value);
                changed.Add(plan.Id);
            }
        }

        if (changed.Count == 0)
        {
            return ValidationResponse.Success("Nothing changed.");
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await entitlements.InvalidateAsync(cancellationToken);
        return ValidationResponse.Success(changed.Count == 1
            ? $"{plans.Single(p => changed.Contains(p.Id)).Name} saved. Its subscribers have the change now."
            : $"{changed.Count} plans saved. Their subscribers have the changes now.");
    }

    public async Task<ValidationResponse> AcceptSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        var plans = await unitOfWork.Repository<Plan>().GetAllAsync(null, [p => p.Entitlements], cancellationToken: cancellationToken);
        var decided = 0;
        foreach (var plan in plans)
        {
            foreach (var definition in EntitlementCatalog.All.Where(d => !plan.Entitlements.Any(e => string.Equals(e.Code, d.Code, StringComparison.Ordinal))))
            {
                plan.SetEntitlement(definition.Code, definition.DefaultValue);
                decided++;
            }
        }

        if (decided == 0)
        {
            return ValidationResponse.Success("Every plan has already decided on every feature.");
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await entitlements.InvalidateAsync(cancellationToken);
        return ValidationResponse.Success("Suggestions accepted: Standard features are in every plan, Premium ones are left out until you add them.");
    }

    internal static PlanSummary ToSummary(Plan plan, int subscribers) => new(
        plan.Id,
        plan.Code,
        plan.Name,
        plan.Description,
        plan.Currency,
        plan.MonthlyPrice,
        plan.AnnualPrice,
        plan.TrialDays,
        plan.IsActive,
        plan.SortOrder,
        subscribers,
        plan.Entitlements.ToDictionary(e => e.Code, e => e.Value, StringComparer.Ordinal));

    private static void Apply(Plan plan, SavePlanRequest request)
    {
        plan.Update(request.Name, request.Description, request.Currency, request.MonthlyPrice, request.AnnualPrice, request.TrialDays, request.SortOrder);
        foreach (var definition in EntitlementCatalog.All)
        {
            plan.SetEntitlement(definition.Code, request.Entitlements.TryGetValue(definition.Code, out var value) ? value : definition.DefaultValue);
        }
    }
}
