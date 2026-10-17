using Base.Domain.Common;

namespace Base.Domain.Subscriptions;

/// <summary>
/// Something a tenant can subscribe to: prices, a trial length and its entitlements (features switched on and
/// limits). Plans are platform data shared by every tenant. A retired plan keeps its subscribers but can't be chosen.
/// </summary>
public sealed class Plan : EntityBase, INamedEntity
{
    public const int CurrencyLength = 3;
    public const int DescriptionMaxLength = 500;
    public const int MaxTrialDays = 365;

    private readonly List<PlanEntitlement> _entitlements = [];

    private Plan()
    {
    }

    public Plan(string code, string name, string currency, decimal monthlyPrice, decimal annualPrice, int trialDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Id = SequentialGuid.Next();
        Code = code.Trim().ToLowerInvariant();
        IsActive = true;
        Update(name, null, currency, monthlyPrice, annualPrice, trialDays, sortOrder: 0);
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>ISO 4217 code, e.g. ZAR.</summary>
    public string Currency { get; private set; } = string.Empty;

    public decimal MonthlyPrice { get; private set; }

    public decimal AnnualPrice { get; private set; }

    /// <summary>Days of free trial for a new subscription; 0 starts it active.</summary>
    public int TrialDays { get; private set; }

    /// <summary>Offered for new subscriptions. Retired plans keep their subscribers.</summary>
    public bool IsActive { get; private set; }

    public int SortOrder { get; private set; }

    public IReadOnlyCollection<PlanEntitlement> Entitlements => _entitlements.AsReadOnly();

    public void Update(string name, string? description, string currency, decimal monthlyPrice, decimal annualPrice, int trialDays, int sortOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegative(monthlyPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(annualPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(trialDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trialDays, MaxTrialDays);
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Currency = currency.Trim().ToUpperInvariant();
        MonthlyPrice = monthlyPrice;
        AnnualPrice = annualPrice;
        TrialDays = trialDays;
        SortOrder = sortOrder;
    }

    public decimal PriceFor(BillingCycle cycle) => cycle == BillingCycle.Annual ? AnnualPrice : MonthlyPrice;

    /// <summary>
    /// Sets one entitlement. A feature is on when its value is 1; a limit's value is the maximum, null meaning
    /// unlimited. Rows are updated in place, never removed, so the audit trail shows every change.
    /// </summary>
    public void SetEntitlement(string code, long? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Entitlement values can't be negative.");
        }

        var existing = _entitlements.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.Ordinal));
        if (existing is null)
        {
            _entitlements.Add(new PlanEntitlement(Id, code, value));
        }
        else
        {
            existing.Change(value);
        }
    }

    public void Retire() => IsActive = false;

    public void Offer() => IsActive = true;
}

/// <summary>One feature or limit of a plan. Codes come from the application's entitlement catalogue.</summary>
public sealed class PlanEntitlement : EntityBase
{
    public const int CodeMaxLength = 100;

    private PlanEntitlement()
    {
    }

    internal PlanEntitlement(Guid planId, string code, long? value)
    {
        Id = SequentialGuid.Next();
        PlanId = planId;
        Code = code;
        Value = value;
    }

    public Guid PlanId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>Feature: 1 on, 0 off. Limit: the maximum, or null for unlimited.</summary>
    public long? Value { get; private set; }

    internal void Change(long? value) => Value = value;
}
