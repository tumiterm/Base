using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Base.Application.Security;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Base.IntegrationTests.Web;

/// <summary>End-to-end through the real middleware pipeline. One host for the class (Serilog's bootstrap logger is static).</summary>
public sealed partial class WebTests(SqlServerFixture sql) : IAsyncLifetime
{
    private WebAppFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new WebAppFactory(sql.NewDatabase());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Liveness_is_anonymous()
    {
        var response = await Client().GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        var response = await Client().GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_answers_401_problem_details_instead_of_redirecting()
    {
        var response = await Client().GetAsync("/api/me", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pages_require_sign_in_by_default()
    {
        var response = await Client().GetAsync("/account/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldContain("/account/login");
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_echo_a_valid_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Correlation-ID", "test-correlation-123");

        var response = await Client().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Correlation-ID").ShouldBe(["test-correlation-123"]);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Content.Headers.Contains("Content-Security-Policy").ShouldBeFalse();
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("script-src 'self'");
    }

    [Fact]
    public async Task Unhandled_api_errors_become_problem_details_with_a_ticket_and_no_internals()
    {
        var response = await Client().GetAsync("/api/test/throw", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("\"ticket\":\"E-");
        body.ShouldNotContain("hunter2");
        body.ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task Administrator_signs_in_and_receives_every_permission()
    {
        var client = Client();
        await SignInAsync(client);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", TestContext.Current.CancellationToken);

        me.GetProperty("tenant").GetString().ShouldBe("default");
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldBe(PermissionCatalog.All.Select(p => p.Key), ignoreOrder: true);
    }

    [Fact]
    public async Task Wrong_password_shows_one_generic_message()
    {
        var client = Client();
        var token = await GetFormTokenAsync(client, "/account/login");

        var response = await client.PostAsync(
            "/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Email"] = WebAppFactory.AdminEmail,
                ["Password"] = "wrong-password-123",
                ["__RequestVerificationToken"] = token,
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Invalid email or password");
    }

    [Fact]
    public async Task Unsafe_api_calls_need_the_antiforgery_token()
    {
        var client = Client();
        await SignInAsync(client);
        using var content = JsonContent.Create(new { value = "New name" });

        var withoutToken = await client.PutAsync("/api/settings/General.ApplicationName", content, TestContext.Current.CancellationToken);
        withoutToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var token = await GetMetaTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings/General.ApplicationName") { Content = JsonContent.Create(new { value = "New name" }) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        var withToken = await client.SendAsync(request, TestContext.Current.CancellationToken);
        withToken.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task SignInAsync(HttpClient client)
    {
        var token = await GetFormTokenAsync(client, "/account/login");
        var response = await client.PostAsync(
            "/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Email"] = WebAppFactory.AdminEmail,
                ["Password"] = WebAppFactory.AdminPassword,
                ["__RequestVerificationToken"] = token,
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string> GetFormTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path, TestContext.Current.CancellationToken);
        return FormToken().Match(html).Groups["value"].Value;
    }

    private static async Task<string> GetMetaTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);
        return MetaToken().Match(html).Groups["value"].Value;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<value>[^\"]+)\"", RegexOptions.None, 1000)]
    private static partial Regex FormToken();

    [GeneratedRegex("<meta name=\"csrf-token\" content=\"(?<value>[^\"]+)\"", RegexOptions.None, 1000)]
    private static partial Regex MetaToken();
}
