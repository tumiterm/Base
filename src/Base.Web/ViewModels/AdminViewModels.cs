using System.ComponentModel.DataAnnotations;
using Base.Application.Common.Persistence;
using Base.Application.Security;
using Base.Application.Users;

namespace Base.Web.ViewModels;

public sealed record UserListViewModel(PagedResult<UserSummary> Users, string? Search);

public sealed class InviteUserViewModel
{
    [Required(ErrorMessage = "Enter the email address the invitation goes to.")]
    [EmailAddress(ErrorMessage = "Enter an email address like name@example.com.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter their full name.")]
    [Display(Name = "Full name")]
    public string DisplayName { get; set; } = string.Empty;

    public IList<string> Roles { get; set; } = [];

    public IReadOnlyList<RoleSummary> AvailableRoles { get; set; } = [];
}

public sealed class EditUserViewModel
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter their full name.")]
    [Display(Name = "Full name")]
    public string DisplayName { get; set; } = string.Empty;

    public IList<string> Roles { get; set; } = [];

    public bool IsLockedOut { get; set; }

    public bool HasPassword { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }

    public IReadOnlyList<RoleSummary> AvailableRoles { get; set; } = [];
}

public sealed class EditRoleViewModel
{
    public Guid? Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public IList<string> Permissions { get; set; } = [];

    public bool IsSystem { get; set; }

    public bool IsAdministrator => string.Equals(Name, RoleNames.Administrator, StringComparison.Ordinal) && IsSystem;

    public static IEnumerable<IGrouping<string, PermissionDefinition>> Modules => PermissionCatalog.All.GroupBy(p => p.Module, StringComparer.Ordinal);
}

public sealed class ForgotPasswordViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

public sealed class SetPasswordViewModel
{
    public Guid UserId { get; set; }

    public string Token { get; set; } = string.Empty;

    public PasswordTokenKind Kind { get; set; }

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class AuditFilterViewModel
{
    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>UTC calendar day.</summary>
    public DateOnly? From { get; set; }

    /// <summary>UTC calendar day, inclusive.</summary>
    public DateOnly? To { get; set; }

    public int Page { get; set; } = 1;
}

public sealed record AuditListViewModel(
    PagedResult<Application.Auditing.AuditRecord> Entries,
    AuditFilterViewModel Filter,
    IReadOnlyList<string> EntityTypes,
    IReadOnlyDictionary<Application.Common.Records.RecordRef, Application.Common.Records.ResolvedRecord> Records)
{
    /// <summary>The record an entry is about (a claim's user or role), named for display.</summary>
    public Application.Common.Records.ResolvedRecord? RecordOf(Application.Auditing.AuditRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (type, id) = Infrastructure.Auditing.AuditRecordOwners.OwnerOf(entry.EntityType, entry.EntityId);
        return Records.GetValueOrDefault(new Application.Common.Records.RecordRef(type, id));
    }

    public Application.Common.Records.ResolvedRecord? Actor =>
        Filter.UserId is { } id ? Records.GetValueOrDefault(new Application.Common.Records.RecordRef("User", id.ToString())) : null;

    public Application.Common.Records.ResolvedRecord? Filtered =>
        Filter.EntityType is { } type && Filter.EntityId is { } id ? Records.GetValueOrDefault(new Application.Common.Records.RecordRef(type, id)) : null;
}

public sealed class NotificationLogFilterViewModel
{
    public string? Search { get; set; }

    public Domain.Notifications.NotificationChannel? Channel { get; set; }

    public Domain.Notifications.NotificationStatus? Status { get; set; }

    public string? Template { get; set; }

    public int Page { get; set; } = 1;
}

public sealed record NotificationLogViewModel(PagedResult<Application.Notifications.NotificationLogItem> Messages, NotificationLogFilterViewModel Filter);
