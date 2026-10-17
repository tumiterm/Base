using System.Globalization;
using Base.Application.Common.Results;

namespace Base.Application.Settings;

public enum SettingDataType
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    TimeZone = 4,
    StringList = 5,
    Url = 6,

    /// <summary>A <c>#rrggbb</c> colour.</summary>
    Color = 7,

    /// <summary>An email address.</summary>
    Email = 8,

    /// <summary>
    /// A credential an operator enters on the settings screen (an SMTP password, ...). Stored encrypted, never shown
    /// again, never in the audit trail. Platform scope only. Keys and tokens a deployment owns stay in configuration.
    /// </summary>
    Secret = 9,
}

public enum SettingScope
{
    /// <summary>One value for the whole installation.</summary>
    Platform = 0,

    /// <summary>Each tenant may override the platform value.</summary>
    Tenant = 1,
}

/// <summary>
/// Everything about a setting except its stored value. Deployment secrets (connection strings, signing keys, API
/// keys) stay in configuration (user-secrets locally, environment / Key Vault in production); the only secrets that
/// are settings are <see cref="SettingDataType.Secret"/> credentials an operator types in, stored encrypted.
/// </summary>
public sealed partial record SettingDefinition(
    string Code,
    string Name,
    SettingDataType DataType,
    string? DefaultValue,
    string Category,
    string Description,
    SettingScope Scope = SettingScope.Tenant,
    IReadOnlyList<string>? AllowedValues = null,
    decimal? Min = null,
    decimal? Max = null,
    bool IsRequired = true)
{
    public const int SecretMaxLength = 1000;

    public bool IsSecret => DataType == SettingDataType.Secret;

    public ValidationResponse Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return IsRequired ? Fail("A value is required.") : ValidationResponse.Success();
        }

        if (AllowedValues is { Count: > 0 } && !AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return Fail($"Must be one of: {string.Join(", ", AllowedValues)}.");
        }

        return DataType switch
        {
            SettingDataType.Integer => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? CheckRange(number)
                : Fail("Must be a whole number."),
            SettingDataType.Decimal => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? CheckRange(number)
                : Fail("Must be a number."),
            SettingDataType.Boolean => bool.TryParse(value, out _) ? ValidationResponse.Success() : Fail("Must be true or false."),
            SettingDataType.TimeZone => TimeZoneInfo.TryFindSystemTimeZoneById(value, out _)
                ? ValidationResponse.Success()
                : Fail("Must be a known time zone id, e.g. Africa/Johannesburg."),
            SettingDataType.Url => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
                    && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                ? ValidationResponse.Success()
                : Fail("Must be an absolute http(s) URL without query string, e.g. https://portal.example.com."),
            SettingDataType.Color => HexColor().IsMatch(value)
                ? ValidationResponse.Success()
                : Fail("Must be a colour like #4f5bd5."),
            SettingDataType.Email => EmailAddress().IsMatch(value)
                ? ValidationResponse.Success()
                : Fail("Must be an email address, e.g. noreply@example.com."),
            SettingDataType.Secret => value.Length <= SecretMaxLength
                ? ValidationResponse.Success()
                : Fail($"Must be at most {SecretMaxLength} characters."),
            _ => ValidationResponse.Success(),
        };
    }

    private ValidationResponse CheckRange(decimal number)
    {
        if (Min is { } min && number < min)
        {
            return Fail($"Must be at least {min.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (Max is { } max && number > max)
        {
            return Fail($"Must be at most {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        return ValidationResponse.Success();
    }

    private ValidationResponse Fail(string message) => ValidationResponse.Failure(new ValidationError(Code, message, "setting.invalid"));

    [System.Text.RegularExpressions.GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex EmailAddress();

    [System.Text.RegularExpressions.GeneratedRegex("^#[0-9a-fA-F]{6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial System.Text.RegularExpressions.Regex HexColor();
}
