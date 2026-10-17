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

    [Fact]
    public async Task Administration_screens_render_for_an_administrator()
    {
        var client = Client();
        await SignInAsync(client);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", TestContext.Current.CancellationToken);
        var myId = me.GetProperty("userId").GetString();

        foreach (var path in new[] { "/admin/users", "/admin/users/invite", $"/admin/users/{myId}", "/admin/roles", "/admin/roles/new", "/account/password" })
        {
            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        (await client.GetStringAsync("/admin/users", TestContext.Current.CancellationToken)).ShouldContain(WebAppFactory.AdminEmail);
        (await client.GetStringAsync("/admin/roles", TestContext.Current.CancellationToken)).ShouldContain("Administrator");
    }

    [Fact]
    public async Task Audit_trail_is_browsable_by_administrators()
    {
        var client = Client();
        await SignInAsync(client);

        var page = await client.GetAsync("/admin/audit?EntityType=User", TestContext.Current.CancellationToken);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(WebAppFactory.AdminEmail);

        var api = await client.GetFromJsonAsync<JsonElement>("/api/audit?entityType=User", TestContext.Current.CancellationToken);
        api.GetProperty("total").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Notification_screens_render_and_the_bell_shows_in_the_navbar()
    {
        var client = Client();
        await SignInAsync(client);

        foreach (var path in new[] { "/notifications", "/notifications/preferences", "/admin/notifications", "/admin/notifications/templates", "/admin/notifications/templates/General.Reminder" })
        {
            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        (await client.GetStringAsync("/", TestContext.Current.CancellationToken)).ShouldContain("aria-label=\"Notifications, ");
        (await client.GetStringAsync("/admin/notifications/templates/General.Reminder", TestContext.Current.CancellationToken)).ShouldContain("general_reminder");
        (await client.GetFromJsonAsync<int>("/api/me/notifications/unread-count", TestContext.Current.CancellationToken)).ShouldBeGreaterThanOrEqualTo(0);
        var preferences = await client.GetStringAsync("/api/me/notifications/preferences", TestContext.Current.CancellationToken);
        preferences.ShouldContain("\"channel\":\"WhatsApp\"");
    }

    [Fact]
    public async Task Administration_screens_require_sign_in()
    {
        var response = await Client().GetAsync("/admin/users", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldContain("/account/login");
    }

    [Fact]
    public async Task Users_can_be_invited_and_found_through_the_api()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetMetaTokenAsync(client);

        using var invite = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = JsonContent.Create(new { email = "api.invitee@example.com", displayName = "Api Invitee", roles = new[] { "User" } }),
        };
        invite.Headers.Add("X-CSRF-TOKEN", token);
        var created = await client.SendAsync(invite, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var found = await client.GetFromJsonAsync<JsonElement>("/api/users?search=invitee", TestContext.Current.CancellationToken);
        found.GetProperty("total").GetInt32().ShouldBe(1);
        found.GetProperty("items")[0].GetProperty("hasPassword").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Documents_are_uploaded_generated_and_downloaded_through_the_api_and_pages()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetMetaTokenAsync(client);
        var pdf = "%PDF-1.7 web test"u8.ToArray();

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(pdf), "File", "contract.pdf" },
            { new StringContent("Contract"), "Title" },
            { new StringContent("general"), "Type" },
            { new StringContent("legal, signed"), "Tags" },
        };
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = form };
        upload.Headers.Add("X-CSRF-TOKEN", token);
        var created = await client.SendAsync(upload, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var document = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = document.GetProperty("id").GetString();
        document.GetProperty("status").GetString().ShouldBe("Draft");

        (await client.GetByteArrayAsync($"/api/documents/{id}/download", TestContext.Current.CancellationToken)).ShouldBe(pdf);

        using var generate = new HttpRequestMessage(HttpMethod.Post, "/api/documents/generate")
        {
            Content = JsonContent.Create(new
            {
                templateCode = "General.Letter",
                format = "html",
                data = new { RecipientName = "Ann", Subject = "Welcome", Body = "Hello" },
                finalize = true,
            }),
        };
        generate.Headers.Add("X-CSRF-TOKEN", token);
        var generated = await client.SendAsync(generate, TestContext.Current.CancellationToken);
        generated.StatusCode.ShouldBe(HttpStatusCode.Created, await generated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var letters = await client.GetFromJsonAsync<JsonElement>("/api/documents?type=letter", TestContext.Current.CancellationToken);
        letters.GetProperty("total").GetInt32().ShouldBe(1);
        (await client.GetStringAsync("/api/documents/templates", TestContext.Current.CancellationToken)).ShouldContain("General.Letter");

        foreach (var path in new[] { "/documents", $"/documents/{id}", "/documents/generate", "/documents/generate?template=General.Letter" })
        {
            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        var detailsPage = await client.GetStringAsync($"/documents/{id}", TestContext.Current.CancellationToken);
        detailsPage.ShouldContain("contract.pdf");
        detailsPage.ShouldContain($"href=\"/documents/{id}/download?version=1\"", customMessage: "pages link to pages, not the API");
        (await client.GetStringAsync("/documents", TestContext.Current.CancellationToken)).ShouldContain("action=\"/documents\"");
    }

    [Fact]
    public async Task Lists_export_and_files_import_through_the_pages_and_the_api()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetMetaTokenAsync(client);

        var page = await client.GetStringAsync("/admin/users?search=admin", TestContext.Current.CancellationToken);
        page.ShouldContain("href=\"/exports/users?format=csv&amp;search=admin\"", customMessage: "the export keeps the list's filter");

        var csv = await client.GetAsync("/exports/users?format=csv&search=admin", TestContext.Current.CancellationToken);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        (await csv.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(WebAppFactory.AdminEmail);

        var api = await client.GetAsync("/api/exports/audit?format=xlsx", TestContext.Current.CancellationToken);
        api.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await api.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Take(2).ShouldBe("PK"u8.ToArray(), "an xlsx is a zip");

        var template = await client.GetAsync("/imports/users/template?format=csv", TestContext.Current.CancellationToken);
        (await template.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Email,Name,Roles");

        using var form = new MultipartFormDataContent { { new ByteArrayContent("Email,Name\r\nbad,Someone\r\n"u8.ToArray()), "file", "people.csv" } };
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/imports/users") { Content = form };
        upload.Headers.Add("X-CSRF-TOKEN", token);
        var preview = await client.SendAsync(upload, TestContext.Current.CancellationToken);
        preview.StatusCode.ShouldBe(HttpStatusCode.Created, await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var job = await preview.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        job.GetProperty("status").GetString().ShouldBe("Invalid");
        var id = job.GetProperty("id").GetString();

        foreach (var path in new[] { "/imports", "/exports", $"/imports/{id}" })
        {
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        (await client.GetStringAsync($"/imports/{id}", TestContext.Current.CancellationToken)).ShouldContain("is not an email address");
        (await client.GetStringAsync($"/api/imports/{id}/errors", TestContext.Current.CancellationToken)).ShouldContain("Errors");
    }

    [Fact]
    public async Task Password_pages_are_anonymous_and_set_password_is_never_cached()
    {
        var client = Client();

        (await client.GetAsync("/account/forgot-password", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var setPassword = await client.GetAsync($"/account/set-password?kind=invite&userId={Guid.NewGuid()}&token=abc", TestContext.Current.CancellationToken);
        setPassword.StatusCode.ShouldBe(HttpStatusCode.OK);
        setPassword.Headers.CacheControl!.NoStore.ShouldBeTrue();
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
