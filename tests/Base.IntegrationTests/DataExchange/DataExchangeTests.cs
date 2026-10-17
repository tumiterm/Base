using System.Text;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.DataExchange;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Users;
using Base.Domain.DataExchange;
using Base.Infrastructure.DataExchange;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.DataExchange;

/// <summary>
/// Exports and imports with real users and real permissions: no test double for the acting user, so background work
/// proves it runs as the person who started it (<see cref="IRunAsUser"/>).
/// </summary>
public sealed class DataExchangeTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestApp _app = null!;
    private Guid _admin;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.CreateAsync(sql.NewDatabase());
        _admin = await CreateUserAsync("admin@example.com", "Ada Admin", RoleNames.Administrator);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Small_exports_download_at_once_and_are_recorded()
    {
        var csv = (await As(_admin, sp => Exports(sp).ExportAsync("users", DataFormat.Csv, ExportFilter.Empty))).GetDataOrThrow();
        var text = await ReadAllTextAsync(csv.File!.Content);
        csv.File.FileName.ShouldBe("users.csv");
        text.ShouldStartWith("Email,Name,Roles,Locked,Has password,Last sign-in,Created"); // the BOM is checked in TabularFormatTests
        text.ShouldContain("admin@example.com,Ada Admin,Administrator,No,Yes,,2026-01-15 10:00:00", customMessage: "local time in the display zone (UTC+2)");

        var xlsx = (await As(_admin, sp => Exports(sp).ExportAsync("users", DataFormat.Xlsx, new ExportFilter(new Dictionary<string, string?> { ["search"] = "ada" })))).GetDataOrThrow();
        var rows = await ReadRowsAsync(new XlsxReader(), xlsx.File!.Content);
        rows[0].ShouldBe(["Email", "Name", "Roles", "Locked", "Has password", "Last sign-in", "Created"]);
        rows[1][0].ShouldBe("admin@example.com");
        rows[1][3].ShouldBe("false");
        rows[1][6].ShouldBe("2026-01-15T10:00:00");

        var history = await As(_admin, sp => Exports(sp).HistoryAsync(new PageRequest()));
        history.Items.Select(j => (j.Status, j.RowCount, j.CanDownload)).ShouldBe([(ExportStatus.Completed, 1, false), (ExportStatus.Completed, 1, false)]);
        (await JobsAsync<ExportJob>()).ShouldContain(j => j.FilterJson.Contains("ada", StringComparison.Ordinal), "the filter is kept for the audit trail");
    }

    [Fact]
    public async Task Exports_need_the_lists_permission()
    {
        var nobody = await CreateUserAsync("nobody@example.com", "No Access", null);

        (await As(nobody, sp => Task.FromResult(Exports(sp).Available))).ShouldBeEmpty();
        (await As(nobody, sp => Exports(sp).ExportAsync("users", DataFormat.Csv, ExportFilter.Empty))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await As(nobody, sp => Exports(sp).ExportAsync("nonsense", DataFormat.Csv, ExportFilter.Empty))).ErrorType.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Large_exports_run_in_the_background_as_the_user_and_only_they_can_download_them()
    {
        await CreateUserAsync("b@example.com", "Bea", null);
        await CreateUserAsync("c@example.com", "Cal", null);
        await As(_admin, async sp => (await sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.Exports.MaxImmediateRows, "1")).IsSuccess.ShouldBeTrue());

        var outcome = (await As(_admin, sp => Exports(sp).ExportAsync("users", DataFormat.Xlsx, ExportFilter.Empty))).GetDataOrThrow();
        outcome.File.ShouldBeNull();

        await DeliverAsync();

        var job = (await JobsAsync<ExportJob>()).Single(j => j.Id == outcome.JobId);
        job.Status.ShouldBe(ExportStatus.Completed);
        job.RowCount.ShouldBe(3);
        job.UserModified.ShouldBe("Ada Admin", "the job ran as the person who asked for it");

        var download = (await As(_admin, sp => Exports(sp).DownloadAsync(job.Id))).GetDataOrThrow();
        (await ReadRowsAsync(new XlsxReader(), download.Content!)).Count.ShouldBe(4);

        var otherAdmin = await CreateUserAsync("other@example.com", "Other Admin", RoleNames.Administrator);
        (await As(otherAdmin, sp => Exports(sp).DownloadAsync(job.Id))).ErrorType.ShouldBe(ErrorType.NotFound);

        await DeliverAsync(); // the in-app notification
        var inbox = await As(_admin, sp => sp.GetRequiredService<INotificationInbox>().GetAsync(new PageRequest()));
        inbox.Items.ShouldHaveSingleItem().Title.ShouldBe("Export: Users");
    }

    [Fact]
    public async Task A_dry_run_reports_every_problem_by_row_and_nothing_is_applied()
    {
        var file = Csv(
            "E-mail,Display name,Roles,Notes",
            "ok@example.com,Okay Person,User,ignored column",
            "not-an-email,Bad Email,,",
            "admin@example.com,Already Here,,",
            "ok@example.com,Same Again,Nonsense,",
            ",No Email,,",
            ",,,");

        var preview = (await As(_admin, sp => Imports(sp).UploadAsync("users", file, "people.csv"))).GetDataOrThrow();

        preview.Status.ShouldBe(ImportStatus.Invalid);
        (preview.TotalRows, preview.ValidRows, preview.InvalidRows).ShouldBe((5, 1, 4), "the empty last row doesn't count");
        preview.Errors.Select(e => (e.Row, e.Column)).ShouldBe([(3, "Email"), (4, "Email"), (5, "Email"), (5, "Roles"), (6, "Email")], ignoreOrder: true,
            customMessage: "business checks run once a row's cells parse");
        preview.Errors.Single(e => e.Row == 4).Message.ShouldBe("A user with this email already exists.");
        preview.Errors.Where(e => e.Row == 5).Select(e => e.Message).ShouldBe(["This email appears earlier in the file.", "There is no role called 'Nonsense'."]);
        preview.CanConfirm.ShouldBeFalse();
        (await As(_admin, sp => Imports(sp).ConfirmAsync(preview.Id))).ErrorType.ShouldBe(ErrorType.Conflict);

        var errorFile = (await As(_admin, sp => Imports(sp).ErrorFileAsync(preview.Id))).GetDataOrThrow();
        errorFile.FileName.ShouldBe("people-errors.csv");
        var rows = await ReadRowsAsync(new CsvReader(), errorFile.Content);
        rows[0].ShouldBe(["E-mail", "Display name", "Roles", "Notes", "Errors"]);
        rows.Count.ShouldBe(5, "header plus the four bad rows");
        rows[1][4].ShouldBe("Email: 'not-an-email' is not an email address.");

        (await UserEmailsAsync()).ShouldNotContain("ok@example.com");
    }

    [Fact]
    public async Task A_confirmed_import_is_applied_in_the_background_as_the_uploader()
    {
        var file = await XlsxAsync(["Email", "Name", "Roles"], ["new.one@example.com", "New One", "User"], ["new.two@example.com", "New Two", null]);

        var preview = (await As(_admin, sp => Imports(sp).UploadAsync("users", file, "invites.xlsx"))).GetDataOrThrow();
        preview.Status.ShouldBe(ImportStatus.Validated);
        (await As(_admin, sp => Imports(sp).ConfirmAsync(preview.Id))).GetDataOrThrow().Status.ShouldBe(ImportStatus.Queued);

        await DeliverAsync();

        var job = (await As(_admin, sp => Imports(sp).GetAsync(preview.Id))).GetDataOrThrow();
        (job.Status, job.AppliedRows, job.FailedRows).ShouldBe((ImportStatus.Completed, 2, 0));
        (await UserEmailsAsync()).ShouldContain("new.one@example.com");
        var created = await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Email == "new.two@example.com"));
        created.UserCreated.ShouldBe("Ada Admin");

        await DeliverAsync();
        _app.Sent.On(Domain.Notifications.NotificationChannel.Email).Select(m => m.To).ShouldBe(["new.one@example.com", "new.two@example.com"], ignoreOrder: true);
        (await As(_admin, sp => sp.GetRequiredService<INotificationInbox>().GetAsync(new PageRequest()))).Items.ShouldContain(i => i.Body == "All 2 rows were applied.");
    }

    [Fact]
    public async Task Imports_cannot_grant_access_the_uploader_does_not_hold()
    {
        var manager = await CreateUserAsync("manager@example.com", "Manny", null);
        await GrantAsync(manager, PermissionCodes.Users.Manage);
        var file = Csv("Email,Name,Roles", "sneaky@example.com,Sneaky,Administrator");

        var preview = (await As(manager, sp => Imports(sp).UploadAsync("users", file, "escalate.csv"))).GetDataOrThrow();
        preview.Status.ShouldBe(ImportStatus.Validated);
        await As(manager, sp => Imports(sp).ConfirmAsync(preview.Id));
        await DeliverAsync();

        var job = (await As(manager, sp => Imports(sp).GetAsync(preview.Id))).GetDataOrThrow();
        (job.Status, job.AppliedRows).ShouldBe((ImportStatus.CompletedWithErrors, 0));
        job.Errors.ShouldHaveSingleItem().Row.ShouldBe(2);
        (await UserEmailsAsync()).ShouldNotContain("sneaky@example.com");
    }

    [Fact]
    public async Task A_broken_workbook_is_reported_not_crashed_on()
    {
        var broken = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4]); // a zip signature, then garbage

        var preview = (await As(_admin, sp => Imports(sp).UploadAsync("users", broken, "broken.xlsx"))).GetDataOrThrow();

        preview.Status.ShouldBe(ImportStatus.Invalid);
        preview.Errors.ShouldHaveSingleItem().Message.ShouldBe("The file is not a valid .xlsx workbook.");
    }

    [Fact]
    public async Task Templates_have_the_headers_an_example_and_drop_downs()
    {
        var template = (await As(_admin, sp => Imports(sp).TemplateAsync("users", DataFormat.Xlsx))).GetDataOrThrow();

        template.FileName.ShouldBe("users-template.xlsx");
        var rows = await ReadRowsAsync(new XlsxReader(), template.Content);
        rows.ShouldBe([["Email", "Name", "Roles"], ["jane@example.com", "Jane Smith", "User"]]);
        (await As(_admin, sp => Task.FromResult(Imports(sp).Available))).ShouldHaveSingleItem().Code.ShouldBe("users");
    }

    private static IExportService Exports(IServiceProvider sp) => sp.GetRequiredService<IExportService>();

    private static IImportService Imports(IServiceProvider sp) => sp.GetRequiredService<IImportService>();

    private static MemoryStream Csv(params string[] lines) => new(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)));

    private static async Task<MemoryStream> XlsxAsync(params string?[][] rows)
    {
        var stream = new MemoryStream();
        await new XlsxWriter().WriteAsync(rows[0].Select(h => new TabularColumn(h!)).ToList(), rows.Skip(1).Select(r => (IReadOnlyList<object?>)r).ToAsyncEnumerable(), stream);
        stream.Position = 0;
        return stream;
    }

    private static async Task<string> ReadAllTextAsync(Stream content)
    {
        await using (content)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return await reader.ReadToEndAsync();
        }
    }

    private static async Task<List<IReadOnlyList<string?>>> ReadRowsAsync(ITabularReader reader, Stream content)
    {
        await using (content)
        {
            var rows = new List<IReadOnlyList<string?>>();
            await foreach (var row in reader.ReadAsync(content))
            {
                rows.Add(row);
            }

            return rows;
        }
    }

    /// <summary>Runs <paramref name="work"/> as the user, exactly as background work does.</summary>
    private Task<T> As<T>(Guid userId, Func<IServiceProvider, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            using var acting = await sp.GetRequiredService<IRunAsUser>().BeginAsync(userId) ?? throw new InvalidOperationException("Can't act as the user.");
            return await work(sp);
        });

    private Task As(Guid userId, Func<IServiceProvider, Task> work) =>
        As<bool>(userId, async sp =>
        {
            await work(sp);
            return true;
        });

    private Task<Guid> CreateUserAsync(string email, string name, string? role) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = name };
            (await users.CreateAsync(user, "Correct-Horse-Battery-Staple")).Succeeded.ShouldBeTrue();
            if (role is not null)
            {
                (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
            }

            return user.Id;
        });

    private Task GrantAsync(Guid userId, string permission) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var roles = sp.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole("Custom " + Guid.NewGuid().ToString("N")[..6]) { Id = Domain.Common.SequentialGuid.Next() };
            (await roles.CreateAsync(role)).Succeeded.ShouldBeTrue();
            await roles.AddClaimAsync(role, new System.Security.Claims.Claim(PermissionCatalog.ClaimType, permission));
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            (await users.AddToRoleAsync((await users.FindByIdAsync(userId.ToString()))!, role.Name!)).Succeeded.ShouldBeTrue();
        });

    private Task<List<string>> UserEmailsAsync() =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().Users.Select(u => u.Email!).ToListAsync());

    private Task<List<T>> JobsAsync<T>()
        where T : class =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().Set<T>().AsNoTracking().ToListAsync());

    private Task DeliverAsync() =>
        ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services).ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);
}
