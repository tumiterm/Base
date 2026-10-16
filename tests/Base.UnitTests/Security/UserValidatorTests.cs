using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Users;

namespace Base.UnitTests.Security;

public sealed class UserValidatorTests
{
    [Fact]
    public void Invite_requires_a_valid_email_and_unique_roles()
    {
        var result = new InviteUserRequestValidator().Validate(new InviteUserRequest("not-an-email", "", ["User", "user"]));

        result.Errors.Select(e => e.PropertyName).ShouldBe(["Email", "DisplayName", "Roles"], ignoreOrder: true);
    }

    [Fact]
    public void Role_names_and_permissions_are_checked()
    {
        var result = new SaveRoleRequestValidator().Validate(new SaveRoleRequest("<script>", null, [PermissionCodes.Users.View, "Made.Up"]));

        result.Errors.Select(e => e.PropertyName).ShouldBe(["Name", "Permissions[1]"], ignoreOrder: true);
    }

    [Fact]
    public void Password_confirmation_must_match() =>
        new SetPasswordRequestValidator().Validate(new SetPasswordRequest(Guid.NewGuid(), "t", PasswordTokenKind.Reset, "one-passphrase", "other-passphrase"))
            .Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("ConfirmPassword");

    [Fact]
    public void New_password_must_differ_from_the_current_one() =>
        new ChangePasswordRequestValidator().Validate(new ChangePasswordRequest("same-passphrase", "same-passphrase", "same-passphrase"))
            .Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("NewPassword");

    [Theory]
    [InlineData("https://portal.example.com", true)]
    [InlineData("http://localhost:5000/", true)]
    [InlineData("portal.example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://files.example.com", false)]
    [InlineData("https://example.com/?next=evil", false)]
    public void Public_base_url_must_be_an_absolute_http_url(string value, bool valid) =>
        SettingCatalog.Get(SettingCodes.General.PublicBaseUrl).Validate(value).IsSuccess.ShouldBe(valid);
}
