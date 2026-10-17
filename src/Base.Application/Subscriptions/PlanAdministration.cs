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
