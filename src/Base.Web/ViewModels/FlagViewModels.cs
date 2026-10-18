using Base.Application.Flags;
using Base.Application.Tenancy;
using Base.Domain.Flags;

namespace Base.Web.ViewModels;

/// <param name="Tenants">Organisations to choose from, by name (the first <see cref="Base.Application.Common.Persistence.PageRequest.MaxPageSize"/>).</param>
/// <param name="TenantTotal">How many organisations there are in all.</param>
public sealed record PlatformFlagsViewModel(IReadOnlyList<FeatureFlagView> Flags, IReadOnlyList<TenantSummary> Tenants, int TenantTotal);

/// <summary>One flag's form as posted.</summary>
public sealed class FlagForm
{
    public FeatureFlagMode Mode { get; set; }

    public int Percentage { get; set; }

    public IList<Guid> TenantIds { get; set; } = [];

    public bool IncludePlatform { get; set; }

    public FeatureFlagSetting ToSetting() => new(Mode, Percentage, [.. TenantIds], IncludePlatform);
}
