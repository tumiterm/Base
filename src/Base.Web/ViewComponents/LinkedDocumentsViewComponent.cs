using Base.Application.Documents;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.ViewComponents;

/// <summary>
/// The documents attached to one record, with an upload form. Drop it on any details page:
/// <c>@await Component.InvokeAsync("LinkedDocuments", new { entityType = "Invoice", entityId = invoice.Id.ToString() })</c>.
/// </summary>
public sealed class LinkedDocumentsViewComponent(IDocumentService documents, IDocumentAccessPolicy policy, ITenantEntitlements entitlements, IApplicationSettingService settings) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string entityType, string entityId)
    {
        if (!await entitlements.HasFeatureAsync(FeatureCodes.Documents, HttpContext.RequestAborted))
        {
            return Content(string.Empty);
        }

        var page = await documents.SearchAsync(new DocumentQuery(LinkedTo: new DocumentLink(entityType, entityId), PageSize: 100), HttpContext.RequestAborted);
        return View(new LinkedDocumentsViewModel(
            entityType, entityId, page.Items, policy.CanCreate, await settings.GetBoolAsync(SettingCodes.Documents.PreviewEnabled, HttpContext.RequestAborted)));
    }
}
