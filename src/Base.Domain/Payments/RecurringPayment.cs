using Base.Domain.Common;
using Base.Domain.Subscriptions;

namespace Base.Domain.Payments;

public enum RecurringPaymentStatus
{
    /// <summary>Set up at checkout; waiting for the first payment, which gives the gateway's token.</summary>
    Pending = 0,

    /// <summary>The gateway charges each billing cycle by itself.</summary>
    Active = 1,

    /// <summary>Stopped (by the organisation, a plan change, the subscription ending, or the gateway). Nothing more is charged.</summary>
    Cancelled = 2,
}

/// <summary>Raised when the gateway stopped an automatic renewal by itself (the buyer cancelled at the gateway...).</summary>
public sealed record RecurringPaymentStoppedByGateway(Guid TenantId, Guid RecurringPaymentId, string Reason) : IDomainEvent;

/// <summary>Raised when an automatic renewal starts, so any it replaces can be stopped at their gateway.</summary>
public sealed record RecurringPaymentActivated(Guid TenantId, Guid RecurringPaymentId) : IDomainEvent;

/// <summary>
/// An automatic renewal: the organisation agreed at checkout that the gateway charges its card each billing cycle for
/// a plan. The gateway's <see cref="Token"/> identifies the agreement there; each charge arrives as a notification and
/// becomes a renewal <see cref="Payment"/>.
/// </summary>
public sealed class RecurringPayment : EntityBase, ITenantOwned
{
    public const int TokenMaxLength = 100;
    public const int ReasonMaxLength = 500;

    private RecurringPayment()
    {
    }

    public RecurringPayment(Guid tenantId, string gateway, Guid planId, BillingCycle cycle, decimal amount, string currency, string startedByName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        Id = SequentialGuid.Next();
        TenantId = tenantId;
        Gateway = gateway;
        PlanId = planId;
        BillingCycle = cycle;
        Amount = decimal.Round(amount, 2);
        Currency = currency.ToUpperInvariant();
        StartedByName = startedByName.Length > Payment.NameMaxLength ? startedByName[..Payment.NameMaxLength] : startedByName;
        Status = RecurringPaymentStatus.Pending;
    }

    public Guid TenantId { get; set; }

    public string Gateway { get; private set; } = string.Empty;

    [Base.Domain.Auditing.AuditRedacted]
    public string? Token { get; private set; }

    public Guid PlanId { get; private set; }

    public BillingCycle BillingCycle { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public RecurringPaymentStatus Status { get; private set; }

    public string StartedByName { get; private set; } = string.Empty;

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? LastChargedAt { get; private set; }

    public int Charges { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>Why the gateway couldn't be told to stop, while it may still be charging.</summary>
    public string? StopError { get; private set; }

    public bool IsLive => Status is RecurringPaymentStatus.Pending or RecurringPaymentStatus.Active;

    /// <summary>The first payment went through: the gateway now charges by itself.</summary>
    public void Activate(string token, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (Status != RecurringPaymentStatus.Pending)
        {
            return;
        }

        Token = token.Length > TokenMaxLength ? token[..TokenMaxLength] : token;
        Status = RecurringPaymentStatus.Active;
        ActivatedAt = now;
        Charged(now);
        Raise(new RecurringPaymentActivated(TenantId, Id));
    }

    /// <summary>The gateway agreed to charge another amount or cycle from the next charge, for another plan.</summary>
    public void Follow(Guid planId, BillingCycle cycle, decimal amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        PlanId = planId;
        BillingCycle = cycle;
        Amount = decimal.Round(amount, 2);
    }

    public bool Matches(Guid planId, BillingCycle cycle, decimal amount) =>
        PlanId == planId && BillingCycle == cycle && Amount == decimal.Round(amount, 2);

    public void Charged(DateTimeOffset now)
    {
        LastChargedAt = now;
        Charges++;
    }

    /// <summary>Stopped here (and at the gateway, by the caller).</summary>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status == RecurringPaymentStatus.Cancelled)
        {
            return;
        }

        Status = RecurringPaymentStatus.Cancelled;
        CancelledAt = now;
        CancelReason = Limit(reason);
        StopError = null;
    }

    /// <summary>The gateway stopped it (the buyer cancelled there...).</summary>
    public void CancelledByGateway(string reason, DateTimeOffset now)
    {
        if (Status == RecurringPaymentStatus.Cancelled)
        {
            return;
        }

        Cancel(reason, now);
        Raise(new RecurringPaymentStoppedByGateway(TenantId, Id, CancelReason!));
    }

    public void StopFailed(string error) => StopError = Limit(error);

    private static string Limit(string value) => value.Length > ReasonMaxLength ? value[..ReasonMaxLength] : value;
}
