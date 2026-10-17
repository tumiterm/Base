using System.Text;

namespace Base.Application.Notifications;

/// <summary>Normalises phone numbers to E.164 (<c>+27821234567</c>). Local numbers starting with 0 get the default country code.</summary>
public static class PhoneNumbers
{
    public static string? ToE164(string? raw, string defaultCountryCode)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-' or '(' or ')' or '.' or '+'))
            {
                return null;
            }
        }

        var number = digits.ToString();
        if (raw.TrimStart().StartsWith('+'))
        {
            // already international
        }
        else if (number.StartsWith("00", StringComparison.Ordinal))
        {
            number = number[2..];
        }
        else if (number.StartsWith('0'))
        {
            number = defaultCountryCode.TrimStart('+') + number[1..];
        }

        return number.Length is >= 8 and <= 15 && number[0] != '0' ? "+" + number : null;
    }
}
