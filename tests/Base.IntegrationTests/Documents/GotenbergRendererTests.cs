using System.Net;
using Base.Application.Documents.Generation;
using Base.Infrastructure.Documents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Base.IntegrationTests.Documents;

/// <summary>The request Gotenberg's API documents for HTML conversion. Verified against these docs, not a live server.</summary>
public sealed class GotenbergRendererTests
{
    private static readonly DocumentRenderContext Context = new(
        DocumentTemplateCatalog.Find(DocumentTemplates.Letter)!, "<html><body>Hello</body></html>", new Dictionary<string, string?>(StringComparer.Ordinal), null);

    [Fact]
    public async Task Posts_the_html_as_index_html_and_returns_the_pdf()
    {
        using var http = new FakeHttp(HttpStatusCode.OK, "%PDF-1.7 rendered");
        using var output = new MemoryStream();

        await new GotenbergPdfRenderer(http, NullLogger<GotenbergPdfRenderer>.Instance).RenderAsync(Context, output, TestContext.Current.CancellationToken);

        http.Request!.Method.ShouldBe(HttpMethod.Post);
        http.Request.RequestUri!.ToString().ShouldBe("http://gotenberg:3000/forms/chromium/convert/html");
        http.Body!.ShouldContain("name=files; filename=index.html");
        http.Body!.ShouldContain("<html><body>Hello</body></html>");
        http.Body!.ShouldContain("name=printBackground");
        System.Text.Encoding.ASCII.GetString(output.ToArray()).ShouldBe("%PDF-1.7 rendered");
    }

    [Fact]
    public async Task Failures_become_a_message_users_can_see()
    {
        using var http = new FakeHttp(HttpStatusCode.ServiceUnavailable, "chromium crashed");

        var error = await Should.ThrowAsync<DocumentRenderException>(() =>
            new GotenbergPdfRenderer(http, NullLogger<GotenbergPdfRenderer>.Instance).RenderAsync(Context, new MemoryStream(), TestContext.Current.CancellationToken));

        error.Message.ShouldBe("The PDF could not be produced right now. Please try again later.");
    }

    private sealed class FakeHttp(HttpStatusCode status, string body) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://gotenberg:3000/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
