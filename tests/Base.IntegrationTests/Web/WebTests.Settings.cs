using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Base.Application.Settings;
using Base.Application.Subscriptions;

namespace Base.IntegrationTests.Web;

/// <summary>The settings screen, document previews and the switches, through the full pipeline.</summary>
public sealed partial class WebTests
{
    [Fact]
    public async Task Settings_are_edited_by_category_and_bad_values_come_back_on_the_form()
    {
        var client = Client();
        await SignInAsync(client);

        foreach (var path in new[] { "/admin/settings", "/admin/settings?category=Email", "/admin/settings?category=Branding", "/admin/settings?q=retention" })
        {
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        var email = await client.GetStringAsync("/admin/settings?category=Email", TestContext.Current.CancellationToken);
        email.ShouldContain("Send a test email");
        email.ShouldContain("name=\"setting:Email.Smtp.Password\"");

        var token = await GetFormTokenAsync(client, "/admin/settings?category=General");
        var invalid = await PostFormAsync(client, "/admin/settings", token, ("category", "General"), ("setting:General.PublicBaseUrl", "not a url"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Must be an absolute http(s) URL");

        var saved = await PostFormAsync(client, "/admin/settings", token, ("category", "General"), ("setting:General.Announcement", "Maintenance tonight at 22:00"));
        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await client.GetStringAsync("/", TestContext.Current.CancellationToken)).ShouldContain("Maintenance tonight at 22:00");

        var reverted = await PostFormAsync(client, "/admin/settings/revert", token, ("code", SettingCodes.General.Announcement), ("category", "General"));
        reverted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await client.GetStringAsync("/", TestContext.Current.CancellationToken)).ShouldNotContain("Maintenance tonight");
    }

    [Fact]
    public async Task Document_previews_may_be_framed_only_by_the_application_and_run_nothing()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetMetaTokenAsync(client);

        async Task<string> UploadAsync(byte[] bytes, string fileName)
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "File", fileName }, { new StringContent(fileName), "Title" } };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = form };
            request.Headers.Add("X-CSRF-TOKEN", token);
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;
        }

        var pdf = await UploadAsync("%PDF-1.7 preview"u8.ToArray(), "payslip.pdf");
        var png = await UploadAsync([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2], "scan.png");

        var pdfPreview = await client.GetAsync($"/documents/{pdf}/preview", TestContext.Current.CancellationToken);
        pdfPreview.StatusCode.ShouldBe(HttpStatusCode.OK);
        pdfPreview.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdfPreview.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        pdfPreview.Headers.GetValues("X-Frame-Options").ShouldBe(["SAMEORIGIN"]);
        var pdfPolicy = pdfPreview.Headers.GetValues("Content-Security-Policy").Single();
        pdfPolicy.ShouldContain("frame-ancestors 'self'");
        pdfPolicy.ShouldNotContain("sandbox");

        var imagePreview = await client.GetAsync($"/documents/{png}/preview", TestContext.Current.CancellationToken);
        imagePreview.Headers.GetValues("Content-Security-Policy").Single().ShouldStartWith("sandbox; default-src 'none'");

        // Every other response still refuses to be framed.
        var page = await client.GetAsync($"/documents/{pdf}", TestContext.Current.CancellationToken);
        page.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        (await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"src=\"/documents/{pdf}/preview?version=1\"");
    }

    [Fact]
    public async Task Password_reset_can_be_switched_off()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetFormTokenAsync(client, "/admin/settings?category=Security");
        (await PostFormAsync(client, "/admin/settings", token, ("category", "Security"), ("setting:Security.PasswordResetEnabled", "false")))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var anonymous = Client();
        try
        {
            (await anonymous.GetAsync("/account/forgot-password", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await anonymous.GetStringAsync("/account/login", TestContext.Current.CancellationToken)).ShouldNotContain("Forgot your password?");
        }
        finally
        {
            await PostFormAsync(client, "/admin/settings/revert", token, ("code", SettingCodes.Security.PasswordResetEnabled), ("category", "Security"));
        }
    }

    [Fact]
    public async Task Release_pages_render_for_the_people_who_take_and_watch_releases()
    {
        var client = Client();
        await SignInAsync(client);
        (await client.GetStringAsync("/admin/releases", TestContext.Current.CancellationToken)).ShouldContain("You're up to date");
        (await client.GetStringAsync("/platform/releases", TestContext.Current.CancellationToken)).ShouldContain("Organisations by release");
        (await client.GetStringAsync("/platform/tenants", TestContext.Current.CancellationToken)).ShouldContain("Release");
    }

    [Fact]
    public async Task Feature_flags_are_switched_from_the_platform_page_and_the_kill_switch_shows_where_it_bites()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetFormTokenAsync(client, "/platform/flags");
        (await client.GetStringAsync("/platform/flags", TestContext.Current.CancellationToken)).ShouldContain("Message sending");
        try
        {
            (await PostFormAsync(client, "/platform/flags/Flag.MessageSending", token, ("Mode", "Off"), ("Percentage", "0")))
                .StatusCode.ShouldBe(HttpStatusCode.Redirect);
            (await client.GetStringAsync("/messages", TestContext.Current.CancellationToken)).ShouldContain("Sending is paused");
            (await PostFormAsync(client, "/platform/flags/Flag.Unknown", token, ("Mode", "Off"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await PostFormAsync(client, "/platform/flags/Flag.MessageSending", token, ("Mode", "Everyone"), ("Percentage", "0"));
        }

        (await client.GetStringAsync("/messages", TestContext.Current.CancellationToken)).ShouldNotContain("Sending is paused");
    }

    [Fact]
    public async Task Gateways_reach_the_notification_endpoint_without_signing_in_and_only_verified_notices_count()
    {
        var anonymous = Client();
        using var forged = new FormUrlEncodedContent([new("token", "forged"), new("outcome", "pay")]);
        (await anonymous.PostAsync("/payments/notify/test", forged, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var unknown = new FormUrlEncodedContent([new("x", "y")]);
        (await anonymous.PostAsync("/payments/notify/nobody", unknown, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/payments/test?token=forged", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/payments/go/PNOSUCHPAYMT", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "payment links are anonymous; an unknown one is simply not found");

        // Gateways sign the raw body: it must reach them exactly as sent, as well as parsed.
        using var raw = new StringContent("reference=PNOBODY12345&note=a+b%26c", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        (await anonymous.PostAsync("/payments/notify/rawbody", raw, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var client = Client();
        await SignInAsync(client);
        (await client.GetStringAsync("/platform/payments", TestContext.Current.CancellationToken)).ShouldContain("Test payments");
        (await client.GetStringAsync("/subscription", TestContext.Current.CancellationToken)).ShouldContain("No subscription needed");
    }

    [Fact]
    public async Task The_running_version_is_reported()
    {
        var client = Client();
        await SignInAsync(client);
        var version = await client.GetFromJsonAsync<JsonElement>("/api/system/version", TestContext.Current.CancellationToken);
        version.GetProperty("baseVersion").GetString()!.ShouldMatch(@"^\d+\.\d+\.\d+$");
        (await client.GetStringAsync("/admin/system", TestContext.Current.CancellationToken)).ShouldContain("Version " + version.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Plans_and_features_can_be_compared_and_saved_on_one_page()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetMetaTokenAsync(client);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/platform/plans")
        {
            Content = JsonContent.Create(new
            {
                code = "grid",
                name = "Grid",
                currency = "ZAR",
                monthlyPrice = 100,
                annualPrice = 1000,
                trialDays = 0,
                sortOrder = 0,
                entitlements = new Dictionary<string, long?> { [FeatureCodes.Documents] = 1 },
            }),
        };
        create.Headers.Add("X-CSRF-TOKEN", token);
        var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString();

        var grid = await client.GetStringAsync("/platform/plans/features", TestContext.Current.CancellationToken);
        grid.ShouldContain("Documents from templates");
        grid.ShouldContain("Premium");
        grid.ShouldContain($"{id}|{FeatureCodes.DocumentGeneration}");

        var saved = await PostFormAsync(client, "/platform/plans/features", token,
            ("cell", $"{id}|{FeatureCodes.DocumentGeneration}"), ($"on:{id}|{FeatureCodes.DocumentGeneration}", "1"),
            ("cell", $"{id}|{LimitCodes.Users}"), ($"limit:{id}|{LimitCodes.Users}", "12"));
        saved.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var plan = await client.GetFromJsonAsync<JsonElement>($"/api/platform/plans/{id}", TestContext.Current.CancellationToken);
        plan.GetProperty("entitlements").GetProperty(FeatureCodes.DocumentGeneration).GetInt64().ShouldBe(1);
        plan.GetProperty("entitlements").GetProperty(LimitCodes.Users).GetInt64().ShouldBe(12);
        (await client.GetStringAsync("/platform/plans", TestContext.Current.CancellationToken)).ShouldContain("Plans and features");
    }

    [Fact]
    public async Task Messages_can_be_written_previewed_and_sent_from_the_pages()
    {
        var client = Client();
        await SignInAsync(client);
        var token = await GetMetaTokenAsync(client);
        foreach (var path in new[] { "/messages", "/messages/new", "/messages/templates", "/messages/templates/new" })
        {
            (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        var list = "Name,Email,Ticket\nAmy Adams,amy@example.com,T-7";
        var reach = await PostFormAsync(client, "/messages/reach", token, ("Mode", "list"), ("ListText", list));
        var counts = await reach.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        counts.GetProperty("people").GetInt32().ShouldBe(1);
        counts.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ShouldContain("Ticket");

        var preview = await PostFormAsync(client, "/messages/new", token,
            ("Mode", "list"), ("ListText", list), ("Channels", "Email"), ("Subject", "Your ticket"), ("HtmlBody", "<p>Ticket {{Ticket}} for {{FirstName}}</p>"), ("When", "now"), ("Action", "preview"));
        (await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Ticket T-7 for Amy");

        var sent = await PostFormAsync(client, "/messages/new", token,
            ("Mode", "list"), ("ListText", list), ("Channels", "Email"), ("Subject", "Your ticket"), ("HtmlBody", "<p>Ticket {{Ticket}}</p>"), ("When", "now"), ("Action", "send"));
        sent.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var details = await client.GetStringAsync(sent.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        details.ShouldContain("Your ticket");
        details.ShouldContain("A list of 1");

        var template = await PostFormAsync(client, "/messages/templates/new", token, ("Name", "Tickets"), ("Subject", "Your ticket"), ("HtmlBody", "<p>Hi {{FirstName}}</p>"));
        template.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await client.GetStringAsync("/messages/templates", TestContext.Current.CancellationToken)).ShouldContain("Tickets");
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields)
    {
        var values = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("__RequestVerificationToken", token));
        return client.PostAsync(path, new FormUrlEncodedContent(values), TestContext.Current.CancellationToken);
    }
}
