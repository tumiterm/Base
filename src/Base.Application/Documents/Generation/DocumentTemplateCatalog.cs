using System.Collections.Frozen;

namespace Base.Application.Documents.Generation;

/// <summary>Output formats. A format is available when an <see cref="IDocumentRenderer"/> for it is registered.</summary>
public static class DocumentFormats
{
    /// <summary>Built in: the filled-in template itself.</summary>
    public const string Html = "html";

    /// <summary>Needs a PDF renderer: Gotenberg (configure <c>Documents:Pdf:Gotenberg</c>) or your own.</summary>
    public const string Pdf = "pdf";
}

/// <summary>Template codes. Adding a template = a constant here + a catalogue entry + <c>Documents/Templates/{Code}.html</c>.</summary>
public static class DocumentTemplates
{
    public const string Letter = "General.Letter";

    /// <summary>The invoice for a paid subscription payment; filled from the frozen <c>Invoice</c>, not typed in.</summary>
    public const string Invoice = "Billing.Invoice";
}

/// <summary>
/// A document the application can produce. The HTML file uses <c>{{ Variable }}</c> placeholders (values are
/// HTML-encoded); <see cref="Variables"/> must be supplied by the caller, the built-ins are filled in automatically.
/// </summary>
/// <param name="IsSystem">Produced by the application itself (an invoice), never offered for people to fill in.</param>
public sealed record DocumentTemplateDefinition(string Code, string Name, string Description, string DocumentType, IReadOnlyList<string> Variables, bool IsSystem = false);

public static class DocumentTemplateCatalog
{
    public const string AppNameVariable = "AppName";

    /// <summary>Today's date in the <c>General.DisplayTimeZone</c>, e.g. "17 October 2026".</summary>
    public const string TodayVariable = "Today";

    public static IReadOnlyList<string> BuiltInVariables { get; } = [AppNameVariable, TodayVariable];

    public static IReadOnlyList<DocumentTemplateDefinition> All { get; } =
    [
        new(DocumentTemplates.Letter, "Letter", "A one-page letter with a subject and body.", DocumentTypes.Letter, ["RecipientName", "Subject", "Body"]),
        new(DocumentTemplates.Invoice, "Invoice", "The invoice for a paid subscription payment.", DocumentTypes.General,
            ["InvoiceTitle", "InvoiceNumber", "IssuedOn", "SellerName", "SellerAddress", "VatLine", "BuyerName", "BuyerEmail", "BuyerContact", "BuyerAddress", "BuyerPhone",
             "BuyerNumbers", "SellerLogo", "SellerLogoClass", "BuyerLogo", "BuyerLogoClass", "PaidOn", "PaidWith",
             "PaymentReference", "Description", "SubtotalLabel", "Subtotal", "VatLabel", "VatAmount", "Total", "Footer"], IsSystem: true),
    ];

    private static readonly FrozenDictionary<string, DocumentTemplateDefinition> ByCode =
        All.ToFrozenDictionary(t => t.Code, StringComparer.Ordinal);

    public static DocumentTemplateDefinition? Find(string? code) => code is null ? null : ByCode.GetValueOrDefault(code);

    /// <summary>The templates people may fill in and generate themselves.</summary>
    public static IReadOnlyList<DocumentTemplateDefinition> ForPeople { get; } = All.Where(t => !t.IsSystem).ToList();
}
