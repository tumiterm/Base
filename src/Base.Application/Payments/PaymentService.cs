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

public sealed record PaymentGatewayView(
    string Code, string Name, IReadOnlyList<string> Methods, bool SupportsRecurring = false, decimal MinimumCharge = 0m, decimal? MaximumRecurringCharge = null);

/// <param name="ReturnBase">Absolute URL the person comes back to; the payment reference is appended.</param>
/// <param name="NotifyBase">Absolute URL gateways post notifications to; the gateway code is appended.</param>
public sealed record PaymentUrls(Uri ReturnBase, Uri NotifyBase);

/// <param name="RenewAutomatically">Have the gateway charge every cycle until stopped (if it can).</param>
public sealed record PayRequest(Guid PlanId, BillingCycle BillingCycle, string Gateway, bool RenewAutomatically = false);

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
    bool Applied,
    bool IsRenewal = false,
    bool SetsUpRenewal = false,
    bool CanShareLink = false,
    bool IsUpgrade = false);

/// <param name="StopError">Why the gateway couldn't be told to stop it, while it may still be charging.</param>
public sealed record RecurringPaymentView(
    Guid Id,
    string Gateway,
    string GatewayName,
    string PlanName,
    BillingCycle BillingCycle,
    decimal Amount,
    string Currency,
    RecurringPaymentStatus Status,
    string StartedByName,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastChargedAt,
    int Charges,
    bool CanUpdateCard,
    string? StopError,
    string? FirstReference = null);

/// <param name="CheckoutUrl">The gateway's page to forward to; null when the link can no longer be used.</param>
/// <param name="Status">The payment's status, to explain why not (null when there is no such payment).</param>
public sealed record PaymentForward(Uri? CheckoutUrl, PaymentStatus? Status, string? Description, decimal Amount, string? Currency);

public sealed record PaymentQuery(PaymentStatus? Status = null, int Page = 1, int PageSize = PageRequest.DefaultPageSize);

/// <summary>Totals for the platform's payments page.</summary>
public sealed record PaymentTotals(decimal ReceivedThisMonth, string? Currency, int SucceededThisMonth, int FailedThisMonth, int Pending, int AutomaticRenewals = 0);

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

    /// <summary>This organisation's automatic renewal that is set up or waiting for its first payment, if any.</summary>
    Task<ValidationResponse<RecurringPaymentView?>> RecurringMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns an automatic renewal off, at the gateway too. Needs <c>Subscription.Pay</c>.</summary>
    Task<ValidationResponse> StopRecurringAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The gateway's page for changing the card an automatic renewal charges. Needs <c>Subscription.Pay</c>.</summary>
    Task<ValidationResponse<Uri>> UpdateCardAsync(Guid id, Uri returnUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where a payment link (<c>payments/go/{reference}</c>) forwards to: the gateway's checkout page while the payment
    /// is still waiting and hasn't expired. Anonymous: the reference is the capability, like the gateway's own link.
    /// </summary>
    Task<PaymentForward> ForwardAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>A gateway's notification, from an anonymous endpoint: trusted only once the gateway verified it.</summary>
    Task<NotificationReply> ReceiveAsync(string gateway, InboundNotification notification, CancellationToken cancellationToken = default);
}

internal sealed partial class PaymentService(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IEnumerable<IPaymentGateway> gateways,
    RecurringPayments recurringPayments,
    ISubscriptionService subscriptions,
    IServiceScopeFactory scopes,
    IOptions<PaymentOptions> options,
    TimeProvider time,
    ILogger<PaymentService> logger) : IPaymentService
{
    public IReadOnlyList<PaymentGatewayView> Gateways(string? currency = null) =>
        gateways
            .Where(g => currency is null || g.Currencies.Contains(currency, StringComparer.OrdinalIgnoreCase))
            .Select(g => new PaymentGatewayView(g.Code, g.Name, g.Methods, g.SupportsRecurring, g.MinimumCharge, g.MaximumRecurringCharge))
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
        var description = $"{plan.Name}, {(request.BillingCycle == BillingCycle.Annual ? "1 year" : "1 month")}";
        var cycle = request.BillingCycle;
        var upgrade = false;

        // With a subscription, what is paid follows the plan-change rules: an upgrade pays the difference for the rest of
        // the period, and a downgrade or change of cycle isn't paid now at all.
        var preview = await subscriptions.PreviewMineAsync(plan.Id, request.BillingCycle, cancellationToken);
        if (preview.IsSuccess)
        {
            var change = preview.Data!;
            if (change.IsBlocked && change.Kind != PlanChangeKind.Extend)
            {
                return ValidationResponse.Failure(
                    $"{plan.Name} allows less than you use: {string.Join(" ", change.OverLimits.Select(l => l.Describe()))}", "plan.over-limits").As<CheckoutRedirect>();
            }

            switch (change.Kind)
            {
                case PlanChangeKind.AtPeriodEnd:
                    return ValidationResponse.Failure(
                        string.Create(CultureInfo.InvariantCulture, $"There's nothing to pay now: {plan.Name} starts when your current period ends, on {change.EffectiveAt:d MMMM yyyy}."),
                        "plan.at-period-end").As<CheckoutRedirect>();
                case PlanChangeKind.Upgrade:
                    amount = change.AmountDueNow;
                    cycle = change.FromCycle;
                    upgrade = true;
                    description = string.Create(CultureInfo.InvariantCulture, $"Upgrade to {plan.Name} until {change.PeriodEnd:d MMM yyyy}");
                    break;
            }
        }

        if (amount <= 0)
        {
            return ValidationResponse.Failure(new ValidationError(
                nameof(request.PlanId),
                upgrade ? "There's nothing to pay for this upgrade." : $"{plan.Name} is free {(request.BillingCycle == BillingCycle.Annual ? "yearly" : "monthly")}; there's nothing to pay.")).As<CheckoutRedirect>();
        }

        var gateway = gateways.FirstOrDefault(g => string.Equals(g.Code, request.Gateway, StringComparison.OrdinalIgnoreCase));
        if (gateway is null || !gateway.Currencies.Contains(plan.Currency, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.Gateway), "Choose how to pay.")).As<CheckoutRedirect>();
        }

        if (amount < gateway.MinimumCharge)
        {
            return ValidationResponse.Failure(new ValidationError(
                nameof(request.Gateway),
                $"{gateway.Name} can't take payments under {plan.Currency} {gateway.MinimumCharge.ToString("N2", CultureInfo.InvariantCulture)}; choose another way to pay.")).As<CheckoutRedirect>();
        }

        if (gateway.MaximumCharge is { } maximum && amount > maximum)
        {
            return ValidationResponse.Failure(new ValidationError(
                nameof(request.Gateway),
                $"{gateway.Name} takes payments of up to {plan.Currency} {maximum.ToString("N2", CultureInfo.InvariantCulture)}; choose another way to pay.")).As<CheckoutRedirect>();
        }

        // An upgrade's difference is paid once; an automatic renewal that is on follows the new plan by itself.
        var renew = request.RenewAutomatically && !upgrade;
        if (renew && !gateway.SupportsRecurring)
        {
            return ValidationResponse.Failure(new ValidationError(nameof(request.RenewAutomatically), $"{gateway.Name} can't renew automatically; pay without it.")).As<CheckoutRedirect>();
        }

        // Checked here, because past this point the gateway would only show its own error page.
        if (renew && gateway.MaximumRecurringCharge is { } recurringMaximum && amount > recurringMaximum)
        {
            return ValidationResponse.Failure(new ValidationError(
                nameof(request.RenewAutomatically),
                $"{gateway.Name} renews automatically only up to {plan.Currency} {recurringMaximum.ToString("N2", CultureInfo.InvariantCulture)} a payment. Turn off \"Renew automatically\" to pay this once, or choose another way to pay.")).As<CheckoutRedirect>();
        }

        var owner = await unitOfWork.Repository<Tenant>().GetByIdAsync(tenant.TenantId, cancellationToken);
        var payment = new Payment(
            tenant.TenantId, gateway.Code, amount, plan.Currency, description, plan.Id, cycle,
            currentUser.UserId, currentUser.AuditName, time.GetUtcNow(), options.Value.PendingFor);
        if (upgrade)
        {
            payment.PaysForUpgrade();
        }

        if (renew)
        {
            var recurring = new RecurringPayment(tenant.TenantId, gateway.Code, plan.Id, request.BillingCycle, amount, plan.Currency, currentUser.AuditName);
            await unitOfWork.Repository<RecurringPayment>().AddAsync(recurring, cancellationToken);
            payment.SetsUp(recurring);
        }

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
            new Uri(urls.NotifyBase.AbsoluteUri.TrimEnd('/') + "/" + gateway.Code.ToLowerInvariant()),
            renew ? request.BillingCycle : null);

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
            if (payment.RecurringPaymentId is { } pendingId && await unitOfWork.Repository<RecurringPayment>().GetByIdAsync(pendingId, cancellationToken) is { } pending)
            {
                pending.Cancel("The first payment couldn't be started.", time.GetUtcNow());
            }

            await unitOfWork.SaveAsync(cancellationToken);
            return ValidationResponse.Failure(started.Error ?? $"{gateway.Name} couldn't start the payment. Try again, or choose another way to pay.").As<CheckoutRedirect>();
        }

        payment.Started(started.GatewayReference, started.Redirect.IsFormPost ? null : started.Redirect.Url);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(started.Redirect with { PaymentReference = payment.Reference });
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

        // Still waiting: ask the gateway, which needs no notification to reach us (a developer's machine, a late webhook).
        // Its answer is applied like a verified notification; coming back here alone never marks a payment paid.
        if (!cancelled && payment.Status is PaymentStatus.Pending or PaymentStatus.Expired && Gateway(payment.Gateway) is { SupportsStatusCheck: true } provider)
        {
            var read = await CheckQuietlyAsync(provider, payment, cancellationToken);
            if (read is { Verified: true, Kind: GatewayNotificationKind.Paid or GatewayNotificationKind.Failed })
            {
                await ApplyAsync(provider, read, read.EventId ?? $"check:{payment.Reference}:{read.Kind}", cancellationToken);
                payment = await unitOfWork.Repository<Payment>().GetAsync(p => p.Reference == reference, asNoTracking: true, cancellationToken: cancellationToken) ?? payment;
            }
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
            var renewing = await unitOfWork.Repository<RecurringPayment>().CountAsync(r => r.Status == RecurringPaymentStatus.Active, cancellationToken);
            var paid = month.Where(p => p.Status == PaymentStatus.Succeeded).ToList();
            var currency = paid.Select(p => p.Currency).Distinct(StringComparer.Ordinal).ToList() is [var only] ? only : null;
            return ValidationResponse.Success(new PaymentTotals(
                currency is null ? 0 : paid.Sum(p => p.Amount),
                currency,
                paid.Count,
                month.Count(p => p.Status == PaymentStatus.Failed),
                pending,
                renewing));
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
        return await ApplyAsync(provider, read, read.EventId ?? Fingerprint(notification.Body), cancellationToken);
    }

    /// <summary>
    /// Records what the gateway said (a notification it sent, or its answer when asked) once per event, and applies a
    /// verified one to the payment it names, in the payment's own tenant.
    /// </summary>
    private async Task<NotificationReply> ApplyAsync(IPaymentGateway provider, GatewayNotification read, string eventId, CancellationToken cancellationToken)
    {
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
            if (payment is null && read.CheckoutId is { Length: > 0 } checkoutId)
            {
                payment = await unitOfWork.Repository<Payment>().GetAsync(p => p.GatewayReference == checkoutId && p.Gateway == provider.Code, cancellationToken: cancellationToken);
            }
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
        var recurring = target.RecurringPaymentId is { } recurringId
            ? await work.Repository<RecurringPayment>().GetByIdAsync(recurringId, cancellationToken)
            : null;
        var reference = target.Reference;
        bool changed;
        switch (read.Kind)
        {
            // A charge under an automatic renewal names the payment that set it up, with a new gateway reference.
            case GatewayNotificationKind.Paid when recurring is not null && target.Status == PaymentStatus.Succeeded
                && !string.Equals(read.GatewayReference, target.GatewayReference, StringComparison.Ordinal):
                var renewal = Payment.Renewal(recurring, target.Description, read.GatewayReference, now);
                await work.Repository<Payment>().AddAsync(renewal, cancellationToken);
                recurring.Charged(now);
                reference = renewal.Reference;
                changed = true;
                break;
            case GatewayNotificationKind.Paid:
                changed = target.Succeed(read.GatewayReference, now);
                if (recurring is not null && read.RecurringToken is { Length: > 0 } token)
                {
                    recurring.Activate(token, now);
                }

                break;
            case GatewayNotificationKind.Failed:
                changed = target.Fail(read.Detail ?? $"{provider.Name} declined the payment.", now);
                break;
            case GatewayNotificationKind.Cancelled when recurring is not null && target.Status == PaymentStatus.Succeeded:
                changed = recurring.IsLive;
                recurring.CancelledByGateway($"Stopped at {provider.Name}.", now);
                break;
            default:
                changed = target.Cancel(now);
                break;
        }

        await work.Repository<PaymentNotification>().AddAsync(
            new PaymentNotification(provider.Code, eventId, reference, changed ? PaymentNotificationOutcome.Applied : PaymentNotificationOutcome.Duplicate, read.Detail ?? read.Kind.ToString(), now),
            cancellationToken);
        await work.SaveAsync(cancellationToken);
        LogReceived(logger, provider.Code, reference, read.Kind, changed);
        return NotificationReply.Ok();
    }

    public async Task<ValidationResponse<RecurringPaymentView?>> RecurringMineAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Subscription.View))
        {
            return ValidationResponse.Forbidden().As<RecurringPaymentView?>();
        }

        var live = await unitOfWork.Repository<RecurringPayment>().GetAllAsync(
            filter: r => r.Status == RecurringPaymentStatus.Active || r.Status == RecurringPaymentStatus.Pending,
            orderBy: q => q.OrderByDescending(r => r.Status).ThenByDescending(r => r.DateCreated),
            cancellationToken: cancellationToken);
        if (live.Count == 0 || (live[0].Status == RecurringPaymentStatus.Pending && !await HasWaitingPaymentAsync(live[0].Id, cancellationToken)))
        {
            return ValidationResponse.Success<RecurringPaymentView?>(null);
        }

        var r = live[0];
        var plan = await unitOfWork.Repository<Plan>().GetByIdAsync(r.PlanId, cancellationToken);
        var first = await unitOfWork.Repository<Payment>().GetAsync(p => p.RecurringPaymentId == r.Id && !p.IsRenewal, p => p.Reference, cancellationToken);
        var gateway = Gateway(r.Gateway);
        return ValidationResponse.Success<RecurringPaymentView?>(new RecurringPaymentView(
            r.Id, r.Gateway, gateway?.Name ?? r.Gateway, plan?.Name ?? "Plan", r.BillingCycle, r.Amount, r.Currency, r.Status, r.StartedByName,
            r.DateCreated, r.LastChargedAt, r.Charges,
            r.Status == RecurringPaymentStatus.Active && gateway?.SupportsCardUpdate == true,
            r.StopError,
            first));
    }

    public async Task<ValidationResponse> StopRecurringAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Subscription.Pay))
        {
            return ValidationResponse.Forbidden();
        }

        var recurring = await unitOfWork.Repository<RecurringPayment>().GetByIdAsync(id, cancellationToken);
        if (recurring is null || !recurring.IsLive)
        {
            return ValidationResponse.NotFound("That automatic renewal isn't on.");
        }

        var stopped = await recurringPayments.StopAsync(recurring, $"Turned off by {currentUser.AuditName}.", cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return stopped.Succeeded
            ? ValidationResponse.Success("Automatic renewal is off. You're paid up to the end of the current period; pay again any time.")
            : ValidationResponse.Failure(stopped.Error!);
    }

    public async Task<ValidationResponse<Uri>> UpdateCardAsync(Guid id, Uri returnUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);
        if (!currentUser.HasPermission(PermissionCodes.Subscription.Pay))
        {
            return ValidationResponse.Forbidden().As<Uri>();
        }

        var recurring = await unitOfWork.Repository<RecurringPayment>().GetByIdAsync(id, cancellationToken);
        return recurring is { Status: RecurringPaymentStatus.Active, Token: { } token } && Gateway(recurring.Gateway)?.UpdateCardUrl(token, returnUrl) is { } url
            ? ValidationResponse.Success(url)
            : ValidationResponse.NotFound("The card for this renewal can't be changed online.").As<Uri>();
    }

    public async Task<PaymentForward> ForwardAsync(string reference, CancellationToken cancellationToken = default)
    {
        Payment? payment;
        using (tenant.BeginSystemScope())
        {
            payment = string.IsNullOrWhiteSpace(reference) || reference.Length != Payment.ReferenceLength
                ? null
                : await unitOfWork.Repository<Payment>().GetAsync(p => p.Reference == reference, asNoTracking: true, cancellationToken: cancellationToken);
        }

        if (payment is null)
        {
            return new PaymentForward(null, null, null, 0, null);
        }

        var usable = payment.CanForward && payment.ExpiresAt > time.GetUtcNow();
        return new PaymentForward(usable ? new Uri(payment.CheckoutUrl!) : null, payment.Status, payment.Description, payment.Amount, payment.Currency);
    }

    private async Task<GatewayNotification?> CheckQuietlyAsync(IPaymentGateway provider, Payment payment, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.CheckAsync(payment.Reference, payment.GatewayReference, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            LogCheckFailed(logger, provider.Code, payment.Reference, ex);
            return null;
        }
    }

    private Task<bool> HasWaitingPaymentAsync(Guid recurringId, CancellationToken cancellationToken) =>
        unitOfWork.Repository<Payment>().ExistsAsync(p => p.RecurringPaymentId == recurringId && p.Status == PaymentStatus.Pending, cancellationToken);

    private IPaymentGateway? Gateway(string code) =>
        gateways.FirstOrDefault(g => string.Equals(g.Code, code, StringComparison.OrdinalIgnoreCase));

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
        p.AppliedAt is not null,
        p.IsRenewal,
        p.RecurringPaymentId is not null && !p.IsRenewal,
        p.CanForward && p.ExpiresAt > time.GetUtcNow(),
        p.IsUpgrade);

    private static string Fingerprint(string body) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment gateway {Gateway} couldn't be reached to start payment {Reference}")]
    private static partial void LogGatewayUnreachable(ILogger logger, string gateway, string reference, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't ask {Gateway} how payment {Reference} went")]
    private static partial void LogCheckFailed(ILogger logger, string gateway, string reference, Exception exception);

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
        else if (payment.IsRenewal)
        {
            // The gateway charged what the renewal was set to: it pays for the next period, and never switches plan.
            subscription.RecordPayment(now, payment.Reference);
        }
        else if (payment.IsUpgrade)
        {
            subscription.Upgrade(plan, PlanChangeSource.Upgrade, payment.Reference);
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

            // An automatic renewal whose first payment never went through was never set up at the gateway.
            var waiting = await unitOfWork.Repository<RecurringPayment>().GetAllAsync(filter: r => r.Status == RecurringPaymentStatus.Pending, cancellationToken: cancellationToken);
            var ids = waiting.Select(r => r.Id).ToList();
            var stillPaying = (await unitOfWork.Repository<Payment>().GetAllAsync(
                    p => p.RecurringPaymentId,
                    p => p.RecurringPaymentId != null && ids.Contains(p.RecurringPaymentId.Value) && p.Status == PaymentStatus.Pending && p.ExpiresAt > now,
                    cancellationToken: cancellationToken))
                .ToHashSet();
            var abandoned = waiting.Where(r => !stillPaying.Contains(r.Id)).ToList();
            foreach (var recurring in abandoned)
            {
                recurring.Cancel("Never set up at the payment provider: the first payment didn't go through.", now);
            }

            await unitOfWork.SaveAsync(cancellationToken);
            return stale.Count == 0 && abandoned.Count == 0 ? "Nothing waiting too long" : $"Expired {stale.Count} payment(s) and {abandoned.Count} unfinished automatic renewal(s)";
        }
    }
}
