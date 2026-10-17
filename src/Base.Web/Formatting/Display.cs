using System.Globalization;
using Base.Application.Subscriptions;
using Base.Domain.Subscriptions;

namespace Base.Web.Formatting;

/// <summary>Consistent display of money, statuses and limits across views.</summary>
public static class Display
{
    public static string Money(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{currency} {amount:#,0.##}");

    public static string PerCycle(BillingCycle cycle) => cycle == BillingCycle.Annual ? "/year" : "/month";

    /// <summary>The badge colour for a status (Bootstrap's text-bg-* names, restyled by the design system).</summary>
    public static string Tone(SubscriptionStatus? status) => status switch
    {
        SubscriptionStatus.Trialing => "info",
        SubscriptionStatus.Active => "success",
        SubscriptionStatus.PastDue => "warning",
        SubscriptionStatus.Suspended => "danger",
        null => "light",
        _ => "secondary",
    };

    public static string Status(SubscriptionStatus? status) => status is { } s ? SubscriptionStatusText.Describe(s) : "No plan";

    public static string Limit(string code, long? value) => EntitlementCatalog.Get(code).Format(value);

    /// <summary>Two letters for an avatar: initials of the first two words.</summary>
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0])),
        };
    }
}
