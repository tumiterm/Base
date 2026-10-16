using Base.Domain.Common;
using Base.Domain.Tenancy;

namespace Base.UnitTests.Domain;

public sealed class EntityBaseTests
{
    private sealed class Thing : EntityBase;

    private sealed class OtherThing : EntityBase;

    [Fact]
    public void Transient_entities_are_only_equal_to_themselves()
    {
        var a = new Thing();
        var b = new Thing();

        a.Equals(a).ShouldBeTrue();
        a.Equals(b).ShouldBeFalse();
        a.IsTransient.ShouldBeTrue();
    }

    [Fact]
    public void Entities_with_same_type_and_id_are_equal()
    {
        var id = Guid.CreateVersion7();

        (new Thing { Id = id } == new Thing { Id = id }).ShouldBeTrue();
        new Thing { Id = id }.GetHashCode().ShouldBe(new Thing { Id = id }.GetHashCode());
    }

    [Fact]
    public void Entities_of_different_types_are_never_equal()
    {
        var id = Guid.CreateVersion7();

        new Thing { Id = id }.Equals(new OtherThing { Id = id }).ShouldBeFalse();
    }

    [Fact]
    public void Creating_a_tenant_raises_an_event_with_its_id()
    {
        var tenant = new Tenant(" ACME ", "Acme Ltd");

        tenant.Code.ShouldBe("acme");
        tenant.IsTransient.ShouldBeFalse();
        tenant.DomainEvents.ShouldHaveSingleItem().ShouldBe(new TenantCreated(tenant.Id, "acme"));

        tenant.ClearDomainEvents();
        tenant.DomainEvents.ShouldBeEmpty();
    }
}

public sealed class SequentialGuidTests
{
    [Fact]
    public void Values_ascend_in_sql_server_uniqueidentifier_order()
    {
        // SqlGuid compares exactly like SQL Server's uniqueidentifier (last byte group first).
        var ids = Enumerable.Range(0, 1000).Select(_ => new System.Data.SqlTypes.SqlGuid(SequentialGuid.Next())).ToList();

        ids.ShouldBe(ids.Order().ToList());
        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void Guid_v7_is_not_sequential_for_sql_server()
    {
        // Documents why the base doesn't use Guid.CreateVersion7 for keys on SQL Server.
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(0, 200).Select(i => new System.Data.SqlTypes.SqlGuid(Guid.CreateVersion7(start.AddMilliseconds(i)))).ToList();

        ids.ShouldNotBe(ids.Order().ToList());
    }
}
