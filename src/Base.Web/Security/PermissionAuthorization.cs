using Base.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Base.Web.Security;

/// <summary>Gate an action or controller on a permission: <c>[HasPermission(PermissionCodes.Settings.Edit)]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(PermissionCatalog.PolicyPrefix + permission)
{
    public string Permission { get; } = permission;
}

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Creates <c>perm:X</c> policies on demand, so permissions need no individual registration.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(PermissionCatalog.PolicyPrefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }

        var permission = policyName[PermissionCatalog.PolicyPrefix.Length..];
        if (!PermissionCatalog.Exists(permission))
        {
            // A misspelt permission must fail loudly, not silently deny everyone.
            throw new InvalidOperationException($"Permission '{permission}' is not in the PermissionCatalog.");
        }

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
    }
}

internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(PermissionCatalog.ClaimType, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
