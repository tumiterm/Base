using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Application.Users;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Identity;

public sealed class RoleAdministrationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services =>
        {
            services.AddSingleton<ICurrentUser>(_actor);
            services.AddSingleton<IEmailSender>(new CapturingEmailSender());
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Roles_can_be_created_changed_and_deleted()
    {
        var id = (await Roles(r => r.CreateAsync(new("Auditor", "Reads things", [PermissionCodes.Settings.View, PermissionCodes.Users.View])))).GetDataOrThrow();

        (await Roles(r => r.UpdateAsync(id, new("Senior Auditor", null, [PermissionCodes.Users.View, PermissionCodes.System.HealthView])))).IsSuccess.ShouldBeTrue();

        var role = (await Roles(r => r.GetAsync(id))).GetDataOrThrow();
        role.Name.ShouldBe("Senior Auditor");
        role.Description.ShouldBeNull();
        role.Permissions.ShouldBe([PermissionCodes.System.HealthView, PermissionCodes.Users.View]);
        role.IsSystem.ShouldBeFalse();

        (await Roles(r => r.DeleteAsync(id))).IsSuccess.ShouldBeTrue();
        (await Roles(r => r.GetAsync(id))).ErrorType.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task System_roles_cannot_be_renamed_or_deleted_and_administrators_keep_everything()
    {
        var roles = await Roles(r => r.ListAsync());
        var admin = roles.Single(r => r.Name == RoleNames.Administrator);
        var user = roles.Single(r => r.Name == RoleNames.User);

        admin.IsSystem.ShouldBeTrue();
        admin.Permissions.Count.ShouldBe(PermissionCatalog.All.Count);
        (await Roles(r => r.UpdateAsync(user.Id, new("Members", null, [])))).ErrorType.ShouldBe(ErrorType.Conflict);
        (await Roles(r => r.UpdateAsync(admin.Id, new(RoleNames.Administrator, null, [PermissionCodes.Users.View])))).ErrorType.ShouldBe(ErrorType.Conflict);
        (await Roles(r => r.DeleteAsync(user.Id))).ErrorType.ShouldBe(ErrorType.Conflict);

        // The User role's permissions are editable.
        (await Roles(r => r.UpdateAsync(user.Id, new(RoleNames.User, "Everyone", [PermissionCodes.Files.Upload, PermissionCodes.Users.View])))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_role_in_use_cannot_be_deleted()
    {
        var id = (await Roles(r => r.CreateAsync(new("Clerks", null, [])))).GetDataOrThrow();
        await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new("clerk@example.com", "Clerk", ["Clerks"])));

        (await Roles(r => r.DeleteAsync(id))).ErrorType.ShouldBe(ErrorType.Conflict);
        (await Roles(r => r.GetAsync(id))).GetDataOrThrow().UserCount.ShouldBe(1);
    }

    [Fact]
    public async Task You_cannot_grant_or_remove_permissions_you_do_not_hold()
    {
        var id = (await Roles(r => r.CreateAsync(new("Ops", null, [PermissionCodes.Settings.Edit])))).GetDataOrThrow();
        _actor.With(PermissionCodes.Roles.Manage);

        (await Roles(r => r.CreateAsync(new("Sneaky", null, [PermissionCodes.Settings.Edit])))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Roles(r => r.UpdateAsync(id, new("Ops", null, [])))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Roles(r => r.DeleteAsync(id))).ErrorType.ShouldBe(ErrorType.Forbidden);

        // Adding a permission you lack is still blocked; changing only what you hold is fine.
        (await Roles(r => r.UpdateAsync(id, new("Ops", "Operations", [PermissionCodes.Settings.Edit, PermissionCodes.Files.ReadAll])))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Roles(r => r.UpdateAsync(id, new("Ops", "Operations", [PermissionCodes.Settings.Edit])))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_role_input_is_reported_per_field()
    {
        var result = await Roles(r => r.CreateAsync(new("", null, ["Not.A.Permission"])));

        result.ErrorType.ShouldBe(ErrorType.Validation);
        result.Errors.Select(e => e.Field).ShouldContain(nameof(SaveRoleRequest.Name));
        result.Errors.ShouldContain(e => e.Field.StartsWith(nameof(SaveRoleRequest.Permissions), StringComparison.Ordinal));
    }

    private Task<T> Roles<T>(Func<IRoleAdministration, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IRoleAdministration>()));
}
