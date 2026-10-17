using System.Globalization;
using System.Reflection;

namespace Base.Application.Common.Text;

/// <summary>Turns caller data (a dictionary or an object's public properties) into <see cref="TemplateRenderer"/> values.</summary>
internal static class TemplateValues
{
    /// <summary>Invariant formatting; dates as ISO 8601.</summary>
    public static Dictionary<string, string?> From(object? data)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (data)
        {
            case null:
                break;
            case IEnumerable<KeyValuePair<string, string?>> strings:
                foreach (var (key, value) in strings)
                {
                    values[key] = value;
                }

                break;
            case IEnumerable<KeyValuePair<string, object?>> objects:
                foreach (var (key, value) in objects)
                {
                    values[key] = Format(value);
                }

                break;
            default:
                foreach (var property in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
                {
                    values[property.Name] = Format(property.GetValue(data));
                }

                break;
        }

        return values;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
