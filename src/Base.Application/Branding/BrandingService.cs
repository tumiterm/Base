using Base.Application.Common.Results;
using Base.Application.Files;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Files;

namespace Base.Application.Branding;

/// <summary>A tenant's look. <see cref="Version"/> changes whenever any of it does, for cache-busting the theme stylesheet.</summary>
public sealed record BrandingTheme(string ApplicationName, string PrimaryColor, string DefaultTheme, Guid? LogoFileId)
{
    public string Version => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{PrimaryColor}|{DefaultTheme}|{LogoFileId}")))[..12];
}

public sealed record BrandingLogo(Stream Content, string ContentType);

/// <summary>
/// Per-tenant appearance: accent colour, default theme and logo. Colours are settings (validated, cached, audited);
/// the logo is a stored file in the <c>branding</c> category (point it at the Database provider to keep it with the data).
/// </summary>
public interface IBrandingService
{
    Task<BrandingTheme> GetAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse> SaveAsync(string primaryColor, string defaultTheme, CancellationToken cancellationToken = default);

    Task<ValidationResponse> SetLogoAsync(Stream content, string fileName, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RemoveLogoAsync(CancellationToken cancellationToken = default);

    /// <summary>The logo bytes for the current tenant, or null. Public: the sign-in page shows it.</summary>
    Task<BrandingLogo?> OpenLogoAsync(CancellationToken cancellationToken = default);
}

internal sealed class BrandingService(
    IApplicationSettingService settings,
    FileService files,
    Common.Persistence.IUnitOfWork unitOfWork,
    ITenantEntitlements entitlements) : IBrandingService
{
    internal const string Category = "branding";
    private const long MaxLogoBytes = 512 * 1024;
    private static readonly string[] LogoExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    public async Task<BrandingTheme> GetAsync(CancellationToken cancellationToken = default) =>
        await entitlements.HasFeatureAsync(FeatureCodes.CustomBranding, cancellationToken)
            ? await LoadAsync(cancellationToken)
            : new BrandingTheme(
                await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? "Base",
                SettingCatalog.Get(SettingCodes.Branding.PrimaryColor).DefaultValue!,
                SettingCatalog.Get(SettingCodes.Branding.DefaultTheme).DefaultValue!,
                null);

    private async Task<ValidationResponse?> NotIncludedAsync(CancellationToken cancellationToken) =>
        await entitlements.HasFeatureAsync(FeatureCodes.CustomBranding, cancellationToken)
            ? null
            : ValidationResponse.Forbidden("Your plan doesn't include custom branding.");

    private async Task<BrandingTheme> LoadAsync(CancellationToken cancellationToken) => new(
        await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? "Base",
        await settings.GetAsync(SettingCodes.Branding.PrimaryColor, cancellationToken) ?? "#4f5bd5",
        await settings.GetAsync(SettingCodes.Branding.DefaultTheme, cancellationToken) ?? "system",
        Guid.TryParse(await settings.GetAsync(SettingCodes.Branding.LogoFileId, cancellationToken), out var logo) ? logo : null);

    public async Task<ValidationResponse> SaveAsync(string primaryColor, string defaultTheme, CancellationToken cancellationToken = default)
    {
        if (await NotIncludedAsync(cancellationToken) is { } notIncluded)
        {
            return notIncluded;
        }

        var color = await settings.SetAsync(SettingCodes.Branding.PrimaryColor, primaryColor?.Trim().ToLowerInvariant(), cancellationToken);
        if (!color.IsSuccess)
        {
            return color;
        }

        var theme = await settings.SetAsync(SettingCodes.Branding.DefaultTheme, defaultTheme, cancellationToken);
        return theme.IsSuccess ? ValidationResponse.Success("Appearance saved.") : theme;
    }

    public async Task<ValidationResponse> SetLogoAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        if (await NotIncludedAsync(cancellationToken) is { } notIncluded)
        {
            return notIncluded;
        }

        // SVG is refused on purpose: it can carry script, and the logo is served on the sign-in page.
        var staged = await files.StageAsync(new FileUploadRequest(content, fileName, null, Category), null, LogoExtensions, replaceAllowList: true, cancellationToken, MaxLogoBytes);
        if (!staged.IsSuccess)
        {
            return staged;
        }

        await staged.Data!.SaveOrDiscardAsync(unitOfWork, cancellationToken);
        var previous = await settings.GetAsync(SettingCodes.Branding.LogoFileId, cancellationToken);
        var saved = await settings.SetAsync(SettingCodes.Branding.LogoFileId, staged.Data.File.Id.ToString(), cancellationToken);
        if (saved.IsSuccess)
        {
            await DeleteFileAsync(previous, cancellationToken);
        }

        return saved.IsSuccess ? ValidationResponse.Success("Logo updated.") : saved;
    }

    public async Task<ValidationResponse> RemoveLogoAsync(CancellationToken cancellationToken = default)
    {
        var previous = await settings.GetAsync(SettingCodes.Branding.LogoFileId, cancellationToken);
        var result = await settings.SetAsync(SettingCodes.Branding.LogoFileId, null, cancellationToken);
        if (result.IsSuccess)
        {
            await DeleteFileAsync(previous, cancellationToken);
        }

        return result.IsSuccess ? ValidationResponse.Success("Logo removed.") : result;
    }

    public async Task<BrandingLogo?> OpenLogoAsync(CancellationToken cancellationToken = default)
    {
        var theme = await GetAsync(cancellationToken);
        if (theme.LogoFileId is not { } id)
        {
            return null;
        }

        var file = await unitOfWork.Repository<StoredFile>().GetAsync(f => f.Id == id && f.Category == Category, asNoTracking: true, cancellationToken: cancellationToken);
        return file is null ? null : new BrandingLogo(await files.OpenReadAsync(file, cancellationToken), file.ContentType);
    }

    private async Task DeleteFileAsync(string? fileId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(fileId, out var id))
        {
            return;
        }

        var repository = unitOfWork.Repository<StoredFile>();
        if (await repository.GetAsync(f => f.Id == id && f.Category == Category, cancellationToken: cancellationToken) is { } file)
        {
            repository.Remove(file);
            await unitOfWork.SaveAsync(cancellationToken);
        }
    }
}
