using System.Reflection;
using Base.Domain.Common;
using NetArchTest.Rules;

namespace Base.ArchitectureTests;

/// <summary>Dependencies point inward only: Web → Infrastructure → Application → Domain.</summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(EntityBase).Assembly;
    private static readonly Assembly Application = typeof(Base.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(Base.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Web = typeof(Program).Assembly;

    [Fact]
    public void Domain_references_nothing_else_in_the_solution_or_any_framework() =>
        Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("Base.Application", "Base.Infrastructure", "Base.Web", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Microsoft.Extensions")
            .GetResult().FailingTypeNames.ShouldBeNull();

    [Fact]
    public void Application_does_not_know_about_infrastructure_or_the_web() =>
        Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("Base.Infrastructure", "Base.Web", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Amazon", "Azure", "Hangfire")
            .GetResult().FailingTypeNames.ShouldBeNull();

    [Fact]
    public void Infrastructure_does_not_know_about_the_web() =>
        Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOn("Base.Web").GetResult().FailingTypeNames.ShouldBeNull();

    [Fact]
    public void Controllers_never_touch_the_database_directly() =>
        Types.InAssembly(Web).That().ResideInNamespaceStartingWith("Base.Web.Controllers").ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Base.Infrastructure.Persistence")
            .GetResult().FailingTypeNames.ShouldBeNull();

    [Fact]
    public void Nothing_but_infrastructure_uses_storage_sdks() =>
        Types.InAssemblies([Domain, Application, Web]).ShouldNot()
            .HaveDependencyOnAny("Amazon.S3", "Azure.Storage", "MailKit")
            .GetResult().FailingTypeNames.ShouldBeNull();
}

public sealed class ConventionTests
{
    private static readonly Assembly Domain = typeof(EntityBase).Assembly;

    [Fact]
    public void Domain_entities_derive_from_the_single_entity_base()
    {
        var entities = Domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIdentifiable<>)));

        entities.Where(t => !IsEntityBase(t)).Select(t => t.FullName).ShouldBeEmpty();

        static bool IsEntityBase(Type? type)
        {
            for (; type is not null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EntityBase<>))
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public void Domain_events_are_immutable_records() =>
        Domain.GetTypes()
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsInterface: false })
            .Where(t => t.GetMethod("<Clone>$") is null || t.GetProperties().Any(p => p.SetMethod is { IsPublic: true } && !IsInitOnly(p)))
            .Select(t => t.FullName)
            .ShouldBeEmpty();

    [Fact]
    public void Interfaces_start_with_I() =>
        Types.InAssemblies([Domain, typeof(Base.Application.DependencyInjection).Assembly, typeof(Base.Infrastructure.DependencyInjection).Assembly])
            .That().AreInterfaces().Should().HaveNameStartingWith("I", StringComparison.Ordinal)
            .GetResult().FailingTypeNames.ShouldBeNull();

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;
}
