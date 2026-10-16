using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using Base.Domain.Auditing;
using Base.Domain.Common;
using Base.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Base.Infrastructure.Auditing;

/// <summary>
/// Decides what is audited and how it is named. Audited: every <see cref="IAuditable"/> entity (all
/// <see cref="EntityBase"/> types and users) plus roles, role assignments, permission grants and external logins.
/// Opt out with <see cref="NotAuditedAttribute"/>; hide values with <see cref="AuditRedactedAttribute"/>.
/// </summary>
internal static class AuditPolicy
{
    private static readonly FrozenDictionary<Type, string> IdentityTypes = new Dictionary<Type, string>
    {
        [typeof(ApplicationUser)] = "User",
        [typeof(ApplicationRole)] = "Role",
        [typeof(IdentityUserRole<Guid>)] = "UserRole",
        [typeof(IdentityRoleClaim<Guid>)] = "RoleClaim",
        [typeof(IdentityUserClaim<Guid>)] = "UserClaim",
        [typeof(IdentityUserLogin<Guid>)] = "UserLogin",
    }.ToFrozenDictionary();

    /// <summary>Bookkeeping columns: already on the audit record itself, or pure noise (sign-in counters).</summary>
    private static readonly FrozenSet<string> IgnoredProperties = new[]
    {
        nameof(IHasRowVersion.RowVersion),
        nameof(IAuditable.DateCreated), nameof(IAuditable.DateModified), nameof(IAuditable.UserCreated), nameof(IAuditable.UserModified),
        nameof(ISoftDeletable.IsDeleted), nameof(ISoftDeletable.DateDeleted), nameof(ISoftDeletable.UserDeleted),
        nameof(ApplicationUser.ConcurrencyStamp), nameof(ApplicationUser.LastSignInAt), nameof(ApplicationUser.AccessFailedCount),
        nameof(ApplicationUser.NormalizedUserName), nameof(ApplicationUser.NormalizedEmail), nameof(ApplicationRole.NormalizedName),
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Secrets on framework types we can't put attributes on.</summary>
    private static readonly FrozenSet<string> RedactedProperties = new[]
    {
        nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp),
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<Type, bool> AuditedCache = new();
    private static readonly ConcurrentDictionary<PropertyInfo, PropertyRule> PropertyCache = new();

    public enum PropertyRule
    {
        Record = 0,
        Redact = 1,
        Ignore = 2,
    }

    public static bool IsAudited(Type type) =>
        AuditedCache.GetOrAdd(type, static t =>
            !Attribute.IsDefined(t, typeof(NotAuditedAttribute))
            && (IdentityTypes.ContainsKey(t) || typeof(IAuditable).IsAssignableFrom(t)));

    public static string NameOf(Type type) => IdentityTypes.GetValueOrDefault(type) ?? type.Name;

    public static IEnumerable<string> AuditedNames(IModel model) =>
        model.GetEntityTypes().Select(e => e.ClrType).Where(IsAudited).Select(NameOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>
    /// The key as text. Claims are recorded against their role or user (their own id is database-generated and
    /// unknown before the insert), so a role's or user's history includes its claims.
    /// </summary>
    public static string EntityIdOf(EntityEntry entry) => entry.Entity switch
    {
        IdentityRoleClaim<Guid> claim => claim.RoleId.ToString(),
        IdentityUserClaim<Guid> claim => claim.UserId.ToString(),
        _ => string.Join('|', entry.Metadata.FindPrimaryKey()!.Properties.Select(p => Format(entry.Property(p.Name).CurrentValue))),
    };

    public static PropertyRule RuleFor(IProperty property)
    {
        if (property.IsPrimaryKey() || property.IsShadowProperty() || property.PropertyInfo is null)
        {
            return property.IsPrimaryKey() ? PropertyRule.Ignore : PropertyRule.Record;
        }

        return PropertyCache.GetOrAdd(property.PropertyInfo, static info =>
            IgnoredProperties.Contains(info.Name) || Attribute.IsDefined(info, typeof(NotAuditedAttribute)) ? PropertyRule.Ignore
            : RedactedProperties.Contains(info.Name) || Attribute.IsDefined(info, typeof(AuditRedactedAttribute)) ? PropertyRule.Redact
            : PropertyRule.Record);
    }

    public const int MaxValueLength = 500;

    public static string? Format(object? value)
    {
        var text = value switch
        {
            null => null,
            DateTimeOffset dto => dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            byte[] bytes => $"({bytes.Length} bytes)",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        return text is { Length: > MaxValueLength } ? text[..MaxValueLength] + "…" : text;
    }
}
