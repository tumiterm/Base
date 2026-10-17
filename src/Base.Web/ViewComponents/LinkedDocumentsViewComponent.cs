using Base.Application.Documents;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.ViewComponents;

/// <summary>
/// The documents attached to one record, with an upload form. Drop it on any details page:
/// <c>@await Component.InvokeAsync("LinkedDocuments", new { entityType = "Invoice", entityId = invoice.Id.ToString() })</c>.
/// </summary>
public sealed class LinkedDocumentsViewComponent(IDocumentService documents, IDocumentAccessPolicy policy) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string entityType, string entityId)
    {
        var page = await documents.SearchAsync(new DocumentQuery(LinkedTo: new DocumentLink(entityType, entityId), PageSize: 100), HttpContext.RequestAborted);
        return View(new LinkedDocumentsViewModel(entityType, entityId, page.Items, policy.CanCreate));
    }
}
