using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Messaging;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Application.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>How long a payment waits for the gateway's answer before it counts as expired (a late success still applies).</summary>
    public TimeSpan PendingFor { get; set; } = TimeSpan.FromHours(2);
}

public sealed record PaymentGatewayView(string Code, string Name, IReadOnlyList<string> Methods);

/// <param name="ReturnBase">Absolute URL the person comes back to; the payment reference is appended.</param>
/// <param name="NotifyBase">Absolute URL gateways post notifications to; the gateway code is appended.</param>
public sealed record PaymentUrls(Uri ReturnBase, Uri NotifyBase);

public sealed record PayRequest(Guid PlanId, BillingCycle BillingCycle, string Gateway);

public sealed record PaymentView(
    Guid Id,
    string Reference,
    Guid TenantId,
    string? TenantName,
    string Gateway,
    string GatewayName,
    decimal Amount,
    string Currency,
    string Description,
    BillingCycle BillingCycle,
    PaymentStatus Status,
    string? FailureReason,
    string RequestedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool Applied);

public sealed record PaymentQuery(PaymentStatus? Status = null, int Page = 1, int PageSize = PageRequest.DefaultPageSize);

/// <summary>Totals for the platform's payments page.</summary>
public sealed record PaymentTotals(decimal ReceivedThisMonth, string? Currency, int SucceededThisMonth, int FailedThisMonth, int Pending);

/// <summary>What to answer a gateway's notification with.</summary>
public sealed record NotificationReply(int StatusCode, string Body)
{
    public static NotificationReply Ok() => new(200, "OK");
}

/// <summary>
/// Paying for the subscription through a gateway: start a checkout, receive the gateway's notifications, and see
/// payments. A succeeded payment moves the subscription on (<see cref="PaymentSucceeded"/>).
/// </summary>
public interface IPaymentService
{
    /// <summary>The gateways configured on this server that accept <paramref name="currency"/> (all when null).</summary>
    IReadOnlyList<PaymentGatewayView> Gateways(string? currency = null);

    /// <summary>Starts paying for a plan and cycle. Needs <c>Subscription.Pay</c>.</summary>
    Task<ValidationResponse<CheckoutRedirect>> StartAsync(PayRequest request, PaymentUrls urls, CancellationToken cancellationToken = default);

    /// <summary>One of this organisation's payments, by reference; <paramref name="cancelled"/> when the person came back without paying.</summary>
    Task<ValidationResponse<PaymentView>> ReturnedAsync(string reference, bool cancelled, CancellationToken cancellationToken = default);

    Task<ValidationResponse<PagedResult<PaymentView>>> ListMineAsync(PaymentQuery query, CancellationToken cancellationToken = default);

    /// <summary>Every organisation's payments. Needs <c>Platform.Tenants.View</c>.</summary>
    Task<ValidationResponse<PagedResult<PaymentView>>> ListAllAsync(PaymentQuery query, CancellationToken cancellationToken = default);

    Task<ValidationResponse<PaymentTotals>> TotalsAsync(CancellationToken cancellationToken = default);

    /// <summary>A gateway's notification, from an anonymous endpoint: trusted only once the gateway verified it.</summary>
    Task<NotificationReply> ReceiveAsync(string gateway, InboundNotification notification, CancellationToken cancellationToken = default);
}

internal sealed partial class PaymentService(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IEnumerable<IPaymentGateway> gateways,
    IServiceScopeFactory scopes,
    IOptions<PaymentOptions> options,
    TimeProvider time,
    ILogger<PaymentService> logger) : IPaymentService
{
    public IReadOnlyList<PaymentGatewayView> Gateways(string? currency = null) =>
        gateways
            .Where(g => currency is null || g.Currencies.Contains(currency, StringComparer.OrdinalIgnoreCase))
            .Select(g => new PaymentGatewayView(g.Code, g.Name, g.Methods))
            .ToList();

    public async Task<ValidationResponse<CheckoutRedirect>> StartAsync(PayRequest request, PaymentUrls urls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(urls);
        if (!currentUser.HasPermission(PermissionCodes.Subscription.Pay) || !tenant.IsResolved)
        {
            return ValidationResponse.Forbidden().As<CheckoutRedirect>();
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(request.PlanId, cancellationToken);
        if (plan is null || !plan.IsActive)
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.PlanId), "Choose one of the plans on offer.")).As<CheckoutRedirect>();
        }

        var amount = request.BillingCycle == BillingCycle.Annual ? plan.AnnualPrice : plan.MonthlyPrice;
        if (amount <= 0)
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.PlanId), $"{plan.Name} is free {(request.BillingCycle == BillingCycle.Annual ? "yearly" : "monthly")}; there's nothing to pay.")).As<CheckoutRedirect>();
        }

        var gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, request.Gateway, StringComparison.OrdinalIgnoreCase));
        if (gateway is null || !gateway.Currencies.Contains(plan.Currency, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.Gateway), "Choose how to pay.")).As<CheckoutRedirect>();
        }

        var owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(tenant.TenantId, cancellationToken);
        var description = $"{plan.Name}, {(request.BillingCycle == BillingCycle.Annual ? "1 year" : "1 month")}";
        var payment = new Payment(
            tenant.TenantId, gateway.Code, amount, plan.Currency, description, plan.Id, request.BillingCycle,
            currentUser.UserId, currentUser.AuditName, time.GetUtcNow(), options.Value.PendingFor);
        await unitOfWork.Repository<Payment>().AddAsync(payment, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);

        var returnUrl = new Uri(urls.ReturnBase.AbsoluteUri.TrimEnd('/') + "/" + payment.Reference);
        var checkout = new CheckoutRequest(
            payment.Reference,
            payment.Amount,
            payment.Currency,
            $"{description} ({owner?.Name})",
            owner?.ContactEmail,
            owner?.Name,
            returnUrl,
            new Uri(returnUrl.AbsoluteUri + "?cancelled=true"),
            new Uri(urls.NotifyBase.AbsoluteUri.TrimEnd('/') + "/" + gateway.Code.ToLowerInvariant()));

        CheckoutStart started;
        try
        {
            started = await gateway.StartCheckoutAsync(checkout, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogGatewayUnreachable(logger, gateway.Code, payment.Reference, ex);
            started = CheckoutStart.Failed($"{gateway.Name} couldn't be reached. Try again in a minute.");
        }

        if (started.Redirect is null)
        {
            payment.Fail(started.Error ?? "The payment couldn't be started.", time.GetUtcNow());
            await unitOfWork.SaveAsync(cancellationToken);
            return ValidationResponse.Failure(started.Error ?? $"{gateway.Name} couldn't start the payment. Try again, or choose another way to pay.").As<CheckoutRedirect>();
        }

        payment.Started(started.GatewayReference);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(started.Redirect);
    }

    public async Task<ValidationResponse<PaymentView>> ReturnedAsync(string reference, bool cancelled, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Subscription.View))
        {
            return ValidationResponse.Forbidden().As<PaymentView>();
        }

        var payment = await unitOfWork.Repository<Payment>().GetAsync(p => p.Reference == reference, cancellationToken: cancellationToken);
        if (payment is null)
        {
            return ValidationResponse.NotFound("That payment doesn't exist.").As<PaymentView>();
        }

        // Only marks a payment still waiting: the gateway's own word (a success, even later) always wins.
        if (cancelled && payment.Cancel(time.GetUtcNow()))
        {
            await unitOfWork.SaveAsync(cancellationToken);
        }

        return ValidationResponse.Success(View(payment, null));
    }

    public async Task<ValidationResponse<PagedResult<PaymentView>>> ListMineAsync(PaymentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!currentUser.HasPermission(PermissionCodes.Subscription.View))
        {
            return ValidationResponse.Forbidden().As<PagedResult<PaymentView>>();
        }

        var page = await PageAsync(query, cancellationToken);
        return ValidationResponse.Success(new PagedResult<PaymentView>(page.Items.Select(p => View(p, null)).ToList(), page.Total, page.Page, page.PageSize));
    }

    public async Task<ValidationResponse<PagedResult<PaymentView>>> ListAllAsync(PaymentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!currentUser.HasPermission(PermissionCodes.Platform.TenantsView))
        {
            return ValidationResponse.Forbidden().As<PagedResult<PaymentView>>();
        }

        using (tenant.BeginSystemScope())
        {
            var page = await PageAsync(query, cancellationToken);
            var ids = page.Items.Select(p => p.TenantId).Distinct().ToList();
            var names = (await unitOfWork.Repository<Tenant>().GetAllAsync(t => new { t.Id, t.Name }, t => ids.Contains(t.Id), cancellationToken: cancellationToken))
                .ToDictionary(t => t.Id, t => t.Name);
            return ValidationResponse.Success(new PagedResult<PaymentView>(
                page.Items.Select(p => View(p, names.GetValueOrDefault(p.TenantId))).ToList(), page.Total, page.Page, page.PageSize));
        }
    }

    public async Task<ValidationResponse<PaymentTotals>> TotalsAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Platform.TenantsView))
        {
            return ValidationResponse.Forbidden().As<PaymentTotals>();
        }

        var now = time.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        using (tenant.BeginSystemScope())
        {
            var month = await unitOfWork.Repository<Payment>().GetAllAsync(
                p => new { p.Status, p.Amount, p.Currency },
                p => p.CompletedAt >= monthStart && (p.Status == PaymentStatus.Succeeded || p.Status == PaymentStatus.Failed),
                cancellationToken: cancellationToken);
            var pending = await unitOfWork.Repository<Payment>().CountAsync(p => p.Status == PaymentStatus.Pending, cancellationToken);
            var paid = month.Where(p => p.Status == PaymentStatus.Succeeded).ToList();
            var currency = paid.Select(p => p.Currency).Distinct(StringComparer.Ordinal).ToList() is [var only] ? only : null;
            return ValidationResponse.Success(new PaymentTotals(
                currency is null ? 0 : paid.Sum(p => p.Amount),
                currency,
                paid.Count,
                month.Count(p => p.Status == PaymentStatus.Failed),
                pending));
        }
    }

    public async Task<NotificationReply> ReceiveAsync(string gateway, InboundNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var provider = gateways.FirstOrDefault(g => string.Equals(g.Code, gateway, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new NotificationReply(404, "Unknown gateway");
        }

        var read = await provider.ReadNotificationAsync(notification, cancellationToken);
        var eventId = read.EventId ?? Fingerprint(notification.Body);
        var now = time.GetUtcNow();
        Payment? payment;
        using (tenant.BeginSystemScope())
        {
            var log = unitOfWork.Repository<PaymentNotification>();
            if (await log.ExistsAsync(n => n.Gateway == provider.Code && n.EventId == eventId, cancellationToken))
            {
                return NotificationReply.Ok();
            }

            if (!read.Verified)
            {
                LogRejected(logger, provider.Code, read.Detail);
                await log.AddAsync(new PaymentNotification(provider.Code, eventId, read.Reference, PaymentNotificationOutcome.Rejected, read.Detail, now), cancellationToken);
                await unitOfWork.SaveAsync(cancellationToken);
                return new NotificationReply(400, "Not verified");
            }

            payment = read.Reference is null
                ? null
                : await unitOfWork.Repository<Payment>().GetAsync(p => p.Reference == read.Reference && p.Gateway == provider.Code, cancellationToken: cancellationToken);
            if (payment is null || read.Kind == GatewayNotificationKind.Other)
            {
                await log.AddAsync(new PaymentNotification(provider.Code, eventId, read.Reference, PaymentNotificationOutcome.Ignored, read.Detail ?? "Not about a payment of ours.", now), cancellationToken);
                await unitOfWork.SaveAsync(cancellationToken);
                return NotificationReply.Ok();
            }

            if (read.Amount is { } amount && decimal.Round(amount, 2) != payment.Amount)
            {
                LogAmountMismatch(logger, payment.Reference, payment.Amount, amount);
                await log.AddAsync(new PaymentNotification(provider.Code, eventId, payment.Reference, PaymentNotificationOutcome.Rejected, $"Amount {amount.ToString("0.00", CultureInfo.InvariantCulture)} doesn't match {payment.Amount.ToString("0.00", CultureInfo.InvariantCulture)}.", now), cancellationToken);
                await unitOfWork.SaveAsync(cancellationToken);
                return new NotificationReply(400, "Amount mismatch");
            }
        }

        // Saved in the payment's own tenant, so the success event is handled there.
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(payment.TenantId, null);
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var target = await work.Repository<Payment>().GetByIdAsync(payment.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {payment.Reference} disappeared.");
        var changed = read.Kind switch
        {
            GatewayNotificationKind.Paid => target.Succeed(read.GatewayReference, now),
            GatewayNotificationKind.Failed => target.Fail(read.Detail ?? $"{provider.Name} declined the payment.", now),
            _ => target.Cancel(now),
        };
        await work.Repository<PaymentNotification>().AddAsync(
            new PaymentNotification(provider.Code, eventId, target.Reference, changed ? PaymentNotificationOutcome.Applied : PaymentNotificationOutcome.Duplicate, read.Detail ?? read.Kind.ToString(), now),
            cancellationToken);
        await work.SaveAsync(cancellationToken);
        LogReceived(logger, provider.Code, target.Reference, read.Kind, changed);
        return NotificationReply.Ok();
    }

    private Task<PagedResult<Payment>> PageAsync(PaymentQuery query, CancellationToken cancellationToken) =>
        unitOfWork.Repository<Payment>().GetPagedAsync(
            new PageRequest(query.Page, query.PageSize),
            p => p,
            q => q.OrderByDescending(p => p.DateCreated),
            query.Status is { } status ? p => p.Status == status : null,
            cancellationToken);

    private PaymentView View(Payment p, string? tenantName) => new(
        p.Id,
        p.Reference,
        p.TenantId,
        tenantName,
        p.Gateway,
        gateways.FirstOrDefault(g => string.Equals(g.Code, p.Gateway, StringComparison.OrdinalIgnoreCase))?.Name ?? p.Gateway,
        p.Amount,
        p.Currency,
        p.Description,
        p.BillingCycle,
        p.Status,
        p.FailureReason,
        p.RequestedByName,
        p.DateCreated,
        p.CompletedAt,
        p.AppliedAt is not null);

    private static string Fingerprint(string body) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment gateway {Gateway} couldn't be reached to start payment {Reference}")]
    private static partial void LogGatewayUnreachable(ILogger logger, string gateway, string reference, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a {Gateway} notification: {Detail}")]
    private static partial void LogRejected(ILogger logger, string gateway, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment {Reference} is for {Expected} but the gateway reported {Reported}; ignored")]
    private static partial void LogAmountMismatch(ILogger logger, string reference, decimal expected, decimal reported);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Gateway} notification for payment {Reference}: {Kind} (changed: {Changed})")]
    private static partial void LogReceived(ILogger logger, string gateway, string reference, GatewayNotificationKind kind, bool changed);
}

/// <summary>
/// Applies a succeeded payment to the subscription once (in the payment's tenant, after the commit) and sends the
/// receipt.
/// </summary>
internal sealed class PaymentSucceededHandler(
    IUnitOfWork unitOfWork,
    ITenantEntitlements entitlements,
    INotificationService notifications,
    IEnumerable<IPaymentGateway> gateways,
    TimeProvider time) : IDomainEventHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var payment = await unitOfWork.Repository<Payment>().GetByIdAsync(domainEvent.PaymentId, cancellationToken);
        if (payment is null || payment.AppliedAt is not null)
        {
            return;
        }

        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(payment.PlanId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {payment.Reference} is for a plan that no longer exists.");
        var now = time.GetUtcNow();
        var subscriptions = unitOfWork.Repository<TenantSubscription>();
        var subscription = await subscriptions.GetAsync(s => s.TenantId == payment.TenantId, cancellationToken: cancellationToken);
        if (subscription is null)
        {
            subscription = TenantSubscription.Start(payment.TenantId, plan, payment.BillingCycle, now, withTrial: false);
            await subscriptions.AddAsync(subscription, cancellationToken);
        }
        else
        {
            subscription.ApplyPayment(plan, payment.BillingCycle, now, payment.Reference);
        }

        payment.MarkApplied(now);
        await unitOfWork.SaveAsync(cancellationToken);
        await entitlements.InvalidateAsync(cancellationToken);

        var data = new
        {
            PlanName = plan.Name,
            Amount = payment.Currency + " " + payment.Amount.ToString("N2", CultureInfo.InvariantCulture),
            payment.Reference,
            PaidUntil = subscription.CurrentPeriodEnd.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            Gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, payment.Gateway, StringComparison.OrdinalIgnoreCase))?.Name ?? payment.Gateway,
        };
        if (payment.RequestedByUserId is { } payer)
        {
            await notifications.SendAsync(new Notification(NotificationTemplates.Subscriptions.PaymentReceived, NotificationRecipient.ToUser(payer), data), cancellationToken);
        }

        var owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(payment.TenantId, cancellationToken);
        if (owner?.ContactEmail is { } contact)
        {
            await notifications.SendAsync(new Notification(NotificationTemplates.Subscriptions.PaymentReceived, NotificationRecipient.ToEmail(contact, owner.Name), data), cancellationToken);
        }
    }
}

/// <summary>Marks payments nobody heard back about as expired, so they stop showing as waiting.</summary>
internal sealed class PaymentExpiryJob(IUnitOfWork unitOfWork, ITenantContext tenant, TimeProvider time) : IScheduledJob
{
    public string Code => "payment-expiry";

    public string Name => "Payment expiry";

    public string Description => "Marks online payments the gateway never answered about as expired. A late success still applies.";

    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        using (tenant.BeginSystemScope())
        {
            var stale = await unitOfWork.Repository<Payment>().GetAllAsync(filter: p => p.Status == PaymentStatus.Pending && p.ExpiresAt <= now, cancellationToken: cancellationToken);
            foreach (var payment in stale)
            {
                payment.Expire(now);
            }

            await unitOfWork.SaveAsync(cancellationToken);
            return stale.Count == 0 ? "Nothing waiting too long" : $"Expired {stale.Count} payment(s)";
        }
    }
}
