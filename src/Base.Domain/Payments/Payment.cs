using System.Security.Cryptography;
using Base.Domain.Common;
using Base.Domain.Subscriptions;

namespace Base.Domain.Payments;

public enum PaymentStatus
{
    /// <summary>Sent to the gateway; waiting for it to say how it went.</summary>
    Pending = 0,

    Succeeded = 1,

    /// <summary>The gateway declined it or it went wrong; nothing was taken.</summary>
    Failed = 2,

    /// <summary>The person left the gateway's page without paying.</summary>
    Cancelled = 3,

    /// <summary>Nothing was heard back in time; a late success still counts.</summary>
    Expired = 4,
}

/// <summary>Raised once when a payment succeeds; its handler applies it to the subscription after the commit.</summary>
public sealed record PaymentSucceeded(Guid TenantId, Guid PaymentId) : IDomainEvent;

/// <summary>
/// One attempt by an organisation to pay for its subscription (a plan for a billing cycle) through a payment gateway.
/// <see cref="Reference"/> is what the gateway sees; the gateway's own id is <see cref="GatewayReference"/>.
/// </summary>
public sealed class Payment : EntityBase, ITenantOwned
{
    public const int ReferenceLength = 12;
    public const int GatewayMaxLength = 50;
    public const int GatewayReferenceMaxLength = 200;
    public const int CurrencyLength = 3;
    public const int DescriptionMaxLength = 200;
    public const int ReasonMaxLength = 500;
    public const int NameMaxLength = 200;

    private const string ReferenceAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private Payment()
    {
    }

    public Payment(Guid tenantId, string gateway, decimal amount, string currency, string description, Guid planId, BillingCycle cycle, Guid? requestedBy, string requestedByName, DateTimeOffset now, TimeSpan expiresIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        Id = SequentialGuid.Next();
        TenantId = tenantId;
        Reference = NewReference();
        Gateway = gateway;
        Amount = decimal.Round(amount, 2);
        Currency = currency.ToUpperInvariant();
        Description = Limit(description, DescriptionMaxLength);
        PlanId = planId;
        BillingCycle = cycle;
        RequestedByUserId = requestedBy;
        RequestedByName = Limit(requestedByName, NameMaxLength);
        Status = PaymentStatus.Pending;
        ExpiresAt = now + expiresIn;
    }

    public Guid TenantId { get; set; }

    /// <summary>Ours, unique, short enough to read out ("P7K2M9QX4T3B").</summary>
    public string Reference { get; private set; } = string.Empty;

    /// <summary>The gateway's code (<c>Payfast</c>, <c>Yoco</c>, <c>Test</c>...).</summary>
    public string Gateway { get; private set; } = string.Empty;

    public string? GatewayReference { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>The plan and cycle it pays for; a different one from the current subscription changes plan.</summary>
    public Guid PlanId { get; private set; }

    public BillingCycle BillingCycle { get; private set; }

    public PaymentStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When the subscription was moved on for it, so it is applied once.</summary>
    public DateTimeOffset? AppliedAt { get; private set; }

    public Guid? RequestedByUserId { get; private set; }

    public string RequestedByName { get; private set; } = string.Empty;

    public bool IsFinal => Status is PaymentStatus.Succeeded or PaymentStatus.Failed or PaymentStatus.Cancelled;

    public void Started(string? gatewayReference)
    {
        if (!string.IsNullOrWhiteSpace(gatewayReference))
        {
            GatewayReference = Limit(gatewayReference.Trim(), GatewayReferenceMaxLength);
        }
    }

    /// <summary>Paid. Only the first success counts; a late one after expiry still does (the money was taken).</summary>
    public bool Succeed(string? gatewayReference, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Succeeded)
        {
            return false;
        }

        Started(gatewayReference);
        Status = PaymentStatus.Succeeded;
        FailureReason = null;
        CompletedAt = now;
        Raise(new PaymentSucceeded(TenantId, Id));
        return true;
    }

    public bool Fail(string reason, DateTimeOffset now) => End(PaymentStatus.Failed, reason, now);

    public bool Cancel(DateTimeOffset now) => End(PaymentStatus.Cancelled, "Left the payment page without paying.", now);

    public bool Expire(DateTimeOffset now) =>
        Status == PaymentStatus.Pending && ExpiresAt <= now && End(PaymentStatus.Expired, "No answer from the payment gateway in time.", now);

    public void MarkApplied(DateTimeOffset now) => AppliedAt ??= now;

    private bool End(PaymentStatus status, string reason, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Pending)
        {
            return false;
        }

        Status = status;
        FailureReason = Limit(reason, ReasonMaxLength);
        CompletedAt = now;
        return true;
    }

    private static string NewReference() => "P" + RandomNumberGenerator.GetString(ReferenceAlphabet, ReferenceLength - 1);

    private static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
}

public enum PaymentNotificationOutcome
{
    /// <summary>Verified and recorded against its payment.</summary>
    Applied = 0,

    /// <summary>Seen before; nothing changed.</summary>
    Duplicate = 1,

    /// <summary>Failed verification (signature, amount, source): ignored.</summary>
    Rejected = 2,

    /// <summary>Verified, but for no payment of ours, or about something we don't act on.</summary>
    Ignored = 3,
}

/// <summary>
/// A notification a gateway sent us (webhook, ITN), kept so each is acted on once and so support can see what arrived.
/// Platform data: it is received before anyone knows which organisation it is for. Holds no card or personal data.
/// </summary>
public sealed class PaymentNotification
{
    public const int EventIdMaxLength = 200;
    public const int DetailMaxLength = 1000;

    private PaymentNotification()
    {
    }

    public PaymentNotification(string gateway, string eventId, string? paymentReference, PaymentNotificationOutcome outcome, string? detail, DateTimeOffset receivedAt)
    {
        Id = SequentialGuid.Next();
        Gateway = gateway;
        EventId = eventId.Length > EventIdMaxLength ? eventId[..EventIdMaxLength] : eventId;
        PaymentReference = paymentReference;
        Outcome = outcome;
        Detail = detail is { Length: > DetailMaxLength } ? detail[..DetailMaxLength] : detail;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }

    public string Gateway { get; private set; } = string.Empty;

    /// <summary>The gateway's id for the event (or a hash of it), unique per gateway.</summary>
    public string EventId { get; private set; } = string.Empty;

    public string? PaymentReference { get; private set; }

    public PaymentNotificationOutcome Outcome { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
}
