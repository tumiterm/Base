# Base: .NET 10 starter solution

This repo is the reusable base for every new .NET 10 project. Read these before changing structure, adding a
cross-cutting feature or adding a package:
- [docs/architecture.md](docs/architecture.md): how it fits together (layers, pipeline order, persistence, tenancy)
- [docs/adr/](docs/adr/): why it is built this way
- [docs/base-project-blueprint.md](docs/base-project-blueprint.md): what was kept from FO16, and what is banned
- [docs/ui-design-direction.md](docs/ui-design-direction.md): the minimum bar for every screen (read before any UI work)
- [docs/vertical-slice.md](docs/vertical-slice.md): a new module copies the People sample, file by file
- [docs/api.md](docs/api.md): API keys, idempotency, limits, versions and CORS
- [docs/changelog.md](docs/changelog.md): what changed in each Base version (add to Unreleased with every change)

## Commands

- Build: `dotnet build` (warnings are errors; NuGet audit fails on vulnerable packages)
- Test: `dotnet test` (all), `dotnet test --project tests/Base.UnitTests` (one). Integration tests use Docker, LocalDB on
  Windows without Docker, or `BASE_TEST_SQLSERVER`.
- Run: in Development no config is needed (LocalDB on Windows, docker-compose SQL elsewhere; admin `admin@base.local` /
  `Admin-Dev-Password-1`).
- Style: `dotnet format --verify-no-changes`
- Migration: `dotnet ef migrations add <Name> --project src/Base.Infrastructure --startup-project src/Base.Infrastructure --output-dir Persistence/Migrations`
- Drift check: `dotnet ef migrations has-pending-model-changes --project src/Base.Infrastructure --startup-project src/Base.Infrastructure`
- New project from this base: `dotnet run scripts/new-project.cs -- Acme.Payroll ../Acme.Payroll`

## Rules

- Layers: Domain ← Application ← Infrastructure ← Web. Domain references nothing. Ports live in Application.
  Controllers never touch `AppDbContext`. Architecture tests enforce this.
- Entities derive from `EntityBase` (audit, soft delete, row version, domain events). `INamedEntity` and
  `ITenantOwned` are opt-in. Keys are SQL Server-sequential GUIDs (`SequentialGuid`), not v7 (ADR 0003).
- Time comes from the injected `TimeProvider` and is stored as UTC `DateTimeOffset`. `DateTime.Now`/`UtcNow` are banned at compile time.
- The current user comes from `ICurrentUser` (claims). Tenant isolation is the `Tenant` named filter and fails
  closed; cross-tenant work needs `BeginSystemScope()`.
- Repositories stage, `IUnitOfWork.SaveAsync` commits. Use `ExecuteInTransactionAsync` for multi-step transactions.
  `Remove` soft-deletes. Raw SQL is `FormattableString` only.
- Use cases return `ValidationResponse`/`ValidationResponse<T>`; Web maps them with `ToActionResult` / `AddToModelState`.
- New permission = constant in `PermissionCodes` + `PermissionCatalog` entry; gate with `[HasPermission]`.
  Permissions that act across tenants or on shared data (roles) are `IsPlatform`: only the platform (default)
  tenant's users get them.
- Plan-dependent behaviour: new feature/limit = constant in `FeatureCodes`/`LimitCodes` + `EntitlementCatalog` entry
  (its `ModuleCodes` module, `Since` version and, for a feature, suggested `FeatureTier`; a new module also gets a
  `Modules` entry) and enforcement (`[RequiresFeature]` + `asp-feature`, or `ITenantEntitlements.EnsureWithinLimitAsync` before the
  action). Never check plan codes or prices in code. Tenants are created through `ITenantAdministration`,
  subscriptions changed through `ISubscriptionService`. Online payments go through `IPaymentService` and a hosted
  `IPaymentGateway` (adapter + `Payments:Gateways:{Code}` config; credentials are deployment secrets). Card data never
  touches the app; only the gateway's verified notification marks a payment paid, never the return page (ADR 0026).
- New setting = constant in `SettingCodes` + `SettingCatalog` entry (no migration); it appears on Administration →
  Settings by itself. A switch is a `Boolean` setting read with `GetBoolAsync` in the service that does the work.
  Platform-scope settings change only from the platform tenant. Deployment secrets (connection strings, signing and
  provider keys) are never settings; only operator-entered credentials may be `Secret` settings (encrypted,
  write-only, platform scope).
- API controllers are `[ApiController]` under `api/...`, gated by `[HasPermission]` (API clients carry permissions
  too), return problem details through `ToActionResult`, and are version 1.0 unless marked `[ApiVersion]`. Never read a
  key or token from the query string, never add per-endpoint auth: callers are a person (cookie) or an `ApiClient`
  (key). The one exception is `payments/notify/{gateway}`, verified by the gateway adapter. `Idempotency-Key`
  retries are handled by middleware; endpoints need nothing for it.
- Pick-lists (titles, departments, reasons...) are lookup lists: constant in `LookupLists` + `LookupCatalog` entry with
  defaults. Records store the code; validate with `ILookupService.EnsureValidAsync`, render with `asp-lookup` /
  `data-combobox` / `_LookupChips`, label with `LookupLabels`. Never a table or enum per list, never ids in code.
- Files only through `IFileService`: bytes go to object storage. The `Database` provider is only for small files in chosen
  categories (`Files.CategoryProviders`), capped by the `Files.Database.*` settings; it can't be the default. Extra
  upload checks (virus scanning, ...) implement `IFileUploadCheck`.
- A record other records can point at (documents' "linked to", ...) registers an `IRecordLabelProvider` (names only
  what the user may see) and a row in `RecordLinks`, so screens show names, never ids. Previews go through
  `FilePreviews` and the framed-preview headers; never serve a stored file inline any other way.
- Documents (titled, versioned, linked to any record) go through `IDocumentService`. New document type = constant in
  `DocumentTypes` + `DocumentTypeCatalog` entry; new generated document = constant in `DocumentTemplates` + catalogue
  entry + `Documents/Templates/{Code}.html`. Output formats are `IDocumentRenderer`s; no licensed engine in the base.
- Lists are exported by an `IExporter` (`Exporter<TRow>`, paging an existing use case) and files are imported by an
  `IImporter` (apply rows through existing use cases); register them in DI. Never hand-build CSV/Excel. Background work
  that acts for a person uses `IRunAsUser`, so their permissions apply.
- Recurring housekeeping is an `IScheduledJob` (re-runnable; the scheduler leases, records and shows it), not a
  `BackgroundService` with its own timer. A new external dependency gets an `IHealthCheck` tagged `dependency`
  (never `ready` unless the app can't serve without it).
- Notify people only through `INotificationService` (template code + recipient + data). New template = constant in
  `NotificationTemplates` + `NotificationCatalog` entry + `Notifications/Templates/{Code}.txt` (tests check they match).
  Templates containing secrets are `IsSensitive` (sent at once, stored without content). Providers are configured
  under `Notifications:Providers` (or, for email, the SMTP server under Settings → Email); nothing is sent on a channel
  until it is switched on. `IEmailSender` is only for
  untemplated operator email.
- Messages people write go through `IMessagingService` (single, bulk, scheduled). A module whose records are people
  others may write to registers an `IAudienceSource` (filters, placeholders, members read through its own use cases):
  never let a screen pick tables or columns to message. Their HTML is cleaned with `IRichText`.
- User and role changes go through `IUserAdministration`/`IRoleAdministration` (never `UserManager` from Web), which
  enforce `PermissionGuard`: nobody grants or manages access they don't hold. Emails with links are sent from outbox
  handlers (via the sensitive notification templates) and build links from the `General.PublicBaseUrl` setting, never the request host.
- Changes are audited automatically (`AuditTrailInterceptor`). Mark personal or secret fields `[AuditRedacted]` and
  noise `[NotAudited]`. `ExecuteUpdate`/`ExecuteDelete` bypass auditing: don't use them on business data.
- Side effects after a commit go through domain events + `IDomainEventHandler<T>` (outbox; handlers idempotent).
- Every schema change ships a generated migration. Never hand-write migrations or edit the snapshot.
- Package versions live only in `Directory.Packages.props`. No pre-release packages.
- Versions: `BaseVersion` in `Directory.Build.props` (SemVer, ADR 0019). Every change people would notice adds a line
  under Unreleased in `docs/changelog.md` in the same commit; a release bumps the version and tags `vX.Y.Z`.
- A change that alters what people see or do can be release-gated: constant in `ReleaseFeatureCodes` + `ReleaseCatalog`
  entry (introduced version, `OptIn`/`OnByDefault`) + a gate (`ITenantReleases.IsOnAsync`, `[RequiresRelease]`,
  `asp-release`), tested in both states. Never gate fixes. Once on everywhere, set `Retired`, then delete the old path
  (ADR 0022).
- Code that isn't ready for everyone, or must be stoppable in production, is feature-flagged: constant in
  `FeatureFlagCodes` + `FeatureFlagCatalog` entry (owner; `Release` with `RemoveBy`, or `KillSwitch`) + a gate
  (`IFeatureFlags.IsOnAsync`, `[RequiresFlag]`, `asp-flag`), tested in both states. Flags are for engineering, never for
  what customers pay for (plans), take (releases) or configure (settings). Delete a release flag and its old path once
  it is on everywhere (ADR 0025).
- UI is built from the design system (`design-system.css` tokens, `<icon>`, the shell in `_Layout`) and must meet
  `docs/ui-design-direction.md`: no stock Bootstrap look, no inline scripts or styles (strict CSP), light and dark,
  phone layouts. Brand colours come from `var(--accent*)` (per-tenant `Branding.*` settings), never hard-coded.
- Integration tests use real SQL Server (Testcontainers), never EF InMemory. Main stays green.

## Banned (blueprint §11, §13)

Session-stored users and static mutable request state; static `Helper` god-classes; `ViewBag` as a contract;
local-time timestamps; hard-coded keys, IVs or licence keys; secrets in committed config;
`catch (Exception) { throw; }` and empty `catch { }`; fire-and-forget work that captures `HttpContext`; string-built SQL;
`EnsureCreated` outside tests; duplicate type names across layers; per-file author/date banners; licensed report
engines as base dependencies; repo-root clutter (documents and generators go in `/docs` or `/scripts`).
