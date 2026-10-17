using System.Security.Claims;
using Base.Application.Common.Results;
using Base.Application.Subscriptions;
using Base.Domain.Subscriptions;
using Base.Web.Subscriptions;
using Microsoft.AspNetCore.Http;

namespace Base.IntegrationTests.Web;

public sealed class SubscriptionGateTests
{
    [Theory]
    [InlineData("/documents", SubscriptionStatus.Suspended, 302)]
    [InlineData("/api/documents", SubscriptionStatus.Cancelled, 402)]
    [InlineData("/subscription", SubscriptionStatus.Suspended, 200)]
    [InlineData("/account/logout", SubscriptionStatus.Expired, 200)]
    [InlineData("/documents", SubscriptionStatus.PastDue, 200)]
    public async Task Inactive_subscriptions_only_reach_the_subscription_page_and_sign_out(string path, SubscriptionStatus status, int expected)
    {
        var context = SignedInRequest(path);
        var reachedNext = false;
        var gate = new SubscriptionGateMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });

        await gate.InvokeAsync(context, new FixedEntitlements(new TenantAccess(true, "starter", "Starter", status, null, false, new Dictionary<string, long?>(StringComparer.Ordinal))));

        (reachedNext ? 200 : context.Response.StatusCode).ShouldBe(expected);
        if (expected == 302)
        {
            context.Response.Headers.Location.ToString().ShouldBe("/subscription");
        }
    }

    [Fact]
    public async Task Tenants_without_a_subscription_and_anonymous_visitors_pass()
    {
        var reached = 0;
        var gate = new SubscriptionGateMiddleware(_ =>
        {
            reached++;
            return Task.CompletedTask;
        });

        await gate.InvokeAsync(SignedInRequest("/documents"), new FixedEntitlements(TenantAccess.Unrestricted));
        var anonymous = new DefaultHttpContext();
        anonymous.Request.Path = "/documents";
        await gate.InvokeAsync(anonymous, new FixedEntitlements(new TenantAccess(true, "x", "X", SubscriptionStatus.Suspended, null, false, new Dictionary<string, long?>(StringComparer.Ordinal))));

        reached.ShouldBe(2);
    }

    [Fact]
    public void Feature_gates_must_name_a_catalogued_feature()
    {
        new RequiresFeatureAttribute(FeatureCodes.Documents).Feature.ShouldBe(FeatureCodes.Documents);
        Should.Throw<InvalidOperationException>(() => new RequiresFeatureAttribute(LimitCodes.Users));
        Should.Throw<InvalidOperationException>(() => new RequiresFeatureAttribute("Feature.Typo"));
    }

    private static DefaultHttpContext SignedInRequest(string path)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "a")], "test")) };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FixedEntitlements(TenantAccess access) : ITenantEntitlements
    {
        public Task<TenantAccess> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(access);

        public Task<bool> HasFeatureAsync(string featureCode, CancellationToken cancellationToken = default) => Task.FromResult(access.HasFeature(featureCode));

        public Task<ValidationResponse> EnsureWithinLimitAsync(string limitCode, long adding = 1, CancellationToken cancellationToken = default) =>
            Task.FromResult(ValidationResponse.Success());

        public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
