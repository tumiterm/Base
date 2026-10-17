# 0013: Exports and imports through registered exporters and importers

**Context.** Every business app is asked to "export this list to Excel" and "load these 2,000 rows from a spreadsheet".
Done ad hoc, exports leak data with nobody recording it, CSVs become formula-injection vectors, imports half-apply
and leave no trail, and background imports run as "System", skipping the permission checks a person would face.

**Decision.**
- **Exporters.** An export is an `IExporter` (`Exporter<TRow>`): columns, a permission, and pages from an existing
  use case, so that use case's access rules apply. `IExportService` writes CSV or Excel.
  - Exports up to `Exports.MaxImmediateRows` rows download at once; larger ones (up to `Exports.MaxRows`) run in the
    background. They are stored as a file only the requester can download, and the requester is notified in-app.
  - Every export is recorded as an `ExportJob` with its filter, so it is audited.
- **Importers.** An import is an `IImporter`: typed columns with aliases, row checks, and an apply step that calls
  existing use cases. `IImportService` runs it as upload → dry run (nothing saved) → confirm → apply in the background.
  - Results are kept per row, and the user can download an error file with an extra Errors column.
  - **All-or-nothing by default:** nothing is applied unless every row is valid, and rows are checked again just
    before applying. An importer can opt into partial imports instead.
- **Background work runs as the requester.** `IRunAsUser` rebuilds the requester's principal with the sign-in claims
  factory and swaps it into `ICurrentUser` for that scope. Their permissions as they are now apply, `PermissionGuard`
  included, and audit stamps name them. Work is refused if the user is gone or locked out.
- **Formats.**
  - CSV is written and read by hand: RFC 4180, UTF-8 with a BOM, and cells that Excel would run as formulas are
    neutralised (OWASP).
  - Excel uses `DocumentFormat.OpenXml` (Microsoft, MIT) with its streaming writer and reader.
  - ClosedXML was rejected because its dependency `SixLabors.Fonts` allows 2.x versions, which are under the
    commercial Six Labors Split License. An update could pull that in without anyone noticing.

**Consequences.** A new export or import is one class and a DI line. Excel files are plain: one sheet, typed cells,
a frozen header and drop-downs, with no charts or formulas. Re-applying a crashed import is not automatic: a job left
`Running` must be uploaded again. The OpenXML SDK's async writer drops child elements in 3.5.1, so the sync writer is
used, into a local temp file.
