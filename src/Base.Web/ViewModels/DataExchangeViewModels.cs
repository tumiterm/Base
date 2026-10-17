using Base.Application.Common.Persistence;
using Base.Application.DataExchange;

namespace Base.Web.ViewModels;

public sealed record ImportsViewModel(IReadOnlyList<ImporterInfo> Importers, PagedResult<ImportJobView> History);

/// <summary>For the <c>_ExportButtons</c> partial: which exporter, and whether this user may run it.</summary>
public sealed record ExportButtonsViewModel(string Code, string Permission);
