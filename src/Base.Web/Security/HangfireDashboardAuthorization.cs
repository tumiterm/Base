using Base.Application.Security;
using Hangfire.Dashboard;

namespace Base.Web.Security;

/// <summary>The job dashboard requires the <c>System.Jobs.Manage</c> permission, in every environment.</summary>
internal sealed class HangfireDashboardAuthorization : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true && user.HasClaim(PermissionCatalog.ClaimType, PermissionCodes.System.JobsManage);
    }
}
