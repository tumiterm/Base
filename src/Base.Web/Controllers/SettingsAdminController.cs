using Base.Application.Common.Abstractions;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Domain.Notifications;
using Base.Web.Formatting;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>
/// Every catalogued setting, by category, with typed editors. Tenants see their own (tenant-scope) settings; the
/// platform tenant also sees and edits the installation-wide ones (email delivery, storage, limits...).
/// </summary>
[Route("admin/settings")]
[HasPermission(PermissionCodes.Settings.View)]
public sealed class SettingsAdminController(
    IApplicationSettingService settings,
    INotificationChannels channels,
    IEmailDeliveryTest emailTest,
    ICurrentUser currentUser) : Controller
{
    /// <summary>Form fields are named <c>setting:{code}</c>. A blank secret keeps the stored one; Revert removes it.</summary>
    private const string FieldPrefix = "setting:";

    [HttpGet("")]
    public Task<IActionResult> Index(string? category, string? q, CancellationToken cancellationToken) =>
        PageAsync(category, q, new Dictionary<string, string?>(StringComparer.Ordinal), null, cancellationToken);

    [HttpPost("")]
    [HasPermission(PermissionCodes.Settings.Edit)]
    public async Task<IActionResult> Save(string category, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(cancellationToken);
        var editable = visible.Where(s => string.Equals(s.Definition.Category, category, StringComparison.Ordinal)
            && SettingsDisplay.ManagedOn(s.Definition.Category) is null).ToList();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var setting in editable)
        {
            var code = setting.Definition.Code;
            if (!Request.Form.TryGetValue(FieldPrefix + code, out var posted))
            {
                continue;
            }

            // Checkboxes post a hidden "false" first, then "true" when ticked: the last value wins.
            var value = posted.Count > 0 ? posted[^1] : null;
            if (!setting.Definition.IsSecret || !string.IsNullOrEmpty(value))
            {
                values[code] = value;
            }
        }

        var result = await settings.SetManyAsync(values, cancellationToken);
        if (!result.IsSuccess)
        {
            result.AddToModelState(ModelState);
            var posted = values.Where(v => !SettingCatalog.Get(v.Key).IsSecret).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            return await PageAsync(category, null, posted, null, cancellationToken);
        }

        this.Flash(result);
        return RedirectToAction(nameof(Index), new { category });
    }

    [HttpPost("revert")]
    [HasPermission(PermissionCodes.Settings.Edit)]
    public async Task<IActionResult> Revert(string code, string? category, CancellationToken cancellationToken)
    {
        this.Flash(await settings.RevertAsync(code, cancellationToken));
        return RedirectToAction(nameof(Index), new { category = category ?? SettingCatalog.Find(code)?.Category });
    }

    [HttpPost("test-email")]
    [HasPermission(PermissionCodes.Settings.Edit)]
    public async Task<IActionResult> TestEmail(string to, CancellationToken cancellationToken)
    {
        var result = await emailTest.SendAsync(to, cancellationToken);
        if (!result.IsSuccess && result.Errors.Count > 0)
        {
            result.AddToModelState(ModelState);
            return await PageAsync("Email", null, new Dictionary<string, string?>(StringComparer.Ordinal), to, cancellationToken);
        }

        this.Flash(result);
        return RedirectToAction(nameof(Index), new { category = "Email" });
    }

    private async Task<IActionResult> PageAsync(string? category, string? search, IReadOnlyDictionary<string, string?> posted, string? testAddress, CancellationToken cancellationToken)
    {
        var visible = await VisibleAsync(cancellationToken);
        var categories = visible
            .GroupBy(s => s.Definition.Category, StringComparer.Ordinal)
            .OrderBy(g => SettingsDisplay.Order(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SettingsCategoryView(g.Key, g.Count(), g.Count(s => s.IsChanged)))
            .ToList();

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var selected = term is null && categories.Exists(c => string.Equals(c.Name, category, StringComparison.Ordinal)) ? category! : categories[0].Name;
        var shown = term is null
            ? visible.Where(s => string.Equals(s.Definition.Category, selected, StringComparison.Ordinal)).ToList()
            : visible.Where(s => s.Definition.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.Definition.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.Definition.Code.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        var choices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [SettingCodes.Notifications.EmailProvider] = channels.ProviderNames(NotificationChannel.Email),
            [SettingCodes.Notifications.SmsProvider] = channels.ProviderNames(NotificationChannel.Sms),
            [SettingCodes.Notifications.WhatsAppProvider] = channels.ProviderNames(NotificationChannel.WhatsApp),
        };

        var showsEmail = term is null && string.Equals(selected, "Email", StringComparison.Ordinal);
        return View("Index", new SettingsPageViewModel(
            categories,
            selected,
            shown,
            term,
            currentUser.HasPermission(PermissionCodes.Settings.Edit),
            await settings.CanEditPlatformAsync(cancellationToken),
            posted,
            choices,
            showsEmail ? await channels.DescribeAsync(NotificationChannel.Email, cancellationToken) : null,
            testAddress ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value));
    }

    /// <summary>Platform settings are shown only to the platform tenant, which is the only one that may change them.</summary>
    private async Task<List<SettingValue>> VisibleAsync(CancellationToken cancellationToken)
    {
        var all = await settings.GetAllAsync(cancellationToken);
        var isPlatform = await settings.CanEditPlatformAsync(cancellationToken);
        return all.Where(s => isPlatform || s.Definition.Scope == SettingScope.Tenant).ToList();
    }
}
