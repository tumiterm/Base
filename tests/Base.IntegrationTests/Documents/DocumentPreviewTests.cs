using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Records;
using Base.Application.Common.Results;
using Base.Application.Documents;
using Base.Application.Files;
using Base.Application.People;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Domain.People;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Documents;

public sealed class DocumentPreviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ZipHeader = [0x50, 0x4B, 0x03, 0x04];

    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services => services.AddSingleton<ICurrentUser>(_actor));

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Pdfs_and_images_open_in_the_browser_and_office_files_only_download()
    {
        var pdf = await CreateAsync("Payslip", "payslip.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 body"));
        var png = await CreateAsync("ID scan", "id.png", [.. PngHeader, 1, 2, 3]);
        var docx = await CreateAsync("Contract", "contract.docx", [.. ZipHeader, 1, 2, 3]);

        var preview = (await Docs(d => d.PreviewAsync(pdf))).GetDataOrThrow();
        preview.Kind.ShouldBe(PreviewKind.Pdf);
        preview.ContentType.ShouldBe("application/pdf");
        await using (preview.Content)
        {
            (await new StreamReader(preview.Content).ReadToEndAsync()).ShouldBe("%PDF-1.7 body");
        }

        var image = (await Docs(d => d.PreviewAsync(png))).GetDataOrThrow();
        await image.Content.DisposeAsync();
        image.Kind.ShouldBe(PreviewKind.Image);

        (await Docs(d => d.PreviewAsync(docx))).ErrorType.ShouldBe(ErrorType.NotFound);
        (await Docs(d => d.DownloadAsync(docx))).IsSuccess.ShouldBeTrue();

        var summary = (await Docs(d => d.SearchAsync(new DocumentQuery()))).Items.Single(d => d.Id == png);
        summary.ShouldSatisfyAllConditions(s => s.FileName.ShouldBe("id.png"), s => s.ContentType.ShouldBe("image/png"), s => s.SizeBytes.ShouldBe(11));
    }

    [Fact]
    public async Task Previews_can_be_switched_off()
    {
        var pdf = await CreateAsync("Payslip", "payslip.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 body"));
        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
            (await sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.Documents.PreviewEnabled, "false")).IsSuccess.ShouldBeTrue());

        (await Docs(d => d.PreviewAsync(pdf))).ErrorType.ShouldBe(ErrorType.NotFound);
        (await Docs(d => d.DownloadAsync(pdf))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Linked_records_are_named_for_people_who_may_see_them_and_never_shown_as_ids()
    {
        var person = (await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IPersonService>().CreateAsync(new SavePersonRequest("Thandi", "Nkosi", JobTitle: "Payroll analyst"))))
            .GetDataOrThrow();
        var toPerson = new RecordRef(nameof(Person), person.ToString());
        var toUnknown = new RecordRef("PurchaseOrder", "42");
        var toMissing = new RecordRef(nameof(Person), Guid.NewGuid().ToString());

        var labels = await Labels([toPerson, toUnknown, toMissing]);
        labels[toPerson].ShouldSatisfyAllConditions(
            l => l.Label!.Title.ShouldBe("Thandi Nkosi"),
            l => l.Label!.Subtitle.ShouldBe("Payroll analyst"),
            l => l.Text.ShouldBe("Person: Thandi Nkosi"));
        labels[toUnknown].ShouldSatisfyAllConditions(l => l.Label.ShouldBeNull(), l => l.TypeName.ShouldBe("Purchase order"));
        labels[toMissing].Label.ShouldBeNull();

        _actor.Permissions.Remove(PermissionCodes.People.View);
        (await Labels([toPerson]))[toPerson].ShouldSatisfyAllConditions(l => l.Label.ShouldBeNull(), l => l.Text.ShouldBe("Person"));
    }

    private Task<IReadOnlyDictionary<RecordRef, ResolvedRecord>> Labels(RecordRef[] records) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IRecordLabels>().ResolveAsync(records));

    private async Task<Guid> CreateAsync(string title, string fileName, byte[] content) =>
        (await Docs(d => d.CreateAsync(new CreateDocumentRequest(title, DocumentTypes.General, new DocumentUpload(new MemoryStream(content), fileName))))).GetDataOrThrow().Id;

    private Task<T> Docs<T>(Func<IDocumentService, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IDocumentService>()));
}
