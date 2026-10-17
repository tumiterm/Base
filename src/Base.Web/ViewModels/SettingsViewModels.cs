using Base.Application.Notifications;
using Base.Application.Settings;

namespace Base.Web.ViewModels;

public sealed record SettingsCategoryView(string Name, int Count, int Changed);

/// <param name="Posted">Values from a form that failed validation, shown again instead of the stored ones.</param>
/// <param name="ProviderChoices">Provider instance names per provider setting (Notifications.EmailProvider, ...).</param>
public sealed record SettingsPageViewModel(
    IReadOnlyList<SettingsCategoryView> Categories,
    string Category,
    IReadOnlyList<SettingValue> Settings,
    string? Search,
    bool CanEdit,
    bool IsPlatform,
    IReadOnlyDictionary<string, string?> Posted,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProviderChoices,
    ChannelRoute? EmailRoute,
    string? TestAddress)
{
    public string? ValueOf(SettingValue setting) =>
        setting is not null && Posted.TryGetValue(setting.Definition.Code, out var posted) ? posted : setting?.Value;
}

/// <summary>One setting's editor row.</summary>
/// <param name="ShowScope">Mark platform-wide settings: only when the category mixes them with tenant ones.</param>
public sealed record SettingFieldViewModel(SettingValue Setting, string? Value, bool CanEdit, bool IsPlatform, IReadOnlyList<string>? Choices, bool ShowScope);
