using Base.Domain.Common;
using Base.Domain.Subscriptions;

namespace Base.Domain.Payments;

public enum BillingEventKind
{
    Subscribed = 0,
    StatusChanged = 1,
    PlanChanged = 2,
    ChangeScheduled = 3,
    ChangeWithdrawn = 4,
    TrialExtended = 5,
    PaidUpTo = 6,
    CancelScheduled = 7,
    Resumed = 8,
    CheckoutStarted = 9,
    PaymentReceived = 10,
    PaymentFailed = 11,
    PaymentCancelled = 12,
    PaymentExpired = 13,
    RenewalCharged = 14,
    RenewalOn = 15,
    RenewalChanged = 16,
    RenewalStopped = 17,
    RenewalStopFailed = 18,
    InvoiceIssued = 19,
    InvoiceSent = 20,
}

/// <summary>
/// One line of an organisation's billing history: what happened to its subscription, payments and automatic renewal,
/// when, and by whom (a person, the payment provider or the system). Written in the same save as the change it
/// describes, append-only, and kept for good: financial records outlive the audit trail's retention.
/// </summary>
public sealed class BillingEvent : ITenantOwned
{
    public const int TextMaxLength = 500;
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 50;

    private BillingEvent()
    {
    }

    public BillingEvent(Guid tenantId, BillingEventKind kind, DateTimeOffset occurredAt, Guid? actorUserId, string actorName)
    {
        Id = SequentialGuid.Next();
        TenantId = tenantId;
        Kind = kind;
        OccurredAt = occurredAt;
        ActorUserId = actorUserId;
        ActorName = Limit(actorName, NameMaxLength) ?? string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; set; }

    public BillingEventKind Kind { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? ActorUserId { get; private set; }

    /// <summary>The person, the payment provider ("Payfast") or "system".</summary>
    public string ActorName { get; private set; } = string.Empty;

    public Guid? PlanId { get; private set; }

    public Guid? FromPlanId { get; private set; }

    public BillingCycle? BillingCycle { get; private set; }

    public decimal? Amount { get; private set; }

    public string? Currency { get; private set; }

    public string? PaymentReference { get; private set; }

    public string? Gateway { get; private set; }

    /// <summary>A status (from → to), a date, a description: the facts the summary is written from.</summary>
    public string? Detail { get; private set; }

    /// <summary>Why, as given by whoever made the change or reported by the provider.</summary>
    public string? Reason { get; private set; }

    public string? TraceId { get; private set; }

    public BillingEvent ForPlan(Guid? planId, BillingCycle? cycle = null, Guid? fromPlanId = null)
    {
        PlanId = planId;
        BillingCycle = cycle;
        FromPlanId = fromPlanId;
        return this;
    }

    public BillingEvent ForPayment(decimal amount, string currency, string? reference, string? gateway)
    {
        Amount = amount;
        Currency = currency;
        PaymentReference = reference;
        Gateway = Limit(gateway, CodeMaxLength);
        return this;
    }

    public BillingEvent With(string? detail = null, string? reason = null, string? traceId = null)
    {
        Detail = Limit(detail, TextMaxLength);
        Reason = Limit(reason, TextMaxLength);
        TraceId = Limit(traceId, 64);
        return this;
    }

    private static string? Limit(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
}
