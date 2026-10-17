using Base.Domain.MasterData;
using Base.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Base.Infrastructure.Persistence.Configurations;

internal sealed class LookupValueConfiguration : IEntityTypeConfiguration<LookupValue>
{
    public void Configure(EntityTypeBuilder<LookupValue> builder)
    {
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.ListCode).HasMaxLength(LookupValue.ListCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(v => v.Code).HasMaxLength(LookupValue.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(v => v.Name).HasMaxLength(LookupValue.NameMaxLength).IsRequired();
        builder.HasIndex(v => new { v.TenantId, v.ListCode, v.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("People");
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.FirstName).HasMaxLength(Person.NameMaxLength).IsRequired();
        builder.Property(p => p.LastName).HasMaxLength(Person.NameMaxLength).IsRequired();
        builder.Property(p => p.PreferredName).HasMaxLength(Person.NameMaxLength);
        builder.Property(p => p.TitleCode).HasMaxLength(Person.LookupCodeMaxLength).IsUnicode(false);
        builder.Property(p => p.Email).HasMaxLength(Person.EmailMaxLength);
        builder.Property(p => p.Phone).HasMaxLength(Person.PhoneMaxLength);
        builder.Property(p => p.DepartmentCode).HasMaxLength(Person.LookupCodeMaxLength).IsUnicode(false);
        builder.Property(p => p.JobTitle).HasMaxLength(Person.JobTitleMaxLength);
        builder.Property(p => p.Notes).HasMaxLength(Person.NotesMaxLength);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.PrimitiveCollection(p => p.Skills).HasMaxLength(-1).HasField("_skills");
        builder.Ignore(p => p.DisplayName);

        // The list sorts by name and filters by status and department; email is unique among the tenant's people.
        builder.HasIndex(p => new { p.TenantId, p.LastName, p.FirstName });
        builder.HasIndex(p => new { p.TenantId, p.Status, p.DepartmentCode });
        builder.HasIndex(p => new { p.TenantId, p.Email }).IsUnique().HasFilter("[IsDeleted] = 0 AND [Email] IS NOT NULL");
    }
}
