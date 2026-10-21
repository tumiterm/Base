using Base.Application.Common.Abstractions;
using Base.Application.Payments;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Payments;
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
        var history = currentUser.HasPermission(PermissionCodes.Subscription.View)
            ? (await payments.ListMineAsync(new PaymentQuery(PageSize: 10), cancellationToken)).Data?.Items ?? []
            : [];
        return View(new SubscriptionPageViewModel(
            mine,
            await entitlements.GetAsync(cancellationToken),
            payments.Gateways(currency),
            history,
            currentUser.HasPermission(PermissionCodes.Subscription.Pay)));
    }

    /// <summary>Starts paying: to the gateway's page, by redirect or a form the browser posts by itself.</summary>
    [HttpPost("pay")]
    [HasPermission(PermissionCodes.Subscription.Pay)]
    public async Task<IActionResult> Pay(PayForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var started = await payments.StartAsync(new PayRequest(form.PlanId, form.BillingCycle, form.Gateway), await UrlsAsync(cancellationToken), cancellationToken);
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
        return redirect.IsFormPost ? View("GatewayRedirect", redirect) : Redirect(redirect.Url.AbsoluteUri);
    }

    /// <summary>Where the gateway sends the person back to; the gateway's own notification decides the outcome.</summary>
    [HttpGet("payments/{reference}")]
    public async Task<IActionResult> Payment(string reference, bool cancelled = false, CancellationToken cancellationToken = default)
    {
        var result = await payments.ReturnedAsync(reference, cancelled, cancellationToken);
        return result.IsError ? result.ToActionResult(this) : View(result.Data);
    }

    private async Task<PaymentUrls> UrlsAsync(CancellationToken cancellationToken)
    {
        // The person comes back to the address they are on (a verified tenant host). Gateways post to the public
        // address (General.PublicBaseUrl as it applies to this organisation), never one taken from the request.
        var here = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var configuredBase = await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken);
        var notifyBase = Uri.TryCreate(configuredBase, UriKind.Absolute, out var configured) ? configured.AbsoluteUri.TrimEnd('/') : here;
        return new PaymentUrls(new Uri(here + "/subscription/payments"), new Uri(notifyBase + "/payments/notify"));
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

/// <summary>The built-in Test gateway's checkout page (only when <c>Payments:Gateways:Test:Enabled</c>).</summary>
[Route("payments/test")]
[AllowAnonymous]
public sealed class TestCheckoutController(IPaymentService payments, IServiceProvider services) : Controller
{
    [HttpGet("")]
    public IActionResult Index(string token) =>
        services.GetService<ITestCheckout>()?.Read(token ?? string.Empty) is { } checkout
            ? View(new TestCheckoutViewModel(token!, checkout, Guid.NewGuid().ToString("N")))
            : NotFound();

    [HttpPost("")]
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
