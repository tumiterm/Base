using Base.Application.Integrations;
using Base.Application.Security;

namespace Base.Web.ViewModels;

public sealed record ApiClientListViewModel(IReadOnlyList<ApiClientSummary> Clients, ApiClientForm NewClient, bool OpenForm = false);

public sealed record ApiClientDetailsViewModel(ApiClientSummary Client, ApiClientForm Form);

public sealed record ApiKeyViewModel(IssuedApiKey Key, string Name, bool Created);

public sealed class ApiClientForm
{
    public static readonly int[] LifetimeChoices = [30, 90, 180, 365];

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public IList<string> Permissions { get; set; } = [];

    public int ExpiresInDays { get; set; } = 90;

    public int? RequestsPerMinute { get; set; }

    /// <summary>Permissions the signed-in person may give (they hold them).</summary>
    public IReadOnlyList<PermissionDefinition> Grantable { get; set; } = [];

    public int MaxLifetimeDays { get; set; } = 365;

    public int DefaultRequestsPerMinute { get; set; }

    public static ApiClientForm From(ApiClientSummary client) => new()
    {
        Name = client.Name,
        Description = client.Description,
        Permissions = [.. client.Permissions],
        RequestsPerMinute = client.RequestsPerMinute,
    };

    public SaveApiClientRequest ToRequest() => new(Name, Description, [.. Permissions], ExpiresInDays, RequestsPerMinute);
}

public static class ApiClientDisplay
{
    public static (string Tone, string Label) State(ApiClientState state) => state switch
    {
        ApiClientState.Active => ("success", "Active"),
        ApiClientState.ExpiringSoon => ("warning", "Expires soon"),
        ApiClientState.Expired => ("danger", "Expired"),
        _ => ("secondary", "Revoked"),
    };
}
