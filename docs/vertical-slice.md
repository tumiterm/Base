# Adding a module: copy the People sample

People is the base's worked example of a business module. Every file below exists for a reason; a new module
(Invoices, Vehicles, Cases...) copies the same shape, renames, and deletes what it doesn't need. The UI must meet
[ui-design-direction.md](ui-design-direction.md).

## The files, layer by layer

| Layer | File | What it shows |
|---|---|---|
| Domain | `Base.Domain/People/Person.cs` | `EntityBase` + `ITenantOwned`; private setters and intent-named methods (`ChangeRole`, `ChangeStatus`) that enforce rules; lookup **codes**, not names; `[AuditRedacted]` on personal data; a primitive collection (`Skills`). |
| Application | `People/PersonContracts.cs` | Query, summary, details and save records; a sort enum; counts for the filter. Contracts never expose entities. |
| | `People/PersonValidators.cs` | FluentValidation for shape (lengths, formats, dates via `TimeProvider`). |
| | `People/PersonService.cs` | The use-case service: validate → check lookups (`ILookupService.EnsureValidAsync`) → stage through repositories → one `SaveAsync`; returns `ValidationResponse`. Paging, filtering and sorting run in SQL. Files (the photo) go through `FileService`. |
| | `People/PersonDataExchange.cs` | An `Exporter<TRow>` that pages the same search the screen uses, and an `Importer` that applies each row through the service (so the form's rules apply). |
| | `DependencyInjection.cs` | Registers the service, exporter and importer. |
| | `Security/PermissionCodes.cs` + `PermissionCatalog.cs` | `People.View` / `People.Manage`. |
| | `MasterData/LookupCatalog.cs` | The module's lists (`People.Title`, `People.Department`, `People.Skill`) and their starting values. |
| Infrastructure | `Persistence/Configurations/PeopleConfiguration.cs` | Lengths, enum as string, the primitive collection, indexes that match the list's filters, a filtered unique email index. |
| | `Persistence/AppDbContext.cs` | The `DbSet`. Then `dotnet ef migrations add` (never hand-written). |
| Web | `Controllers/PeopleController.cs` | Thin: maps `ValidationResponse` to views and flash messages; `[HasPermission]` on every action. |
| | `Controllers/Api/PeopleApiControllers.cs` | The same service over HTTP with `ToActionResult`. |
| | `ViewModels/PeopleViewModels.cs` | A form model that binds posts and maps to the request record; display helpers. |
| | `Views/People/Index.cshtml` | List: page header, segmented status filter with counts, auto-applying department filter, search, sortable headers, skill chips that filter, row action menu, empty states, a drawer to add without leaving the list, export/import. Turns into cards on phones. |
| | `Views/People/Details.cshtml` | Master record: header with a segmented status control and an actions menu; tabs for profile, linked documents (`LinkedDocuments`) and history (the audit trail as a timeline); a form that reads as text until Edit is pressed; dialogs (bottom sheets on phones) for the photo and removal. |
| | `Views/People/_PersonFields.cshtml` | One set of fields for the drawer, the full form and the profile. Uses `asp-lookup` selects, the searchable combobox (`data-combobox`) and chip multi-select (`_LookupChips`). |
| | `Views/Shared/_Sidebar.cshtml`, `Views/Home/Index.cshtml` | Navigation and a home shortcut, gated by permission. |
| Tests | `tests/Base.IntegrationTests/People/PeopleTests.cs` | Real SQL Server and a real acting user: lookups, validation, filtering and sorting, the audit trail's redaction, export and import, photos. |
| | `tests/Base.IntegrationTests/Web/WebTests.cs` | Pages render, the API round-trips, anonymous calls are refused. |

## Checklist for a new module

1. Entity in `Base.Domain/<Module>`; rules live in its methods. Mark personal fields `[AuditRedacted]`.
2. Lists the module needs: constants in `LookupLists` + `LookupCatalog` entries. Store codes; label with `LookupLabels`.
3. Contracts, validator, service, exporter/importer in `Base.Application/<Module>`; register in DI.
4. Permissions in `PermissionCodes` + `PermissionCatalog`. Plan-dependent? A feature in `FeatureCodes` + `[RequiresFeature]`.
5. Configuration + `DbSet`, then a generated migration and the drift check.
6. Controller, API, views from the design system; sidebar entry and home shortcut.
7. Integration tests like `PeopleTests`; a web smoke test.
8. Not needed in your project? Delete `People` (the files above, its lookup lists, permissions, sidebar entry and tests) and add a migration that drops the table.
