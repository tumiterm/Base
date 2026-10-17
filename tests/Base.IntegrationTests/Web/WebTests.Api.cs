using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Base.Application.Security;

namespace Base.IntegrationTests.Web;

/// <summary>The API as another system sees it: keys, permissions, idempotency, limits, versions and the document.</summary>
public sealed partial class WebTests
{
    [Fact]
    public async Task An_api_key_calls_the_api_with_its_own_permissions_and_no_cookie_or_csrf_token()
    {
        var key = await CreateApiClientAsync("Directory sync", [PermissionCodes.People.View, PermissionCodes.People.Manage]);
        var api = ApiClient(key);

        var list = await api.GetAsync("/api/people", TestContext.Current.CancellationToken);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        list.Headers.GetValues("api-supported-versions").ShouldContain("1.0");

        var created = await api.PostAsJsonAsync("/api/people", new { firstName = "Key", lastName = "Created" }, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var viaHeader = _factory.CreateClient(new() { AllowAutoRedirect = false });
        viaHeader.DefaultRequestHeaders.Add("X-Api-Key", key);
        (await viaHeader.GetAsync("/api/people", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A key is for the API only; pages still want a signed-in person.
        (await api.GetAsync("/people", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var history = await AdminGetJsonAsync("/api/audit?entityType=Person");
        history.GetProperty("items").EnumerateArray().ShouldContain(e => e.GetProperty("userName").GetString() == "API: Directory sync");
    }

    [Fact]
    public async Task Bad_keys_are_401_and_missing_permissions_403_as_problem_details()
    {
        var readOnly = ApiClient(await CreateApiClientAsync("Read only", [PermissionCodes.People.View]));
        var forbidden = await readOnly.PostAsJsonAsync("/api/people", new { firstName = "No", lastName = "Way" }, TestContext.Current.CancellationToken);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        forbidden.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var bogus = ApiClient("bk_abcdefgh_" + new string('x', 32));
        var unauthorized = await bogus.GetAsync("/api/people", TestContext.Current.CancellationToken);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unauthorized.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
        (await unauthorized.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("title").GetString().ShouldBe("Unauthorized");
    }

    [Fact]
    public async Task A_retried_call_with_the_same_idempotency_key_is_answered_from_the_first()
    {
        var api = ApiClient(await CreateApiClientAsync("Payments", [PermissionCodes.People.Manage]));
        var key = Guid.NewGuid().ToString();

        async Task<HttpResponseMessage> PostAsync(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/people") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key);
            return await api.SendAsync(request, TestContext.Current.CancellationToken);
        }

        var first = await PostAsync(new { firstName = "Once", lastName = "Only" });
        var retry = await PostAsync(new { firstName = "Once", lastName = "Only" });

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await AdminGetJsonAsync("/api/people?search=Only")).GetProperty("total").GetInt32().ShouldBe(1);

        (await PostAsync(new { firstName = "Different", lastName = "Body" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Each_client_has_its_own_rate_limit_and_unsupported_versions_are_refused()
    {
        var api = ApiClient(await CreateApiClientAsync("Tiny", [PermissionCodes.People.View], requestsPerMinute: 2));

        (await api.GetAsync("/api/people", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync("/api/people", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var limited = await api.GetAsync("/api/people", TestContext.Current.CancellationToken);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();

        var other = ApiClient(await CreateApiClientAsync("Unaffected", [PermissionCodes.People.View]));
        (await other.GetAsync("/api/people", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK, "limits are per client");
        (await other.GetAsync("/api/people?api-version=1.0", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync("/api/people?api-version=9.0", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_openapi_document_describes_keys_idempotency_and_errors()
    {
        var json = await Client().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("info").GetProperty("title").GetString().ShouldBe("Base API");
        root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("ApiKey", out _).ShouldBeTrue();
        var createPerson = root.GetProperty("paths").GetProperty("/api/people").GetProperty("post");
        createPerson.GetProperty("parameters").EnumerateArray().ShouldContain(p => p.GetProperty("name").GetString() == "Idempotency-Key");
        createPerson.GetProperty("responses").TryGetProperty("429", out _).ShouldBeTrue();
    }

    private HttpClient ApiClient(string key)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task<string> CreateApiClientAsync(string name, string[] permissions, int? requestsPerMinute = null)
    {
        var admin = Client();
        await SignInAsync(admin);
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Name", name),
            new("ExpiresInDays", "30"),
            new("__RequestVerificationToken", await GetFormTokenAsync(admin, "/admin/api-clients")),
        };
        fields.AddRange(permissions.Select(p => new KeyValuePair<string, string>("Permissions", p)));
        if (requestsPerMinute is { } limit)
        {
            fields.Add(new("RequestsPerMinute", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var response = await admin.PostAsync("/admin/api-clients", new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, html);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue("the key must never be cached");
        var key = IssuedKey().Match(html).Groups["key"].Value;
        key.ShouldNotBeNullOrEmpty(html);
        return key;
    }

    private async Task<JsonElement> AdminGetJsonAsync(string path)
    {
        var admin = Client();
        await SignInAsync(admin);
        return await admin.GetFromJsonAsync<JsonElement>(path, TestContext.Current.CancellationToken);
    }

    [GeneratedRegex("<code id=\"api-key\">(?<key>bk_[A-Za-z0-9_]+)</code>", RegexOptions.None, 1000)]
    private static partial Regex IssuedKey();
}
