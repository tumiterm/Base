using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Base.Domain.Documents;

namespace Base.Application.Documents;

/// <summary>
/// Default rules: <c>Documents.View</c> reads everything, <c>Documents.Manage</c> does everything, and people with
/// <c>Documents.Create</c> can read their own documents and edit them while they are drafts.
/// </summary>
internal sealed class DefaultDocumentAccessPolicy(ICurrentUser currentUser) : IDocumentAccessPolicy
{
    public bool CanSeeAll => currentUser.HasPermission(PermissionCodes.Documents.View);

    public bool CanCreate => currentUser.HasPermission(PermissionCodes.Documents.Create);

    public bool Can(Document document, DocumentAction action)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!currentUser.IsAuthenticated)
        {
            return false;
        }

        var isCreator = document.CreatedByUserId is { } creator && creator == currentUser.UserId;
        return action switch
        {
            DocumentAction.Read => CanSeeAll || isCreator,
            DocumentAction.Edit => currentUser.HasPermission(PermissionCodes.Documents.Manage)
                || (isCreator && CanCreate && document.Status == DocumentStatus.Draft),
            _ => currentUser.HasPermission(PermissionCodes.Documents.Manage),
        };
    }
}
