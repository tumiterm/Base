using Base.Application.Common.Abstractions;
using Base.Application.Payments;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Base.Web.Controllers;

/// <summary>
/// This organisation's own plan, status, usage and online payments. Open to every signed-in user (and to a suspended
/// tenant, so it can see why and pay).
/// </summary>
[Route("subscription")]
public sealed class SubscriptionController(
    ISubscriptionService subscriptions,
    ITenantEntitlements entitlements,
    IPaymentService payments,
    IApplicationSettingService settings,
    ICurrentUser currentUser) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var mine = await subscriptions.GetMineAsync(cancellationToken);
        var currency = mine.Subscription?.Currency ?? (mine.Plans.Count > 0 ? mine.Plans[0].Currency : null);
        var canView = currentUser.HasPermission(PermissionCodes.Subscription.View);
        var history = canView ? (await payments.ListMineAsync(new PaymentQuery(PageSize: 10), cancellationToken)).Data?.Items ?? [] : [];
        var recurring = canView ? (await payments.RecurringMineAsync(cancellationToken)).Data : null;
        return View(new SubscriptionPageViewModel(
            mine,
            await entitlements.GetAsync(cancellationToken),
            payments.Gateways(currency),
            history,
            currentUser.HasPermission(PermissionCodes.Subscription.Pay),
            recurring));
    }

    /// <summary>Starts paying: to the gateway's page, by redirect or a form the browser posts by itself.</summary>
    [HttpPost("pay")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> Pay(PayForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var started = await payments.StartAsync(new PayRequest(form.PlanId, form.BillingCycle, form.Gateway, form.RenewAutomatically), await UrlsAsync(cancellationToken), cancellationToken);
        if (started.IsError)
        {
            if (started.ErrorType is Application.Common.Results.ErrorType.Forbidden)
            {
                return started.ToActionResult(this);
            }

            this.Flash(Application.Common.Results.ValidationResponse.Failure(started.Message ?? string.Join(" ", started.Errors.Select(e => e.Message))));
            return RedirectToAction(nameof(Index), null, null, "pay");
        }

        var redirect = started.Data!;
        if (redirect.IsFormPost)
        {
            return View("GatewayRedirect", redirect);
        }

        // Another site is reached through our forwarding page on the public address, never by redirecting this post:
        // the CSP's form-action (which covers redirects after a post) needs no entry, and gateways that check the
        // referring domain (Yoco) see the address verified with them.
        // The public address may be another origin than this one, so this page continues by script rather than by a
        // redirect of the post (which form-action would refuse).
        var sameSite = string.Equals(redirect.Url.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase);
        return sameSite || redirect.PaymentReference is null
            ? Redirect(redirect.Url.AbsoluteUri)
            : View("GatewayRedirect", new CheckoutRedirect(new Uri(await PublicBaseAsync(cancellationToken) + "/payments/go/" + redirect.PaymentReference)));
    }

    /// <summary>
    /// The pay-or-change form lands here first. Paying for a fresh period or another period of the same plan goes
    /// straight to payment; an upgrade, a downgrade, a change of cycle or a plan the organisation is too big for is shown
    /// for review first: what changes, when, and what is due.
    /// </summary>
    [HttpPost("change/review")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> Review(PayForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var preview = await subscriptions.PreviewMineAsync(form.PlanId, form.BillingCycle, cancellationToken);
        if (preview.ErrorType is Application.Common.Results.ErrorType.NotFound)
        {
            return await Pay(form, cancellationToken);
        }

        if (preview.IsError)
        {
            this.Flash(preview);
            return RedirectToAction(nameof(Index), null, null, "pay");
        }

        var change = preview.Data!;
        if (!change.IsBlocked && change.Kind is PlanChangeKind.Start or PlanChangeKind.Extend)
        {
            return await Pay(form, cancellationToken);
        }

        var all = payments.Gateways(change.Currency);
        var nothingToPay = change.Kind == PlanChangeKind.Upgrade && (all.Count == 0 || change.AmountDueNow < all.Min(g => g.MinimumCharge));
        return View("Change", new PlanChangeViewModel(
            change,
            all.Where(g => g.MinimumCharge <= change.AmountDueNow).ToList(),
            form.Gateway,
            nothingToPay,
            currentUser.HasPermission(PermissionCodes.Users.Manage)));
    }

    /// <summary>Confirms a change that needs no payment: a downgrade or change of cycle, or an upgrade with nothing to pay.</summary>
    [HttpPost("change")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> Change(Guid planId, BillingCycle billingCycle, CancellationToken cancellationToken)
    {
        this.Flash(await subscriptions.ChangeMineAsync(planId, billingCycle, cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Keeps the current plan: withdraws the change waiting for the end of the period.</summary>
    [HttpPost("change/keep")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> KeepPlan(CancellationToken cancellationToken)
    {
        this.Flash(await subscriptions.KeepMyPlanAsync(cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Turns automatic renewal off, at the gateway too.</summary>
    [HttpPost("renewal/{id:guid}/stop")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> StopRenewal(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await payments.StopRecurringAsync(id, cancellationToken));
        return RedirectToAction(nameof(Index));
    }

    /// <summary>To the gateway's page for changing the card the renewal charges; it sends the person back here.</summary>
    [HttpPost("renewal/{id:guid}/card")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> UpdateCard(Guid id, CancellationToken cancellationToken)
    {
        var url = await payments.UpdateCardAsync(id, new Uri($"{Request.Scheme}://{Request.Host}{Request.PathBase}/subscription"), cancellationToken);
        if (url.IsError)
        {
            this.Flash(Application.Common.Results.ValidationResponse.Failure(url.Message ?? "The card can't be changed online."));
            return RedirectToAction(nameof(Index));
        }

        return Redirect(url.Data!.AbsoluteUri);
    }

    /// <summary>Where the gateway sends the person back to; the gateway's own notification decides the outcome.</summary>
    [HttpGet("payments/{reference}")]
    public async Task<IActionResult> Payment(string reference, bool cancelled = false, CancellationToken cancellationToken = default)
    {
        var result = await payments.ReturnedAsync(reference, cancelled, cancellationToken);
        if (result.IsError)
        {
            return result.ToActionResult(this);
        }

        var share = result.Data!.CanShareLink ? await PublicBaseAsync(cancellationToken) + "/payments/go/" + result.Data.Reference : null;
        return View(new PaymentPageViewModel(result.Data, share));
    }

    private async Task<PaymentUrls> UrlsAsync(CancellationToken cancellationToken)
    {
        // The person comes back to the address they are on (a verified tenant host). Gateways post to the public
        // address (General.PublicBaseUrl as it applies to this organisation), never one taken from the request.
        var here = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        return new PaymentUrls(new Uri(here + "/subscription/payments"), new Uri(await PublicBaseAsync(cancellationToken) + "/payments/notify"));
    }

    private async Task<string> PublicBaseAsync(CancellationToken cancellationToken)
    {
        var configuredBase = await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken);
        return Uri.TryCreate(configuredBase, UriKind.Absolute, out var configured)
            ? configured.AbsoluteUri.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    }
}

/// <summary>
/// Where payment gateways post their notifications. Anonymous by necessity: each notification is trusted only after
/// its gateway verifies it (signature, source, confirmation), and it can only change the payment it names.
/// </summary>
[Route("payments/notify")]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class PaymentNotifyController(IPaymentService payments) : Controller
{
    private const int MaxBodyBytes = 64 * 1024;

    [HttpPost("{gateway}")]
    [RequestSizeLimit(MaxBodyBytes)]
    [RawBody(MaxBodyBytes)]
    public async Task<IActionResult> Receive([FromRoute] string gateway, CancellationToken cancellationToken)
    {
        Request.Body.Position = 0;
        string body;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        Request.Body.Position = 0;
        var form = Request.HasFormContentType
            ? (await Request.ReadFormAsync(cancellationToken)).ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var reply = await payments.ReceiveAsync(gateway, new InboundNotification(body, form, headers, HttpContext.Connection.RemoteIpAddress?.ToString()), cancellationToken);
        return new ContentResult { StatusCode = reply.StatusCode, Content = reply.Body, ContentType = "text/plain" };
    }
}

/// <summary>
/// Keeps the request body for the action as it arrived: buffered, and not read by form model binding first. Gateways
/// sign the raw body, so it has to be read exactly as sent.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RawBodyAttribute(int maxBytes) : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.HttpContext.Request.EnableBuffering(maxBytes);
        var factories = context.ValueProviderFactories;
        for (var i = factories.Count - 1; i >= 0; i--)
        {
            if (factories[i] is FormValueProviderFactory or FormFileValueProviderFactory or JQueryFormValueProviderFactory)
            {
                factories.RemoveAt(i);
            }
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

/// <summary>
/// A payment link: a page on our own domain that forwards the browser to the gateway's checkout page while the payment
/// is waiting. Gateways that check the referring domain (Yoco) need the person to arrive from a page like this, which
/// also makes the link safe to send by email or WhatsApp. Anonymous: the reference is the capability.
/// </summary>
[Route("payments/go")]
[AllowAnonymous]
public sealed class PaymentForwardController(IPaymentService payments) : Controller
{
    [HttpGet("{reference}")]
    public async Task<IActionResult> Index(string reference, CancellationToken cancellationToken)
    {
        var forward = await payments.ForwardAsync(reference, cancellationToken);
        if (forward.Status is null)
        {
            return NotFound();
        }

        return forward.CheckoutUrl is { } url ? View("Forward", new CheckoutRedirect(url)) : View("ForwardUnavailable", forward);
    }
}

/// <summary>The built-in Test gateway's checkout page (only when <c>Payments:Gateways:Test:Enabled</c>).</summary>
[Route("payments/test")]
public sealed class TestCheckoutController(IPaymentService payments, IServiceProvider services) : Controller
{
    [HttpGet("")]
    [AllowAnonymous]
    public IActionResult Index(string token) =>
        services.GetService<ITestCheckout>()?.Read(token ?? string.Empty) is { } checkout
            ? View(new TestCheckoutViewModel(token!, checkout, Guid.NewGuid().ToString("N")))
            : NotFound();

    [HttpPost("")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Answer(string token, string outcome, string @event, CancellationToken cancellationToken)
    {
        if (services.GetService<ITestCheckout>()?.Read(token ?? string.Empty) is not { } checkout)
        {
            return NotFound();
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = token!, ["outcome"] = outcome ?? string.Empty, ["event"] = @event ?? string.Empty };
        await payments.ReceiveAsync("Test", new InboundNotification(string.Empty, form, new Dictionary<string, string>(StringComparer.Ordinal), null), cancellationToken);
        return Redirect((string.Equals(outcome, "cancel", StringComparison.Ordinal) ? checkout.CancelUrl : checkout.ReturnUrl).AbsoluteUri);
    }

    /// <summary>Has the Test gateway charge the next renewal now, as a real gateway would at the end of the period.</summary>
    [HttpPost("renew")]
    [ValidateAntiForgeryToken]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> Renew(CancellationToken cancellationToken)
    {
        var recurring = (await payments.RecurringMineAsync(cancellationToken)).Data;
        if (services.GetService<ITestCheckout>() is not { } test || recurring is not { Gateway: "Test", Status: Domain.Payments.RecurringPaymentStatus.Active, FirstReference: { } first })
        {
            return NotFound();
        }

        var form = test.RenewalNotice(first, recurring.Amount, recurring.Currency);
        await payments.ReceiveAsync("Test", new InboundNotification(string.Empty, form, new Dictionary<string, string>(StringComparer.Ordinal), null), cancellationToken);
        return Redirect("/subscription");
    }
}

/// <summary>Every organisation's online payments, for the platform.</summary>
[Route("platform/payments")]
[HasPermission(PermissionCodes.Platform.TenantsView)]
public sealed class PlatformPaymentsController(IPaymentService payments) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(PaymentStatus? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var list = await payments.ListAllAsync(new PaymentQuery(status, page), cancellationToken);
        var totals = await payments.TotalsAsync(cancellationToken);
        return list.IsError ? list.ToActionResult(this) : View(new PlatformPaymentsViewModel(list.Data!, totals.Data!, status, payments.Gateways()));
    }
}
