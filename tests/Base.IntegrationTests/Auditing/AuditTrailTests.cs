using Base.Application.Auditing;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Security;
using Base.Application.Users;
using Base.Domain.Auditing;
using Base.Domain.Files;
using Base.Domain.Tenancy;
using Base.Infrastructure.Auditing;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Persistence.Conventions;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Auditing;

public sealed class AuditTrailTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser { AuditName = "Ann Auditor" }.WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services =>
        {
            services.AddSingleton<ICurrentUser>(_actor);
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task The_whole_lifecycle_of_a_record_is_recorded_with_who_and_what()
    {
        var id = await InTenant(async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var file = await uow.Repository<StoredFile>().AddAsync(NewFile("contract.pdf"));
            await uow.SaveAsync();
            return file.Id;
        });

        await Mutate(id, (db, f) => db.Entry(f).Property(x => x.OriginalFileName).CurrentValue = "contract-signed.pdf");
        await Mutate(id, (_, f) => f.IsDeleted = true);
        await Mutate(id, (_, f) => f.IsDeleted = false);
        await Mutate(id, (db, f) => db.StoredFiles.Remove(f));

        var history = (await Search(new AuditQuery(EntityType: nameof(StoredFile), EntityId: id.ToString()))).Items.Reverse().ToList();

        history.Select(h => h.Action).ShouldBe([AuditAction.Created, AuditAction.Updated, AuditAction.Deleted, AuditAction.Restored, AuditAction.Purged]);
        history.ShouldAllBe(h => h.UserName == "Ann Auditor" && h.UserId == _actor.UserId);

        history[0].Changes.ShouldContain(new AuditChange(nameof(StoredFile.OriginalFileName), null, "contract.pdf"));
        history[0].Changes.ShouldContain(c => c.Property == nameof(StoredFile.TenantId) && c.New == _app.DefaultTenantId.ToString());
        history[0].Changes.ShouldNotContain(c => c.Property == nameof(StoredFile.DateCreated) || c.Property == nameof(StoredFile.RowVersion));

        history[1].Changes.ShouldBe([new AuditChange(nameof(StoredFile.OriginalFileName), "contract.pdf", "contract-signed.pdf")]);
        history[2].Changes.ShouldBeEmpty();
        history[4].Changes.ShouldContain(new AuditChange(nameof(StoredFile.OriginalFileName), "contract-signed.pdf", null));
    }

    [Fact]
    public async Task Saving_without_a_real_change_records_nothing()
    {
        var id = await InTenant(async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var file = await uow.Repository<StoredFile>().AddAsync(NewFile("same.pdf"));
            await uow.SaveAsync();
            return file.Id;
        });

        // Update() flags every column as modified, but no value differs.
        await InTenant(async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var repository = uow.Repository<StoredFile>();
            repository.Update((await repository.GetByIdAsync(id))!);
            await uow.SaveAsync();
            return true;
        });

        (await Search(new AuditQuery(EntityId: id.ToString()))).Total.ShouldBe(1);
    }

    [Fact]
    public async Task User_history_covers_role_assignments_and_never_stores_secrets()
    {
        var userId = (await InTenant(sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new("audited@example.com", "Audited", [RoleNames.User])))).GetDataOrThrow();
        await InTenant(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId.ToString()))!;
            return await users.AddPasswordAsync(user, "Correct-Horse-Battery-Staple");
        });

        var history = (await Search(new AuditQuery(EntityId: userId.ToString()))).Items;

        history.ShouldContain(h => h.EntityType == "User" && h.Action == AuditAction.Created);
        history.ShouldContain(h => h.EntityType == "UserRole" && h.Action == AuditAction.Created && h.EntityId.StartsWith(userId.ToString(), StringComparison.Ordinal));
        var passwordChange = history.First(h => h.EntityType == "User" && h.Action == AuditAction.Updated);
        passwordChange.Changes.ShouldContain(new AuditChange(nameof(ApplicationUser.PasswordHash), null, null, Redacted: true));

        var raw = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().AuditEntries.IgnoreQueryFilters().Select(e => e.Changes).ToListAsync());
        var hash = await InTenant(sp => sp.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync());
        raw.ShouldNotContain(changes => changes.Contains(hash!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Permission_grants_appear_in_the_role_history()
    {
        var roleId = (await InTenant(sp => sp.GetRequiredService<IRoleAdministration>().CreateAsync(new("Reviewers", null, [PermissionCodes.Audit.View])))).GetDataOrThrow();

        var history = (await Search(new AuditQuery(EntityId: roleId.ToString()))).Items;

        history.ShouldContain(h => h.EntityType == "Role" && h.Action == AuditAction.Created);
        history.ShouldContain(h => h.EntityType == "RoleClaim" && h.Changes.Any(c => c.New == PermissionCodes.Audit.View));
    }

    [Fact]
    public async Task Tenants_see_only_their_own_entries_and_platform_entries_need_system_scope()
    {
        var otherTenant = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = new Tenant("audit-other", "Other");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return tenant.Id;
        });
        var otherFile = await _app.InScopeAsync(otherTenant, async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var file = await uow.Repository<StoredFile>().AddAsync(NewFile("theirs.pdf"));
            await uow.SaveAsync();
            return file.Id;
        });

        (await Search(new AuditQuery(EntityId: otherFile.ToString()))).Total.ShouldBe(0);
        (await Search(new AuditQuery(EntityType: nameof(Tenant), EntityId: otherTenant.ToString()))).Total.ShouldBe(0);

        var platformView = await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            using (sp.GetRequiredService<ITenantContext>().BeginSystemScope())
            {
                return await sp.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery(EntityType: nameof(Tenant), EntityId: otherTenant.ToString()));
            }
        });
        platformView.Items.ShouldHaveSingleItem().Action.ShouldBe(AuditAction.Created);
    }

    [Fact]
    public async Task Audit_entries_are_append_only()
    {
        await InTenant(async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            await uow.Repository<StoredFile>().AddAsync(NewFile("x.pdf"));
            return await uow.SaveAsync();
        });

        await InTenant(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var entry = await db.AuditEntries.FirstAsync();
            db.Entry(entry).Property(e => e.UserName).CurrentValue = "someone else";
            var error = await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            error.Message.ShouldContain("append-only");

            db.ChangeTracker.Clear();
            db.AuditEntries.Remove(await db.AuditEntries.FirstAsync());
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            return true;
        });
    }

    [Fact]
    public async Task A_rolled_back_change_leaves_no_audit_entry()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => InTenant<bool>(sp =>
            sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync<bool>(async ct =>
            {
                var uow = sp.GetRequiredService<IUnitOfWork>();
                await uow.Repository<StoredFile>().AddAsync(NewFile("ghost.pdf"), ct);
                await uow.SaveAsync(ct);
                throw new InvalidOperationException("boom");
            })));

        (await InTenant(sp => sp.GetRequiredService<AppDbContext>().AuditEntries.CountAsync(e => e.Changes.Contains("ghost.pdf")))).ShouldBe(0);
    }

    [Fact]
    public async Task Retention_removes_only_entries_older_than_the_setting()
    {
        await InTenant(async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            await uow.Repository<StoredFile>().AddAsync(NewFile("old.pdf"));
            return await uow.SaveAsync();
        });
        _app.Time.Advance(TimeSpan.FromDays(731));
        await InTenant(async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            await uow.Repository<StoredFile>().AddAsync(NewFile("new.pdf"));
            return await uow.SaveAsync();
        });

        var removed = await _app.InScopeAsync(null, sp => sp.GetServices<Base.Application.Operations.IScheduledJob>().OfType<Base.Infrastructure.Jobs.AuditRetentionJob>().Single().PurgeAsync(CancellationToken.None));

        removed.ShouldBeGreaterThan(0);
        var remaining = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().AuditEntries.IgnoreQueryFilters([QueryFilters.Tenant]).Select(e => e.Changes).ToListAsync());
        remaining.ShouldContain(c => c.Contains("new.pdf", StringComparison.Ordinal));
        remaining.ShouldNotContain(c => c.Contains("old.pdf", StringComparison.Ordinal));
    }

    private static StoredFile NewFile(string name) =>
        new("LocalDisk", $"k/{Guid.NewGuid():N}", name, "application/pdf", 10, new string('a', 64), "test", null, null);

    private Task Mutate(Guid id, Action<AppDbContext, StoredFile> change) =>
        InTenant(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var file = await db.StoredFiles.IgnoreQueryFilters([QueryFilters.SoftDelete]).SingleAsync(f => f.Id == id);
            change(db, file);
            return await db.SaveChangesAsync();
        });

    private Task<PagedResult<AuditRecord>> Search(AuditQuery query) =>
        InTenant(sp => sp.GetRequiredService<IAuditTrail>().SearchAsync(query));

    private Task<T> InTenant<T>(Func<IServiceProvider, Task<T>> work) => _app.InScopeAsync(_app.DefaultTenantId, work);
}
