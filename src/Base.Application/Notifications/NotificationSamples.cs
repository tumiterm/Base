using System.Collections.Frozen;

namespace Base.Application.Notifications;

/// <summary>
/// Made-up values for every placeholder, so a template can be previewed and sent as a test before anything real
/// happens. A new placeholder gets a sample here (a test checks every template's placeholders have one).
/// </summary>
public static class NotificationSamples
{
    private static readonly FrozenDictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Link"] = "https://example.com/account/set-password?sample",
        ["OrganisationName"] = "Acme Trading",
        ["SignInUrl"] = "https://acme.example.com/account/login",
        ["PlanLine"] = "You're on the Professional plan, with a 14-day trial.",
        ["Title"] = "Team meeting on Friday",
        ["Message"] = "We meet at 10:00 in the boardroom. Bring your quarterly figures.",
        ["Export"] = "People",
        ["Import"] = "People",
        ["Outcome"] = "1,204 rows, ready to download.",
        ["ClientName"] = "Payroll sync",
        ["ExpiresOn"] = "7 November 2026",
        ["PlanName"] = "Professional",
        ["PreviousPlanName"] = "Starter",
        ["EndsOn"] = "7 November 2026",
        ["Status"] = "past due",
        ["Reason"] = "the last payment didn't go through",
        ["Amount"] = "ZAR 999.00",
        ["Price"] = "ZAR 999.00 a month",
        ["When"] = "from 24 November 2026",
        ["Reference"] = "P7K2M9QX4T3B",
        ["PaidUntil"] = "24 November 2026",
        ["Gateway"] = "Payfast",
        ["InvoiceTitle"] = "Tax invoice",
        ["InvoiceNumber"] = "INV-000123",
        ["IssuedOn"] = "24 October 2026",
        ["SellerName"] = "Base Platform (Pty) Ltd",
        ["BuyerName"] = "Acme Trading (Pty) Ltd",
        ["Description"] = "Professional, 1 month",
        ["Total"] = "ZAR 999.00",
        ["VatLabel"] = "VAT (15%)",
        ["VatAmount"] = "ZAR 130.30",
        ["PaidOn"] = "24 October 2026",
        ["PaymentReference"] = "P7K2M9QX4T3B",
        ["InvoicePath"] = "/subscription/invoices/INV-000123",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool Has(string variable) => Values.ContainsKey(variable);

    /// <summary>A sample for each of the template's placeholders (built-ins are filled by the caller).</summary>
    public static IDictionary<string, string?> For(NotificationTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Variables.ToDictionary(v => v, v => (string?)Values.GetValueOrDefault(v, "[" + v + "]"), StringComparer.Ordinal);
    }
}
