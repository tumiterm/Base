using System.Text;
using Base.Application.Auditing;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.DataExchange;
using Base.Application.MasterData;
using Base.Application.People;
using Base.Application.Security;
using Base.Application.Tenancy;
using Base.Domain.DataExchange;
using Base.Domain.People;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Outbox;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.People;

/// <summary>The sample slice end to end, with a real user acting (no test double), as a new module's tests should look.</summary>
public sealed class PeopleTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestApp _app = null!;
    private Guid _admin;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.CreateAsync(sql.NewDatabase());
        _admin = await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "hr@example.com", Email = "hr@example.com", DisplayName = "Hana HR" };
            (await users.CreateAsync(user, "Correct-Horse-Battery-Staple")).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, RoleNames.Administrator)).Succeeded.ShouldBeTrue();
            return user.Id;
        });
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Every_tenant_starts_with_the_catalogue_values_and_keeps_its_own_list()
    {
        var departments = await As(sp => Lookups(sp).GetAsync(LookupLists.People.Department));
        departments.Select(d => d.Code).ShouldBe(["finance", "hr", "it", "operations", "sales"]);

        var added = await As(sp => Lookups(sp).AddAsync(LookupLists.People.Department, "Research & Development"));
        added.IsSuccess.ShouldBeTrue(added.Message);
        (await As(sp => Lookups(sp).AddAsync(LookupLists.People.Department, "research & development"))).IsError.ShouldBeTrue("names are unique, ignoring case");

        var other = await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ITenantAdministration>().CreateAsync(
            new CreateTenantRequest("Other Co", "other", null, "owner@other.test", "Owner", null)));
        await DeliverAsync();
        var theirs = await _app.InScopeAsync(other.GetDataOrThrow(), sp => Lookups(sp).GetAsync(LookupLists.People.Department));
        theirs.Select(d => d.Code).ShouldBe(["finance", "hr", "it", "operations", "sales"], "a new tenant gets the defaults, not our additions");

        // Running the seeding again adds nothing.
        (await _app.InScopeAsync(null, sp => sp.GetRequiredService<ILookupDefaults>().EnsureAsync(_app.DefaultTenantId))).ShouldBe(0);

        var rnd = (await As(sp => Lookups(sp).GetAsync(LookupLists.People.Department))).Single(d => d.Code == "research-development");
        (await As(sp => Lookups(sp).MoveAsync(rnd.Id, -1))).IsSuccess.ShouldBeTrue();
        (await As(sp => Lookups(sp).GetAsync(LookupLists.People.Department))).Select(d => d.Code).ShouldBe(["finance", "hr", "it", "operations", "research-development", "sales"]);
    }

    [Fact]
    public async Task People_are_saved_with_valid_lookups_and_hidden_values_stay_on_existing_records()
    {
        var id = (await As(sp => People(sp).CreateAsync(Thandi()))).GetDataOrThrow();

        var created = (await As(sp => People(sp).GetAsync(id))).GetDataOrThrow();
        created.DisplayName.ShouldBe("Thandi Nkosi");
        created.Email.ShouldBe("thandi@example.com");
        created.Skills.ShouldBe(["data-analysis", "first-aid"]);

        var unknown = await As(sp => People(sp).CreateAsync(Thandi() with { Email = null, DepartmentCode = "astrology" }));
        unknown.Errors.ShouldHaveSingleItem().Field.ShouldBe(nameof(SavePersonRequest.DepartmentCode));
        (await As(sp => People(sp).CreateAsync(Thandi() with { FirstName = "Other" }))).Errors.ShouldHaveSingleItem().Field.ShouldBe(nameof(SavePersonRequest.Email));

        var finance = (await As(sp => Lookups(sp).GetAsync(LookupLists.People.Department))).Single(d => d.Code == "finance");
        await As(sp => Lookups(sp).SetActiveAsync(finance.Id, false));
        (await As(sp => People(sp).UpdateAsync(id, Thandi() with { JobTitle = "Senior analyst" }))).IsSuccess.ShouldBeTrue("she keeps the hidden department");
        (await As(sp => People(sp).CreateAsync(Thandi() with { Email = "new@example.com" }))).IsError.ShouldBeTrue("nobody new can be put in it");
    }

    [Fact]
    public async Task The_list_filters_sorts_and_counts_and_status_follows_its_rules()
    {
        var thandi = (await As(sp => People(sp).CreateAsync(Thandi()))).GetDataOrThrow();
        await As(sp => People(sp).CreateAsync(new SavePersonRequest("Andile", "Zulu", DepartmentCode: "it", Skills: ["leadership"])));
        await As(sp => People(sp).CreateAsync(new SavePersonRequest("Bongani", "Mokoena", DepartmentCode: "finance", StartDate: new DateOnly(2020, 1, 6))));
        (await As(sp => People(sp).ChangeStatusAsync(thandi, PersonStatus.Left))).IsSuccess.ShouldBeTrue();
        (await As(sp => People(sp).ChangeStatusAsync(thandi, PersonStatus.OnLeave))).IsError.ShouldBeTrue();

        (await As(sp => People(sp).SearchAsync(new PersonQuery()))).Items.Select(p => p.LastName).ShouldBe(["Mokoena", "Nkosi", "Zulu"]);
        (await As(sp => People(sp).SearchAsync(new PersonQuery(Department: "finance", Sort: PersonSort.StartDate)))).Items.Select(p => p.FirstName).ShouldBe(["Thandi", "Bongani"]);
        (await As(sp => People(sp).SearchAsync(new PersonQuery(Skill: "leadership")))).Items.ShouldHaveSingleItem().FirstName.ShouldBe("Andile");
        (await As(sp => People(sp).SearchAsync(new PersonQuery("nkos")))).Items.ShouldHaveSingleItem();

        var counts = await As(sp => People(sp).CountAsync(new PersonQuery(Department: "finance")));
        (counts.Active, counts.Left, counts.All).ShouldBe((1, 1, 2));

        (await As(sp => People(sp).DeleteAsync(thandi))).IsSuccess.ShouldBeTrue();
        (await As(sp => People(sp).GetAsync(thandi))).ErrorType.ShouldBe(ErrorType.NotFound);
        (await As(sp => People(sp).CreateAsync(Thandi()))).IsSuccess.ShouldBeTrue("a removed person's email can be used again");
    }

    [Fact]
    public async Task Personal_details_are_redacted_in_the_history_and_lists_are_readable()
    {
        var id = (await As(sp => People(sp).CreateAsync(Thandi()))).GetDataOrThrow();
        await As(sp => People(sp).UpdateAsync(id, Thandi() with { Phone = "+27 82 555 0199", Skills = ["leadership"], StartDate = new DateOnly(2024, 2, 5) }));

        var history = await As(sp => sp.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery("Person", id.ToString())));
        var update = history.Items[0];
        update.UserName.ShouldBe("Hana HR");
        update.Changes.Single(c => c.Property == nameof(Person.Phone)).ShouldSatisfyAllConditions(c => c.Redacted.ShouldBeTrue(), c => c.New.ShouldBeNull());
        update.Changes.Single(c => c.Property == nameof(Person.StartDate)).New.ShouldBe("2024-02-05", "dates are ISO, never culture-formatted");
        update.Changes.Single(c => c.Property == nameof(Person.Skills)).ShouldSatisfyAllConditions(c => c.Old.ShouldBe("data-analysis, first-aid"), c => c.New.ShouldBe("leadership"));
    }

    [Fact]
    public async Task The_list_exports_with_names_and_imports_add_or_update_by_email()
    {
        await As(sp => People(sp).CreateAsync(Thandi()));

        var preview = (await As(sp => Imports(sp).UploadAsync("people", Csv(
            "First name,Last name,Email,Department,Skills,Start date",
            "Thandi,Nkosi-Dube,thandi@example.com,Information technology,Leadership,",
            "Lerato,Molefe,lerato@example.com,HR,First aid; data-analysis,2025-03-01"), "people.csv"))).GetDataOrThrow();
        preview.Status.ShouldBe(ImportStatus.Validated, string.Join("; ", preview.Errors.Select(e => e.Message)));
        await As(sp => Imports(sp).ConfirmAsync(preview.Id));
        await DeliverAsync();
        (await As(sp => Imports(sp).GetAsync(preview.Id))).GetDataOrThrow().AppliedRows.ShouldBe(2);

        var people = (await As(sp => People(sp).SearchAsync(new PersonQuery()))).Items;
        people.Select(p => (p.LastName, p.DepartmentCode)).ShouldBe([("Molefe", "hr"), ("Nkosi-Dube", "it")]);
        people.Single(p => p.FirstName == "Lerato").Skills.ShouldBe(["data-analysis", "first-aid"]);

        var csv = (await As(sp => Exports(sp).ExportAsync("people", DataFormat.Csv, new ExportFilter(new Dictionary<string, string?>(StringComparer.Ordinal) { ["department"] = "it" })))).GetDataOrThrow();
        using var reader = new StreamReader(csv.File!.Content);
        var text = await reader.ReadToEndAsync();
        text.ShouldContain("Thandi,Nkosi-Dube,thandi@example.com,Payroll analyst,Information technology,Active");
        text.ShouldNotContain("Lerato");

        var broken = (await As(sp => Imports(sp).UploadAsync("people", Csv("First name,Last name,Department", "Sipho,Ndlovu,Astrology"), "bad.csv"))).GetDataOrThrow();
        broken.Status.ShouldBe(ImportStatus.Invalid);
        broken.Errors.ShouldHaveSingleItem().Message.ShouldContain("no department called 'Astrology'");
    }

    [Fact]
    public async Task A_photo_is_stored_as_a_file_anyone_who_sees_the_person_can_open()
    {
        var id = (await As(sp => People(sp).CreateAsync(Thandi()))).GetDataOrThrow();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        (await As(sp => People(sp).SetAvatarAsync(id, new MemoryStream(png), "me.png"))).IsSuccess.ShouldBeTrue();
        (await As(sp => People(sp).SetAvatarAsync(id, new MemoryStream("<svg/>"u8.ToArray()), "me.svg"))).IsError.ShouldBeTrue();

        var photo = (await _app.InScopeAsync(_app.DefaultTenantId, sp => People(sp).OpenAvatarAsync(id))).GetDataOrThrow();
        photo.ContentType.ShouldBe("image/png");
        (await As(sp => People(sp).GetAsync(id))).GetDataOrThrow().HasAvatar.ShouldBeTrue();

        (await As(sp => People(sp).RemoveAvatarAsync(id))).IsSuccess.ShouldBeTrue();
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => People(sp).OpenAvatarAsync(id))).ErrorType.ShouldBe(ErrorType.NotFound);
    }

    private static SavePersonRequest Thandi() => new(
        "Thandi", "Nkosi", TitleCode: "ms", Email: "Thandi@Example.com", Phone: "+27 82 555 0100", DateOfBirth: new DateOnly(1990, 4, 12),
        DepartmentCode: "finance", JobTitle: "Payroll analyst", StartDate: new DateOnly(2019, 3, 1), Skills: ["first-aid", "data-analysis"]);

    private static MemoryStream Csv(params string[] lines) => new(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));

    private static IPersonService People(IServiceProvider services) => services.GetRequiredService<IPersonService>();

    private static ILookupService Lookups(IServiceProvider services) => services.GetRequiredService<ILookupService>();

    private static IImportService Imports(IServiceProvider services) => services.GetRequiredService<IImportService>();

    private static IExportService Exports(IServiceProvider services) => services.GetRequiredService<IExportService>();

    private Task<T> As<T>(Func<IServiceProvider, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            using var acting = await sp.GetRequiredService<IRunAsUser>().BeginAsync(_admin) ?? throw new InvalidOperationException("Can't act as the user.");
            return await work(sp);
        });

    private Task DeliverAsync() =>
        ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services).ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);
}
