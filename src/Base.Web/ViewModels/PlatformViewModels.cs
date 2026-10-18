using Base.Application.Common.Persistence;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Subscriptions;

namespace Base.Web.ViewModels;

public sealed record TenantListViewModel(PagedResult<TenantSummary> Tenants, string? Search, SubscriptionStatus? Status);

public sealed class CreateTenantViewModel
{
    public string Name { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string? Host { get; set; }

    public string AdminName { get; set; } = string.Empty;

    public string AdminEmail { get; set; } = string.Empty;

    /// <summary>Empty: no subscription (unrestricted).</summary>
    public Guid? PlanId { get; set; }

    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    public bool WithTrial { get; set; } = true;

    public IReadOnlyList<PlanSummary> Plans { get; set; } = [];
}

public sealed record TenantDetailsViewModel(TenantDetails Details, IReadOnlyList<PlanSummary> Plans, Base.Application.Notifications.ChannelRoute EmailRoute);

public sealed class PlanEditViewModel
{
    public Guid? Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Currency { get; set; } = "ZAR";

    public decimal MonthlyPrice { get; set; }

    public decimal AnnualPrice { get; set; }

    public int TrialDays { get; set; } = 14;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public int SubscriberCount { get; set; }

    /// <summary>Feature codes that are switched on.</summary>
    public IList<string> Features { get; set; } = [];

    /// <summary>Limit values by code; empty means unlimited when the code is in <see cref="Unlimited"/>.</summary>
    public IDictionary<string, long?> Limits { get; set; } = new Dictionary<string, long?>(StringComparer.Ordinal);

    public IList<string> Unlimited { get; set; } = [];

    /// <summary>Features and limits added since the plan was last saved; shown as "New" with their suggestion.</summary>
    public IReadOnlyList<string> Undecided { get; set; } = [];

    public static PlanEditViewModel From(PlanSummary plan) => new()
    {
        Id = plan.Id,
        Code = plan.Code,
        Name = plan.Name,
        Description = plan.Description,
        Currency = plan.Currency,
        MonthlyPrice = plan.MonthlyPrice,
        AnnualPrice = plan.AnnualPrice,
        TrialDays = plan.TrialDays,
        SortOrder = plan.SortOrder,
        IsActive = plan.IsActive,
        SubscriberCount = plan.SubscriberCount,
        Features = EntitlementCatalog.Features.Where(f => plan.ValueOf(f.Code) == 1).Select(f => f.Code).ToList(),
        Limits = EntitlementCatalog.Limits.ToDictionary(l => l.Code, l => plan.ValueOf(l.Code), StringComparer.Ordinal),
        Unlimited = EntitlementCatalog.Limits.Where(l => plan.ValueOf(l.Code) is null).Select(l => l.Code).ToList(),
        Undecided = plan.Undecided().Select(e => e.Code).ToList(),
    };

    public static PlanEditViewModel New() => new()
    {
        Features = EntitlementCatalog.Features.Where(f => f.DefaultValue == 1).Select(f => f.Code).ToList(),
        Limits = EntitlementCatalog.Limits.ToDictionary(l => l.Code, l => l.DefaultValue, StringComparer.Ordinal),
    };

    public SavePlanRequest ToRequest()
    {
        var values = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var feature in EntitlementCatalog.Features)
        {
            values[feature.Code] = Features.Contains(feature.Code, StringComparer.Ordinal) ? 1 : 0;
        }

        foreach (var limit in EntitlementCatalog.Limits)
        {
            values[limit.Code] = Unlimited.Contains(limit.Code, StringComparer.Ordinal) ? null : (Limits.TryGetValue(limit.Code, out var value) ? value : null) ?? 0;
        }

        return new SavePlanRequest(Code, Name, Description, Currency, MonthlyPrice, AnnualPrice, TrialDays, SortOrder, values);
    }
}

public sealed record SubscriptionPageViewModel(MySubscription Mine, TenantAccess Access);

/// <summary>Plans side by side. Offered plans come first; retired ones keep their subscribers and stay editable.</summary>
public sealed record PlanMatrixViewModel(IReadOnlyList<PlanSummary> Plans)
{
    public IReadOnlyList<PlanSummary> Ordered { get; } = [.. Plans.OrderByDescending(p => p.IsActive)];

    /// <summary>Features and limits at least one offered plan hasn't decided on, each with the plans still to decide.</summary>
    public IReadOnlyList<(EntitlementDefinition Item, IReadOnlyList<PlanSummary> Plans)> Undecided { get; } =
        [.. EntitlementCatalog.All
            .Select(e => (Item: e, Plans: (IReadOnlyList<PlanSummary>)Plans.Where(p => p.IsActive && !p.HasDecided(e.Code)).ToList()))
            .Where(x => x.Plans.Count > 0)];
}

