using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Messaging;
using Base.Domain.Common;
using Base.Domain.Files;
using Base.Domain.Tenancy;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Persistence.Conventions;
using Base.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Persistence;

public sealed class PersistenceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestApp _app = null!;
    private Guid _otherTenantId;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services => services.AddScoped<IDomainEventHandler<TenantCreated>, RecordingHandler>());
        _otherTenantId = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var other = new Tenant("other", "Other Ltd");
            db.Tenants.Add(other);
            await db.SaveChangesAsync();
            return other.Id;
        });
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Model_has_no_changes_missing_from_migrations() =>
        await _app.InScopeAsync(null, sp =>
        {
            sp.GetRequiredService<AppDbContext>().Database.HasPendingModelChanges().ShouldBeFalse("Run: dotnet ef migrations add <Name>");
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Every_soft_deletable_and_tenant_owned_entity_has_its_named_filter() =>
        await _app.InScopeAsync(null, sp =>
        {
            var model = sp.GetRequiredService<AppDbContext>().Model;
            foreach (var entity in model.GetEntityTypes().Where(e => !e.IsOwned() && !Attribute.IsDefined(e.ClrType, typeof(SkipAuditAttribute))))
            {
                var filters = entity.GetDeclaredQueryFilters().Select(f => f.Key).ToList();
                if (typeof(ISoftDeletable).IsAssignableFrom(entity.ClrType))
                {
                    filters.ShouldContain(QueryFilters.SoftDelete, entity.DisplayName());
                }

                if (typeof(ITenantOwned).IsAssignableFrom(entity.ClrType))
                {
                    filters.ShouldContain(QueryFilters.Tenant, entity.DisplayName());
                }
            }

            return Task.CompletedTask;
        });

    [Fact]
    public async Task Audit_columns_are_stamped_in_utc_and_created_is_never_overwritten()
    {
        var created = _app.Time.GetUtcNow();
        var id = await AddFileAsync(_app.DefaultTenantId);

        _app.Time.Advance(TimeSpan.FromHours(3));
        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var file = await db.StoredFiles.SingleAsync(f => f.Id == id);
            file.DateCreated = DateTimeOffset.MinValue; // an attempted rewrite must be ignored
            file.UserCreated = "attacker";
            db.Entry(file).Property(f => f.OriginalFileName).CurrentValue = "renamed.pdf";
            await db.SaveChangesAsync();
        });

        var stored = await LoadAsync(_app.DefaultTenantId, id);
        stored.DateCreated.ShouldBe(created);
        stored.UserCreated.ShouldBe(ICurrentUser.SystemName);
        stored.DateModified.ShouldBe(created.AddHours(3));
        stored.DateModified.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Ids_ascend_in_sql_server_order_so_inserts_append_to_the_clustered_index()
    {
        var inserted = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            inserted.Add(await AddFileAsync(_app.DefaultTenantId, category: "order"));
        }

        var sqlOrder = await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<AppDbContext>().StoredFiles.Where(f => f.Category == "order").OrderBy(f => f.Id).Select(f => f.Id).ToListAsync());

        sqlOrder.ShouldBe(inserted);
    }

    [Fact]
    public async Task Remove_soft_deletes_and_hides_the_row_until_the_filter_is_lifted()
    {
        var id = await AddFileAsync(_app.DefaultTenantId);

        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
            var repository = unitOfWork.Repository<StoredFile>();
            repository.Remove((await repository.GetByIdAsync(id))!);
            await unitOfWork.SaveAsync();
        });

        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.StoredFiles.AnyAsync(f => f.Id == id)).ShouldBeFalse();

            var retired = await db.StoredFiles.IgnoreQueryFilters([QueryFilters.SoftDelete]).SingleAsync(f => f.Id == id);
            retired.IsDeleted.ShouldBeTrue();
            retired.DateDeleted.ShouldBe(_app.Time.GetUtcNow());
            retired.UserDeleted.ShouldBe(ICurrentUser.SystemName);
        });
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_rows_unless_in_system_scope()
    {
        var id = await AddFileAsync(_otherTenantId);

        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.StoredFiles.AnyAsync(f => f.Id == id)).ShouldBeFalse();

            // Lifting only the soft-delete filter must keep tenant isolation.
            (await db.StoredFiles.IgnoreQueryFilters([QueryFilters.SoftDelete]).AnyAsync(f => f.Id == id)).ShouldBeFalse();

            using (sp.GetRequiredService<ITenantContext>().BeginSystemScope())
            {
                (await db.StoredFiles.AnyAsync(f => f.Id == id)).ShouldBeTrue();
            }
        });
    }

    [Fact]
    public async Task Unresolved_tenant_sees_nothing_and_cannot_insert()
    {
        await AddFileAsync(_app.DefaultTenantId);

        await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.StoredFiles.CountAsync()).ShouldBe(0);

            db.StoredFiles.Add(NewFile());
            var error = await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            error.Message.ShouldContain("no tenant has been resolved");
        });
    }

    [Fact]
    public async Task Cross_tenant_writes_are_blocked()
    {
        var id = await AddFileAsync(_otherTenantId);

        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            StoredFile file;
            using (sp.GetRequiredService<ITenantContext>().BeginSystemScope())
            {
                file = await db.StoredFiles.SingleAsync(f => f.Id == id);
            }

            file.IsDeleted = true;
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Concurrent_edits_are_detected_by_row_version()
    {
        var id = await AddFileAsync(_app.DefaultTenantId);

        await using var first = _app.Services.CreateAsyncScope();
        await using var second = _app.Services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(_app.DefaultTenantId, null);
        second.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(_app.DefaultTenantId, null);
        var db1 = first.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = second.ServiceProvider.GetRequiredService<AppDbContext>();

        var copy1 = await db1.StoredFiles.SingleAsync(f => f.Id == id);
        var copy2 = await db2.StoredFiles.SingleAsync(f => f.Id == id);

        copy1.IsDeleted = true;
        await db1.SaveChangesAsync();

        copy2.IsDeleted = true;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Transaction_rolls_back_everything_on_failure()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async ct =>
            {
                var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
                await unitOfWork.Repository<StoredFile>().AddAsync(NewFile("rollback.pdf"), ct);
                await unitOfWork.SaveAsync(ct);
                throw new InvalidOperationException("boom");
            })));

        await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
            (await sp.GetRequiredService<AppDbContext>().StoredFiles.AnyAsync(f => f.OriginalFileName == "rollback.pdf")).ShouldBeFalse());
    }

    [Fact]
    public async Task Paging_and_projection_run_in_the_database()
    {
        for (var i = 0; i < 7; i++)
        {
            await AddFileAsync(_app.DefaultTenantId, $"page-{i}.pdf", category: "paging");
        }

        var page = await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IUnitOfWork>().Repository<StoredFile>().GetPagedAsync(
                new PageRequest(2, 3), f => f.OriginalFileName, q => q.OrderBy(f => f.OriginalFileName), f => f.Category == "paging"));

        page.Total.ShouldBe(7);
        page.TotalPages.ShouldBe(3);
        page.Items.ShouldBe(["page-3.pdf", "page-4.pdf", "page-5.pdf"]);
    }

    [Fact]
    public async Task Domain_events_are_stored_with_the_change_and_dispatched_after_commit()
    {
        RecordingHandler.Received.Clear();
        var tenantId = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = new Tenant("events", "Events Ltd");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenant.DomainEvents.ShouldBeEmpty();
            return tenant.Id;
        });

        var message = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().OutboxMessages.SingleAsync(m => m.Payload.Contains(tenantId.ToString())));
        message.ProcessedAt.ShouldBeNull();

        var processor = ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services);
        await processor.ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);

        RecordingHandler.Received.ShouldContain(new TenantCreated(tenantId, "events"));
        var processed = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id));
        processed.ProcessedAt.ShouldNotBeNull();
        processed.Attempts.ShouldBe(0);
    }

    private static StoredFile NewFile(string name = "a.pdf", string category = "test") =>
        new("LocalDisk", $"k/{Guid.NewGuid():N}", name, "application/pdf", 10, new string('a', 64), category, null, null);

    private Task<Guid> AddFileAsync(Guid tenantId, string name = "a.pdf", string category = "test") =>
        _app.InScopeAsync(tenantId, async sp =>
        {
            var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
            var file = await unitOfWork.Repository<StoredFile>().AddAsync(NewFile(name, category));
            await unitOfWork.SaveAsync();
            file.TenantId.ShouldBe(tenantId);
            return file.Id;
        });

    private Task<StoredFile> LoadAsync(Guid tenantId, Guid id) =>
        _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<AppDbContext>().StoredFiles.AsNoTracking().SingleAsync(f => f.Id == id));

    private sealed class RecordingHandler : IDomainEventHandler<TenantCreated>
    {
        public static List<TenantCreated> Received { get; } = [];

        public Task HandleAsync(TenantCreated domainEvent, CancellationToken cancellationToken)
        {
            Received.Add(domainEvent);
            return Task.CompletedTask;
        }
    }
}
