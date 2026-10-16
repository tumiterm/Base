namespace Base.Application.Common.Abstractions;

/// <summary>
/// Who is acting, resolved from the authenticated principal's claims. Never from Session or a static field
/// (FO's <c>Helper.loggedInUser</c> was shared by every request).
/// </summary>
public interface ICurrentUser
{
    const string SystemName = "System";

    Guid? UserId { get; }

    /// <summary>Name to stamp on audit columns: the user's display name, or <see cref="SystemName"/> for jobs.</summary>
    string AuditName { get; }

    bool IsAuthenticated { get; }

    bool HasPermission(string permission);
}
