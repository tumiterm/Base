using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Documents;
using Base.Application.Documents.Generation;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Domain.Documents;
using Base.Domain.Files;
using Base.Infrastructure.Files;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Documents;

public sealed class DocumentTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services =>
        {
            services.AddSingleton<ICurrentUser>(_actor);
            services.AddSingleton<IDocumentRenderer, FakePdfRenderer>();
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Versions_are_kept_and_each_can_be_downloaded()
    {
        var created = (await Docs(d => d.CreateAsync(new CreateDocumentRequest(
            "Lease agreement", DocumentTypes.General, Pdf("v1"), Tags: ["Lease", "property", "lease"], LinkedTo: new DocumentLink("Property", "12"), Comment: "First draft")))).GetDataOrThrow();

        created.CurrentVersion.ShouldBe(1);
        created.Status.ShouldBe(DocumentStatus.Draft);
        created.Tags.ShouldBe(["lease", "property"]);
        created.Versions.ShouldHaveSingleItem().Comment.ShouldBe("First draft");

        var updated = (await Docs(d => d.AddVersionAsync(created.Id, Pdf("v2", "lease-v2.pdf"), "Signed"))).GetDataOrThrow();
        updated.Versions.Select(v => v.Number).ShouldBe([2, 1]);
        updated.Versions[0].FileName.ShouldBe("lease-v2.pdf");

        (await DownloadText(created.Id, null)).ShouldEndWith("v2");
        (await DownloadText(created.Id, 1)).ShouldEndWith("v1");
        (await Docs(d => d.DownloadAsync(created.Id, 3))).ErrorType.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Final_documents_are_locked_until_reopened_and_archived_ones_until_restored()
    {
        var id = await CreateAsync("Policy");

        (await Docs(d => d.FinalizeAsync(id))).IsSuccess.ShouldBeTrue();
        (await Docs(d => d.AddVersionAsync(id, Pdf("v2"), null))).ErrorType.ShouldBe(ErrorType.Conflict);

        (await Docs(d => d.ArchiveAsync(id))).IsSuccess.ShouldBeTrue();
        (await Docs(d => d.UpdateAsync(id, new UpdateDocumentRequest("Renamed", null, null)))).ErrorType.ShouldBe(ErrorType.Conflict);
        (await Docs(d => d.RestoreAsync(id))).IsSuccess.ShouldBeTrue();
        (await Docs(d => d.GetAsync(id))).GetDataOrThrow().Status.ShouldBe(DocumentStatus.Final, "restore returns it to what it was");

        (await Docs(d => d.ReopenAsync(id))).IsSuccess.ShouldBeTrue();
        (await Docs(d => d.AddVersionAsync(id, Pdf("v2"), null))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_view_permission_people_see_and_edit_only_their_own_drafts()
    {
        var adminsDocument = await CreateAsync("Board minutes");

        _actor.UserId = Guid.NewGuid();
        _actor.With(PermissionCodes.Documents.Create);
        var mine = await CreateAsync("My expense claim");

        var visible = await Docs(d => d.SearchAsync(new DocumentQuery()));
        visible.Items.ShouldHaveSingleItem().Id.ShouldBe(mine);
        (await Docs(d => d.GetAsync(adminsDocument))).ErrorType.ShouldBe(ErrorType.NotFound, "someone else's document looks missing");
        (await Docs(d => d.DownloadAsync(adminsDocument))).ErrorType.ShouldBe(ErrorType.NotFound);

        (await Docs(d => d.GetAsync(mine))).GetDataOrThrow().Can.ShouldBe(new DocumentAbilities(Edit: true, Manage: false));
        (await Docs(d => d.FinalizeAsync(mine))).IsSuccess.ShouldBeTrue();
        (await Docs(d => d.AddVersionAsync(mine, Pdf("v2"), null))).ErrorType.ShouldBe(ErrorType.NotFound, "a final document is no longer the creator's to change");
        (await Docs(d => d.ArchiveAsync(mine))).ErrorType.ShouldBe(ErrorType.NotFound);
        (await Docs(d => d.GetAsync(mine))).IsSuccess.ShouldBeTrue("but they can still read it");

        _actor.With();
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("x", DocumentTypes.General, Pdf("x"))))).ErrorType.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Search_filters_by_type_tag_link_and_hides_archived_documents()
    {
        var invoice = (await Docs(d => d.CreateAsync(new CreateDocumentRequest(
            "Invoice 42", DocumentTypes.General, Pdf("i"), Tags: ["finance"], LinkedTo: new DocumentLink("Invoice", "42"))))).GetDataOrThrow().Id;
        var letter = (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Welcome letter", DocumentTypes.Letter, Pdf("l"))))).GetDataOrThrow().Id;
        var archived = await CreateAsync("Old invoice");
        await Docs(d => d.ArchiveAsync(archived));

        async Task<IEnumerable<Guid>> Find(DocumentQuery query) => (await Docs(d => d.SearchAsync(query))).Items.Select(i => i.Id);

        (await Find(new DocumentQuery())).ShouldBe([letter, invoice], ignoreOrder: true);
        (await Find(new DocumentQuery(IncludeArchived: true))).Count().ShouldBe(3);
        (await Find(new DocumentQuery(Status: DocumentStatus.Archived))).ShouldBe([archived]);
        (await Find(new DocumentQuery(Search: "invoice"))).ShouldBe([invoice]);
        (await Find(new DocumentQuery(Tag: "Finance"))).ShouldBe([invoice]);
        (await Find(new DocumentQuery(DocumentType: DocumentTypes.Letter))).ShouldBe([letter]);
        (await Find(new DocumentQuery(LinkedTo: new DocumentLink("Invoice", "42")))).ShouldBe([invoice]);
        (await Find(new DocumentQuery(LinkedTo: new DocumentLink("Invoice", "43")))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Uploads_follow_the_type_rules_and_the_file_checks()
    {
        var png = new DocumentUpload(new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1]), "scan.png");
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Scan", DocumentTypes.Letter, png)))).Message!.ShouldContain("not allowed");

        var fake = new DocumentUpload(new MemoryStream("MZ not a pdf"u8.ToArray()), "fake.pdf");
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Fake", DocumentTypes.General, fake)))).Message!.ShouldContain("does not match");

        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("x", "nonsense", Pdf("x"))))).Message!.ShouldContain("Unknown document type");
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest(new string('t', 201), DocumentTypes.General, Pdf("x"))))).Errors.ShouldHaveSingleItem().Field.ShouldBe("Title");

        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().StoredFiles.CountAsync())).ShouldBe(0, "nothing is stored for rejected uploads");
    }

    [Fact]
    public async Task Deleting_soft_deletes_everything_and_the_purge_removes_the_bytes_later()
    {
        var id = await CreateAsync("Temporary");
        await Docs(d => d.AddVersionAsync(id, Pdf("v2"), null));
        (await Docs(d => d.DeleteAsync(id))).IsSuccess.ShouldBeTrue();
        (await Docs(d => d.GetAsync(id))).ErrorType.ShouldBe(ErrorType.NotFound);

        var files = await DeletedFilesAsync();
        files.Count.ShouldBe(2);

        _app.Time.Advance(TimeSpan.FromDays(29));
        (await PurgeFilesAsync()).ShouldBe(0, "deleted files are kept for Files.DeletedRetentionDays (30)");

        _app.Time.Advance(TimeSpan.FromDays(2));
        (await PurgeFilesAsync()).ShouldBe(2);
        (await DeletedFilesAsync()).ShouldBeEmpty();
        (await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().DocumentVersions.IgnoreQueryFilters().CountAsync(v => v.DocumentId == id)))
            .ShouldBe(2, "the soft-deleted history stays for the audit trail");
    }

    [Fact]
    public async Task Retention_archives_old_final_letters_then_deletes_them()
    {
        var letter = (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Offer letter", DocumentTypes.Letter, Pdf("l"))))).GetDataOrThrow().Id;
        var general = await CreateAsync("Kept forever");
        await Docs(d => d.FinalizeAsync(letter));
        await Docs(d => d.FinalizeAsync(general));

        _app.Time.Advance(TimeSpan.FromDays(366));
        (await RetentionAsync()).ShouldBe(new DocumentRetentionResult(Archived: 1, Deleted: 0));
        (await Docs(d => d.GetAsync(letter))).GetDataOrThrow().Status.ShouldBe(DocumentStatus.Archived);

        _app.Time.Advance(TimeSpan.FromDays(365 * 5 + 1));
        (await RetentionAsync()).ShouldBe(new DocumentRetentionResult(Archived: 0, Deleted: 1));
        (await Docs(d => d.GetAsync(letter))).ErrorType.ShouldBe(ErrorType.NotFound);
        (await Docs(d => d.GetAsync(general))).GetDataOrThrow().Status.ShouldBe(DocumentStatus.Final);
    }

    [Fact]
    public async Task Templates_render_to_html_with_encoded_values_and_built_ins()
    {
        var file = (await Generate(g => g.RenderAsync(new DocumentGenerationRequest(DocumentTemplates.Letter, DocumentFormats.Html, LetterData())))).GetDataOrThrow();

        file.FileName.ShouldBe("Letter.html");
        file.ContentType.ShouldBe("text/html");
        var html = Encoding.UTF8.GetString(file.Content);
        html.ShouldContain("Dear Jane &lt;Doe&gt;,");
        html.ShouldContain("<span class=\"org\">Base</span>");
        html.ShouldContain("15 January 2026", customMessage: "today in the display time zone");

        (await Generate(g => g.RenderAsync(new DocumentGenerationRequest(DocumentTemplates.Letter, DocumentFormats.Html, new { Subject = "x" }))))
            .Message!.ShouldContain("needs: RecipientName, Body");
        (await Generate(g => g.RenderAsync(new DocumentGenerationRequest(DocumentTemplates.Letter, "docx", LetterData()))))
            .Message!.ShouldBe("Documents can't be produced as 'docx' here. Available: html, pdf.");
    }

    [Fact]
    public async Task Generated_documents_are_stored_with_their_template_and_can_get_new_versions()
    {
        var created = (await Generate(g => g.GenerateAsync(new GenerateDocumentRequest(
            DocumentTemplates.Letter, DocumentFormats.Pdf, LetterData(), Title: "Offer: Jane", LinkedTo: new DocumentLink("Applicant", "7"))))).GetDataOrThrow();

        created.DocumentType.ShouldBe(DocumentTypes.Letter);
        var version = created.Versions.ShouldHaveSingleItem();
        version.Source.ShouldBe(DocumentSource.Generated);
        version.TemplateCode.ShouldBe(DocumentTemplates.Letter);
        version.FileName.ShouldBe("Offer_ Jane.pdf");
        (await DownloadText(created.Id, null)).ShouldStartWith("%PDF-fake");

        // HTML isn't an allowed upload type, but generated output doesn't go through the upload allow-list.
        var html = (await Generate(g => g.GenerateAsync(new GenerateDocumentRequest(DocumentTemplates.Letter, DocumentFormats.Html, LetterData(), AddToDocumentId: created.Id))))
            .GetDataOrThrow();
        html.Versions[0].ContentType.ShouldBe("text/html");

        var final = (await Generate(g => g.GenerateAsync(new GenerateDocumentRequest(DocumentTemplates.Letter, DocumentFormats.Pdf, LetterData(), Finalize: true)))).GetDataOrThrow();
        final.Status.ShouldBe(DocumentStatus.Final);
    }

    [Fact]
    public async Task Database_storage_is_chosen_per_category_and_capped_by_settings()
    {
        await SetAsync(SettingCodes.Files.CategoryProviders, "letter=Database");
        await SetAsync(SettingCodes.Files.DatabaseMaxFileSizeKb, "1");

        var tooBig = new DocumentUpload(new MemoryStream([.. "%PDF-"u8, .. new byte[1100]]), "big.pdf");
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Big", DocumentTypes.Letter, tooBig)))).Message.ShouldBe("Files stored in the database may be at most 1 KB.");

        var small = (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Small", DocumentTypes.Letter, Pdf("in sql"))))).GetDataOrThrow();
        (await DownloadText(small.Id, null)).ShouldEndWith("in sql");
        var stored = await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().StoredFiles.SingleAsync());
        stored.ProviderName.ShouldBe("Database");
        (await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().FileContents.SingleAsync())).TenantId.ShouldBe(_app.DefaultTenantId);

        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Elsewhere", DocumentTypes.General, Pdf("on disk"))))).IsSuccess.ShouldBeTrue();
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().StoredFiles.CountAsync(f => f.ProviderName == "LocalDisk"))).ShouldBe(1);

        // Quota: fill the tenant's allowance directly, then the next upload is refused.
        await SetAsync(SettingCodes.Files.DatabaseTenantQuotaMb, "1");
        await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.FileContents.Add(new FileContent { ObjectKey = $"{_app.DefaultTenantId:N}/filler", TenantId = _app.DefaultTenantId, SizeBytes = 1024 * 1024, Content = [1], CreatedAt = _app.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest("Over quota", DocumentTypes.Letter, Pdf("x"))))).Message!.ShouldStartWith("Database file storage for this tenant is full (1 of 1 MB used)");
    }

    private static DocumentUpload Pdf(string body, string fileName = "document.pdf") => new(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 " + body)), fileName);

    private static object LetterData() => new { RecipientName = "Jane <Doe>", Subject = "Your offer", Body = "Line one\nLine two" };

    private async Task<Guid> CreateAsync(string title) =>
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest(title, DocumentTypes.General, Pdf(title))))).GetDataOrThrow().Id;

    private Task<T> Docs<T>(Func<IDocumentService, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IDocumentService>()));

    private Task<T> Generate<T>(Func<IDocumentGenerator, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IDocumentGenerator>()));

    private async Task<string> DownloadText(Guid id, int? version)
    {
        var download = (await Docs(d => d.DownloadAsync(id, version))).GetDataOrThrow();
        await using var content = download.Content!;
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync();
    }

    private Task SetAsync(string code, string value) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp => (await sp.GetRequiredService<IApplicationSettingService>().SetAsync(code, value)).IsSuccess.ShouldBeTrue());

    private Task<List<StoredFile>> DeletedFilesAsync() =>
        _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().StoredFiles.IgnoreQueryFilters().Where(f => f.IsDeleted).ToListAsync());

    private Task<int> PurgeFilesAsync() =>
        _app.InScopeAsync(null, sp => sp.GetServices<Base.Application.Operations.IScheduledJob>().OfType<Base.Infrastructure.Jobs.FileRetentionJob>().Single().PurgeAsync(CancellationToken.None));

    private Task<DocumentRetentionResult> RetentionAsync() =>
        _app.InScopeAsync(null, sp => sp.GetRequiredService<IDocumentRetention>().ApplyAsync());

    /// <summary>Stands in for a real PDF engine.</summary>
    private sealed class FakePdfRenderer : IDocumentRenderer
    {
        public string Format => DocumentFormats.Pdf;

        public string FileExtension => ".pdf";

        public async Task RenderAsync(DocumentRenderContext context, Stream output, CancellationToken cancellationToken = default) =>
            await output.WriteAsync(Encoding.UTF8.GetBytes("%PDF-fake " + context.Values["Subject"]), cancellationToken);
    }
}
