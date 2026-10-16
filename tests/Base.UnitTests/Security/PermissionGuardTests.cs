using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Security;
using NSubstitute;

namespace Base.UnitTests.Security;

public sealed class PermissionGuardTests
{
    private static ICurrentUser ActorWith(params string[] permissions)
    {
        var actor = Substitute.For<ICurrentUser>();
        actor.HasPermission(Arg.Any<string>()).Returns(call => permissions.Contains(call.Arg<string>(), StringComparer.Ordinal));
        return actor;
    }

    [Fact]
    public void Holding_every_permission_passes() =>
        PermissionGuard.EnsureHoldsAll(ActorWith("A", "B"), ["A", "B"], "do it").IsSuccess.ShouldBeTrue();

    [Fact]
    public void Missing_permissions_are_forbidden_and_named()
    {
        var result = PermissionGuard.EnsureHoldsAll(ActorWith("A"), ["C", "A", "B", "C"], "assign these roles");

        result.ErrorType.ShouldBe(ErrorType.Forbidden);
        result.Message.ShouldBe("You can't assign these roles because it involves permissions you don't have: B, C.");
    }

    [Fact]
    public void Nothing_to_check_passes() => PermissionGuard.EnsureHoldsAll(ActorWith(), [], "x").IsSuccess.ShouldBeTrue();
}
