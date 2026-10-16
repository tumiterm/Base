using Base.Application.Auditing;

namespace Base.UnitTests.Common;

public sealed class AuditChangeTests
{
    [Fact]
    public void Changes_round_trip_and_redaction_carries_no_values()
    {
        IReadOnlyList<AuditChange> changes = [new("Name", "Old", "New"), new("PasswordHash", null, null, Redacted: true)];

        var json = AuditChange.Serialize(changes);

        AuditChange.Deserialize(json).ShouldBe(changes);
        json.ShouldBe("""[{"property":"Name","old":"Old","new":"New"},{"property":"PasswordHash","redacted":true}]""");
    }

    [Fact]
    public void Empty_json_is_no_changes() => AuditChange.Deserialize("").ShouldBeEmpty();
}
