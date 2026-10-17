using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

public sealed record NotificationPreferenceItem(string Category, NotificationChannel Channel, bool IsEnabled, bool IsLocked);

/// <summary>The signed-in user's opt-ins and opt-outs, per category and channel.</summary>
public interface INotificationPreferences
{
    Task<IReadOnlyList<NotificationPreferenceItem>> GetMineAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetMineAsync(string category, NotificationChannel channel, bool isEnabled, CancellationToken cancellationToken = default);
}

public sealed record TemplatePartView(string Part, string DefaultContent, string? OverrideContent)
{
    public string Effective => OverrideContent ?? DefaultContent;
}

public sealed record NotificationTemplateView(NotificationTemplateDefinition Definition, IReadOnlyList<TemplatePartView> Parts);

/// <summary>Lists templates and manages this tenant's overrides of their wording.</summary>
public interface INotificationTemplateAdmin
{
    Task<IReadOnlyList<NotificationTemplateView>> ListAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse<NotificationTemplateView>> GetAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Replaces one part for this tenant. Only the template's declared and built-in variables may be used.</summary>
    Task<ValidationResponse> SaveOverrideAsync(string code, string part, string content, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ResetOverrideAsync(string code, string part, CancellationToken cancellationToken = default);
}

internal sealed class NotificationPreferences(IUnitOfWork unitOfWork, ICurrentUser currentUser) : INotificationPreferences
{
    public async Task<IReadOnlyList<NotificationPreferenceItem>> GetMineAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var saved = await unitOfWork.Repository<NotificationPreference>().GetAllAsync(
            p => new { p.Category, p.Channel, p.IsEnabled }, p => p.UserId == userId, cancellationToken: cancellationToken);

        return ChannelsByCategory()
            .SelectMany(c => c.Channels.Select(channel => (c.Category, Channel: channel)))
            .Select(x => new NotificationPreferenceItem(
                x.Category,
                x.Channel,
                saved.FirstOrDefault(s => string.Equals(s.Category, x.Category, StringComparison.Ordinal) && s.Channel == x.Channel)?.IsEnabled ?? true,
                NotificationCatalog.IsMandatoryCategory(x.Category)))
            .ToList();
    }

    public async Task<ValidationResponse> SetMineAsync(string category, NotificationChannel channel, bool isEnabled, CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return ValidationResponse.Unauthorized();
        }

        if (!ChannelsByCategory().Any(c => string.Equals(c.Category, category, StringComparison.Ordinal) && c.Channels.Contains(channel)))
        {
            return ValidationResponse.NotFound($"There are no {category} notifications on {channel}.");
        }

        if (NotificationCatalog.IsMandatoryCategory(category))
        {
            return ValidationResponse.Conflict($"{category} notifications can't be switched off.");
        }

        var repository = unitOfWork.Repository<NotificationPreference>();
        var row = await repository.GetAsync(p => p.UserId == userId && p.Category == category && p.Channel == channel, cancellationToken: cancellationToken);
        if (row is null)
        {
            await repository.AddAsync(new NotificationPreference(userId, category, channel, isEnabled), cancellationToken);
        }
        else
        {
            row.Set(isEnabled);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Preference saved.");
    }

    private static IEnumerable<(string Category, List<NotificationChannel> Channels)> ChannelsByCategory() =>
        NotificationCatalog.All
            .GroupBy(t => t.Category, StringComparer.Ordinal)
            .Select(g => (g.Key, g.SelectMany(t => t.Channels.Concat(t.Fallback ?? [])).Distinct().Order().ToList()));
}

internal sealed class NotificationTemplateAdmin(IUnitOfWork unitOfWork) : INotificationTemplateAdmin
{
    public async Task<IReadOnlyList<NotificationTemplateView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var overrides = await unitOfWork.Repository<NotificationTemplateOverride>().GetAllAsync(
            o => new { o.TemplateCode, o.Part, o.Content }, cancellationToken: cancellationToken);
        return NotificationCatalog.All.Select(t => View(t, overrides.Where(o => string.Equals(o.TemplateCode, t.Code, StringComparison.Ordinal)).ToDictionary(o => o.Part, o => o.Content, StringComparer.Ordinal))).ToList();
    }

    public async Task<ValidationResponse<NotificationTemplateView>> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        var view = (await ListAsync(cancellationToken)).FirstOrDefault(v => string.Equals(v.Definition.Code, code, StringComparison.Ordinal));
        return view is null ? ValidationResponse.NotFound("Template not found.").As<NotificationTemplateView>() : view;
    }

    public async Task<ValidationResponse> SaveOverrideAsync(string code, string part, string content, CancellationToken cancellationToken = default)
    {
        var template = NotificationCatalog.Find(code);
        if (template is null || !NotificationTemplateFiles.PartsOf(code).ContainsKey(part))
        {
            return ValidationResponse.NotFound("Template part not found.");
        }

        content = (content ?? string.Empty).Trim();
        if (content.Length == 0 || content.Length > NotificationTemplateOverride.ContentMaxLength)
        {
            return ValidationResponse.Failure(new ValidationError("content", $"Enter between 1 and {NotificationTemplateOverride.ContentMaxLength} characters."));
        }

        var allowed = template.Variables.Concat(NotificationCatalog.BuiltInVariables).ToHashSet(StringComparer.Ordinal);
        var unknown = TemplateRenderer.VariablesIn(content).Where(v => !allowed.Contains(v)).ToList();
        if (unknown.Count > 0)
        {
            return ValidationResponse.Failure(new ValidationError("content",
                $"Unknown variable(s): {string.Join(", ", unknown)}. Available: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}."));
        }

        var repository = unitOfWork.Repository<NotificationTemplateOverride>();
        var row = await repository.GetAsync(o => o.TemplateCode == code && o.Part == part, cancellationToken: cancellationToken);
        if (row is null)
        {
            await repository.AddAsync(new NotificationTemplateOverride(code, part, content), cancellationToken);
        }
        else
        {
            row.Change(content);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Template saved.");
    }

    public async Task<ValidationResponse> ResetOverrideAsync(string code, string part, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.Repository<NotificationTemplateOverride>();
        var row = await repository.GetAsync(o => o.TemplateCode == code && o.Part == part, cancellationToken: cancellationToken);
        if (row is not null)
        {
            repository.HardDelete(row); // an override is configuration, not business data: removing it restores the default
            await unitOfWork.SaveAsync(cancellationToken);
        }

        return ValidationResponse.Success("Default restored.");
    }

    private static NotificationTemplateView View(NotificationTemplateDefinition template, Dictionary<string, string> overrides) =>
        new(template, NotificationTemplateFiles.PartsOf(template.Code)
            .OrderBy(p => TemplateParts.All.ToList().IndexOf(p.Key))
            .Select(p => new TemplatePartView(p.Key, p.Value, overrides.GetValueOrDefault(p.Key)))
            .ToList());
}
