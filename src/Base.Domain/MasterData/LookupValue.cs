using Base.Domain.Common;

namespace Base.Domain.MasterData;

/// <summary>
/// One entry in a tenant's lookup list (titles, departments, ...). Records store the <see cref="Code"/>, so renaming an
/// entry relabels every record that uses it. Entries are deactivated, not deleted, so old records keep their value.
/// Lists and their starting values come from the application's lookup catalogue.
/// </summary>
public sealed class LookupValue : EntityBase, ITenantOwned
{
    public const int ListCodeMaxLength = 100;
    public const int CodeMaxLength = 50;
    public const int NameMaxLength = 100;

    private LookupValue()
    {
    }

    public LookupValue(string listCode, string code, string name, int sortOrder, bool isSeeded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Id = SequentialGuid.Next();
        ListCode = listCode;
        Code = code.Trim().ToLowerInvariant();
        SortOrder = sortOrder;
        IsActive = true;
        IsSeeded = isSeeded;
        Rename(name);
    }

    public Guid TenantId { get; set; }

    public string ListCode { get; private set; } = string.Empty;

    /// <summary>What records store. Never changes.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    /// <summary>Inactive entries aren't offered for new choices but still label existing records.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Came from the catalogue's defaults (code may be referenced by application code).</summary>
    public bool IsSeeded { get; private set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"A name has at most {NameMaxLength} characters.", nameof(name));
        }

        Name = trimmed;
    }

    public void MoveTo(int sortOrder) => SortOrder = sortOrder;

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}
