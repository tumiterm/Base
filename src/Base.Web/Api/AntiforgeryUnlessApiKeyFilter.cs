using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Base.Web.Api;

/// <summary>
/// Validates the antiforgery token on every unsafe request authenticated by cookie (pages and the API used by the
/// pages), like <see cref="AutoValidateAntiforgeryTokenAttribute"/>. Requests authenticated by API key carry no cookie,
/// so a forged cross-site request can't use them, and they skip the check.
/// </summary>
internal sealed class AntiforgeryUnlessApiKeyFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    private static readonly string[] SafeMethods = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options, HttpMethods.Trace];

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Result is not null
            || SafeMethods.Contains(context.HttpContext.Request.Method, StringComparer.OrdinalIgnoreCase)
            || context.HttpContext.User.IsApiClient()
            || context.Filters.OfType<IAntiforgeryPolicy>().LastOrDefault() is IgnoreAntiforgeryTokenAttribute)
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new AntiforgeryValidationFailedResult();
        }
    }
}
