using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Domain.Payments;
using Base.Domain.Subscriptions;

namespace Base.Application.Payments;

/// <summary>One line of the billing history, written out.</summary>
public sealed record BillingEventView(
    Guid Id,
    DateTimeOffset OccurredAt,
    BillingEventKind Kind,
    string Title,
    string? Detail,
    string? Reason,
    decimal? Amount,
    string? Currency,
    string? PaymentReference,
    string ActorName,
    string Tone);

/// <summary>
/// An organisation's billing history: everything that happened to its subscription, payments and automatic renewal,
/// who did it and why. Append-only and kept for good (written by the persistence layer as changes are saved).
/// </summary>
public interface IBillingHistory
{
    /// <summary>This organisation's history. Needs <c>Subscription.View</c>.</summary>
    Task<ValidationResponse<PagedResult<BillingEventView>>> ListMineAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    /// <summary>Any organisation's history. Needs <c>Platform.Tenants.View</c>.</summary>
    Task<ValidationResponse<PagedResult<BillingEventView>>> ListAsync(Guid tenantId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
}

internal sealed class BillingHistory(IUnitOfWork unitOfWork, ITenantContext tenant, ICurrentUser currentUser, IEnumerable<IPaymentGateway> gateways) : IBillingHistory
{
    public Task<ValidationResponse<PagedResult<BillingEventView>>> ListMineAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
        !currentUser.HasPermission(PermissionCodes.Subscription.View) || !tenant.IsResolved
            ? Task.FromResult(ValidationResponse.Forbidden().As<PagedResult<BillingEventView>>())
            : PageAsync(tenant.TenantId, page, pageSize, cancellationToken);

    public async Task<ValidationResponse<PagedResult<BillingEventView>>> ListAsync(Guid tenantId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Platform.TenantsView))
        {
            return ValidationResponse.Forbidden().As<PagedResult<BillingEventView>>();
        }

        using (tenant.BeginSystemScope())
        {
            return await PageAsync(tenantId, page, pageSize, cancellationToken);
        }
    }

    private async Task<ValidationResponse<PagedResult<BillingEventView>>> PageAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Repository<BillingEvent>().GetPagedAsync(
            new PageRequest(page, pageSize),
            e => e,
            q => q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id),
            e => e.TenantId == tenantId,
            cancellationToken);
        var planIds = rows.Items.SelectMany(e => new[] { e.PlanId, e.FromPlanId }).OfType<Guid>().Distinct().ToList();
        var plans = (await unitOfWork.Repository<Plan>().GetAllAsync(p => new { p.Id, p.Name }, p => planIds.Contains(p.Id), cancellationToken: cancellationToken))
            .ToDictionary(p => p.Id, p => p.Name);
        return ValidationResponse.Success(new PagedResult<BillingEventView>(rows.Items.Select(e => View(e, plans)).ToList(), rows.Total, rows.Page, rows.PageSize));
    }

    private BillingEventView View(BillingEvent e, Dictionary<Guid, string> plans)
    {
        var plan = e.PlanId is { } id ? plans.GetValueOrDefault(id, "a removed plan") : "the plan";
        var from = e.FromPlanId is { } fromId ? plans.GetValueOrDefault(fromId, "a removed plan") : null;
        var cycle = e.BillingCycle switch { BillingCycle.Annual => "yearly", BillingCycle.Monthly => "monthly", _ => null };
        var money = e.Amount is { } amount ? $"{e.Currency} {amount.ToString("N2", CultureInfo.InvariantCulture)}" : null;
        var gateway = e.Gateway is null ? null : gateways.FirstOrDefault(g => string.Equals(g.Code, e.Gateway, StringComparison.OrdinalIgnoreCase))?.Name ?? e.Gateway;
        var actor = string.Equals(e.ActorName, ICurrentUser.SystemName, StringComparison.Ordinal) ? "System"
            : e.Gateway is not null && string.Equals(e.ActorName, e.Gateway, StringComparison.OrdinalIgnoreCase) ? gateway ?? e.ActorName
            : e.ActorName;
        var (title, tone) = e.Kind switch
        {
            BillingEventKind.Subscribed => ($"Subscribed to {plan}{(cycle is null ? "" : ", " + cycle)}", "info"),
            BillingEventKind.StatusChanged => ($"Subscription {e.Detail}", "warning"),
            BillingEventKind.PlanChanged => ($"Plan changed{(from is null ? "" : " from " + from)} to {plan}{(cycle is null ? "" : ", " + cycle)}", "info"),
            BillingEventKind.ChangeScheduled => ($"Move{(from is null ? "" : " from " + from)} to {plan}{(cycle is null ? "" : ", " + cycle)} scheduled", "info"),
            BillingEventKind.ChangeWithdrawn => ("Scheduled change of plan withdrawn", "secondary"),
            BillingEventKind.TrialExtended => ("Trial extended", "info"),
            BillingEventKind.PaidUpTo => ("Paid-up date moved on", "success"),
            BillingEventKind.CancelScheduled => ("Set to cancel at the end of the period", "warning"),
            BillingEventKind.Resumed => ("Set to renew again", "info"),
            BillingEventKind.CheckoutStarted => ($"Payment of {money} started with {gateway}", "secondary"),
            BillingEventKind.PaymentReceived => ($"Payment of {money} received", "success"),
            BillingEventKind.PaymentFailed => ($"Payment of {money} failed", "danger"),
            BillingEventKind.PaymentCancelled => ($"Payment of {money} cancelled", "secondary"),
            BillingEventKind.PaymentExpired => ($"Payment of {money} expired without an answer", "secondary"),
            BillingEventKind.RenewalCharged => ($"Automatic renewal charged {money}", "success"),
            BillingEventKind.RenewalOn => ($"Automatic renewal on: {money} {cycle} through {gateway}", "success"),
            BillingEventKind.RenewalChanged => ($"Automatic renewal changed to {money} {cycle} for {plan}", "info"),
            BillingEventKind.RenewalStopped => ("Automatic renewal stopped", "warning"),
            BillingEventKind.RenewalStopFailed => ($"{gateway} couldn't be told to stop the automatic renewal", "danger"),
            BillingEventKind.InvoiceIssued => ($"Invoice issued for {money}", "info"),
            BillingEventKind.InvoiceSent => ("Invoice emailed", "secondary"),
            _ => (e.Kind.ToString(), "secondary"),
        };
        var detail = e.Kind switch
        {
            BillingEventKind.StatusChanged => null,
            BillingEventKind.PaidUpTo => "Paid up to " + e.Detail,
            _ => e.Detail,
        };
        return new BillingEventView(e.Id, e.OccurredAt, e.Kind, title, detail, e.Reason, e.Amount, e.Currency, e.PaymentReference, actor, tone);
    }
}
