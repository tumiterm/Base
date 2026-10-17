# Architecture

## Layers

```
Base.Web ──► Base.Infrastructure ──► Base.Application ──► Base.Domain
   │                                        ▲
   └────────────────────────────────────────┘
```

Dependencies point inward only, enforced by `tests/Base.ArchitectureTests`.

| Layer | Owns | Must not reference |
| --- | --- | --- |
| Domain | Entities, value rules, domain events, `EntityBase`, `SequentialGuid` | Anything (no NuGet packages, no other project) |
| Application | Use-case services, ports (`IRepository`, `IUnitOfWork`, `IFileStorageProvider`, `ICurrentUser`, `ITenantContext`, `IEmailSender`), `ValidationResponse`, catalogues | EF Core, ASP.NET Core, storage SDKs, Infrastructure, Web |
| Infrastructure | EF Core, Identity, tenancy resolution, storage/email adapters, outbox, startup tasks, Hangfire | Web |
| Web | `Program.cs`, middleware, auth handlers, controllers, views | `AppDbContext` from controllers |

Each layer registers itself: `AddWeb(...)`, `AddApplication()`, `AddInfrastructure(...)`. `Program.cs` only composes.

## Request pipeline

Order matters; this is `WebApplicationExtensions.UseWeb`:

1. Forwarded headers (only for proxies listed in `ForwardedHeaders:KnownProxies`)
2. Exception handler → `GlobalExceptionHandler` (ProblemDetails for `/api`, re-executes `/error` for pages)
3. Status code pages (`/error/{code}`; `/api` paths get ProblemDetails)
4. HSTS (non-development), HTTPS redirection
5. Security headers (CSP, frame, referrer, permissions policies)
6. Correlation id (accepts a well-formed `X-Correlation-ID`, otherwise the trace id; added to every log line)
7. Serilog request logging, static assets
8. Routing → rate limiter → **tenant resolution** (before authentication, because users are tenant-scoped)
9. Authentication → authorization (fallback policy: authenticated user)
10. Health checks, OpenAPI (development), Hangfire dashboard (if enabled), controllers

## Persistence rules

- **One entity base.** `EntityBase` gives `Id`, audit stamps, soft delete, `RowVersion` and domain events.
  `INamedEntity` (Code/Name) and `ITenantOwned` are opt-in.
- **Keys** are SQL Server-sequential GUIDs, assigned on add (EF) or with `SequentialGuid.Next()` when the id is
  needed earlier. Guid v7 is deliberately not used: SQL Server sorts `uniqueidentifier` by its last bytes, so v7
  fragments clustered indexes (proven in `SequentialGuidTests`).
- **Conventions by interface** (`EntityConventions`): row version, audit defaults and lengths, the
  `SoftDelete` and `Tenant` **named** query filters. Named filters can be lifted individually:
  `IgnoreQueryFilters([QueryFilters.SoftDelete])` keeps tenant isolation.
- **Interceptors:** `AuditingInterceptor` stamps UTC audit columns from `TimeProvider`/`ICurrentUser` and
  enforces tenant ownership; `DomainEventOutboxInterceptor` writes raised events to the outbox in the same save.
- **Repositories stage, the unit of work commits.** `Remove` soft-deletes; `HardDelete` is explicit.
  `ExecuteInTransactionAsync` wraps the work in EF's execution strategy, so retries on transient faults stay valid.
- **Configuration** lives in `IEntityTypeConfiguration<T>` classes. Migrations are generated, never hand-written;
  CI fails on model drift and an integration test checks it too.

## Tenancy

`ITenantContext` is scoped. In single-tenant mode every request is the default tenant. In multi-tenant mode
the request host must be a verified `TenantDomain`; unknown hosts get 404 unless `FallbackToDefaultTenant`
is set. Tenant isolation **fails closed**: with no tenant resolved, tenant-owned queries return nothing and
inserts throw. Platform jobs opt out explicitly with `using (tenant.BeginSystemScope())`. The outbox runs each
event in the tenant it was raised in.

Users belong to a tenant; user names are unique per tenant. Roles and their permissions are global.

## Authorization

Permissions are constants in `PermissionCodes` with one `PermissionCatalog` entry each (unit-tested both ways).
Roles carry permissions as role claims; `AppUserClaimsPrincipalFactory` expands implications into the cookie.
The security stamp validator rebuilds the principal every minute, so permission changes and lockouts apply
without a logout. Gate endpoints with `[HasPermission(PermissionCodes.X.Y)]` and UI elements with
`asp-permission="..."`. An unknown permission throws instead of silently denying.

## Users and roles

`IUserAdministration`, `IRoleAdministration` and `IAccountService` are ports in Application, implemented in
`Infrastructure/Identity` on top of ASP.NET Core Identity. Screens live at `/admin/users` and `/admin/roles`, and
the API at `/api/users`, `/api/roles` and `/api/permissions`.

- **Invite-only onboarding.** Administrators never set passwords. An invite creates the account without a password
  and raises `UserInvited`; the outbox handler generates a 72-hour token at send time and emails a link built from
  the `General.PublicBaseUrl` setting. Tokens embed the security stamp, so a link works once.
- **Self-service.** Forgot password always answers the same way, whether or not the account exists. Reset tokens
  last 1 hour. Changing a password rotates the security stamp: other sessions end within a minute, and the current
  one is refreshed.
- **Escalation guard** (`PermissionGuard`). You can only assign roles, create or edit roles, or manage users
  (update, lock, reset) when you hold every permission involved. A user with `Users.Manage` cannot touch an administrator.
- **Safety rails.** You can't lock yourself. The last active administrator can't be locked or demoted. System roles
  (`Administrator`, `User`) can't be renamed or deleted, and Administrator always holds every permission. A role
  still assigned to users can't be deleted. Locking rotates the security stamp, which signs the user out everywhere.
- **Passwords** follow NIST SP 800-63B: at least 12 characters, at least 4 distinct, no forced digits or symbols, and
  lockout after 5 failures.

## Notifications

Business code calls `INotificationService.SendAsync(new Notification(template, recipient, data, channels?))`.

- **Templates** are entries in `NotificationCatalog` (category, default channels, fallback, mandatory or sensitive,
  WhatsApp template) plus a wording file `Application/Notifications/Templates/{Code}.txt`. A file has sections
  (`email.subject`, `email.html`, `email.text`, `sms`, `inapp.title`, `inapp.body`) with `{{Variable}}` placeholders.
  Unknown variables are an error; HTML parts are encoded. Tenants override individual sections in the database, and
  overrides are validated against the template's variables.
- **Queueing.** `SendAsync` resolves the recipient (from the user account or explicit addresses; phones are
  normalised to E.164), then picks the first usable channel in each chain (primary, then fallback). Channels that are
  off, have no address, or that the user opted out of are recorded as **Skipped**, with the reason. The rest are
  queued as `NotificationMessage` rows, which raise `NotificationQueued`, and the unit of work is saved.
- **Delivery** (`NotificationDispatcher`, an outbox handler) sends through the channel's provider. Retryable failures
  (timeouts, 429, 5xx) are retried up to 3 times with the outbox's backoff. Permanent failures, or running out of
  retries, queue the next fallback channel. In-app messages go straight to the user's inbox.
- **Sensitive** templates (invite and reset links) are sent immediately, and their rows keep no content, data or
  addresses. If no channel accepts them, `SendAsync` throws, so the outbox retries the caller with a fresh token.
- **Providers** are named instances from `Notifications:Providers` (by `Type`), plus any `INotificationProvider` in DI.
  Each channel has a default instance, and a tenant setting can pick another. `IEmailSender` (plain, untemplated email,
  e.g. error alerts) uses the Email channel's provider too.
- **Retention:** `NotificationRetentionService` purges messages older than `Notifications.RetentionDays` (default 365).

## Audit trail

`AuditTrailInterceptor` (the last interceptor in the chain) turns each audited insert, update, soft delete, restore and
hard delete into an `AuditEntry` in the same SaveChanges, so a change and its record commit or roll back together.

- **What is audited** (`AuditPolicy`): every `IAuditable` entity, which means all `EntityBase` types and users, plus
  roles, role assignments, role and user claims, and external logins. Identity tokens are never audited. Opt a type or
  property out with `[NotAudited]`; record that a property changed without its values with `[AuditRedacted]`
  (`PasswordHash` and `SecurityStamp` are redacted already).
- **What is recorded:** entity type and key, action, changed properties with old and new values (only real
  differences; bookkeeping columns such as `DateModified`, `RowVersion` and sign-in counters are skipped), user id and
  name, trace id, UTC time.
- **Keys:** composite keys start with their owner (`userId|roleId`), and claims are recorded against their role or
  user, so filtering by a user's or role's id shows its whole history.
- **Isolation and integrity:** a named `Tenant` filter shows a tenant only its own entries; platform entries (tenants,
  platform settings) need system scope. The interceptor rejects any update or delete of an entry.
- **Retention:** `AuditRetentionService` deletes entries older than the `Audit.RetentionDays` setting (default 730)
  once a day, in batches.
- **Limitation:** diffs use the change tracker's original values. An entity attached detached and marked modified
  records an update without values. Load, then change, is the pattern everywhere in the base.

## Results and errors

Use cases return `ValidationResponse` / `ValidationResponse<T>` (immutable, `IsSuccess`, `ErrorType`, field
errors). `ToActionResult` maps them to HTTP (400 ValidationProblem, 404, 403, 409…); `AddToModelState` feeds
MVC forms. Unexpected exceptions reach `GlobalExceptionHandler`: one log entry with a ticket, a redacted query
string, and an alert queued for `ErrorAlertSender` (throttled per fingerprint, HTML-encoded). The response
carries the ticket and nothing internal.

## Files

`IFileService` is the only entry point. Uploads are streamed to a temp file while they are measured, hashed (SHA-256) and
signature-checked; the content type is derived from the extension, never the client. Bytes go to the
configured provider under `{tenant}/{category}/{yyyy}/{MM}/{id}{ext}`; SQL stores a `StoredFile` row. If the row
can't be saved, the object is deleted. Reads use the provider recorded on the file, check
`IStoredFileAccessPolicy` (owner or `Files.ReadAll`), and redirect to a short-lived pre-signed URL when possible.

The provider is chosen per category: `Files.CategoryProviders` (e.g. `avatar=Database`) wins over `Files.DefaultProvider`.
The `Database` provider keeps bytes in `FileContents`, capped per file and per tenant by `Files.Database.*` settings (read
on every upload, so changes apply at once). Before storing, each registered `IFileUploadCheck` runs in `Order`; ClamAV
is one, registered when `FileScanning:ClamAv:Host` is set. Deletes are soft. A daily job purges files that have been
deleted or expired for longer than `Files.DeletedRetentionDays`: it deletes the object first and the row second, so no
bytes are ever orphaned.

## Documents

`IDocumentService` adds titles, types, tags, record links (`LinkedEntityType`/`LinkedEntityId`, no foreign key) and
versions on top of files. Each version is a `StoredFile` with no owner, so it is reachable only through the document,
whose `IDocumentAccessPolicy` decides: `Documents.View` reads everything, `Documents.Manage` does everything, and
`Documents.Create` lets people work on their own drafts. The new file and the document row are saved in one
`SaveAsync`; if it fails, the stored object is deleted. Final documents take no new versions until reopened. A daily
job applies each `DocumentTypeCatalog` type's `ArchiveAfterDays` and `DeleteAfterDays` across tenants.

`IDocumentGenerator` fills an HTML template from `Documents/Templates/{Code}.html` (values HTML-encoded, plus
`AppName` and `Today`) and hands it to the `IDocumentRenderer` for the requested format. `html` is built in; `pdf` is
`GotenbergPdfRenderer` when `Documents:Pdf:Gotenberg:BaseUrl` is set. The output is stored like an upload (size limit
and checks apply, but not the upload allow-list) or returned for preview. `LinkedDocuments` is a view component that
lists and uploads a record's documents on any page.

## Exports and imports

Each list that can be exported has an `IExporter`, usually an `Exporter<TRow>` that pages an existing query use case
(`IUserAdministration.SearchAsync`, `IAuditTrail`...), so whatever that use case filters out stays out. The list's
query string is the export filter. `IExportService` counts first:
- up to `Exports.MaxImmediateRows` rows, it streams the file;
- above that (and up to `Exports.MaxRows`), it queues an `ExportJob` whose outbox handler writes the file. The file is
  a `StoredFile` owned by the requester that expires after `Exports.RetentionDays`, and the requester gets an in-app
  notification.

Each kind of import has an `IImporter`. Upload goes through `FileService` (signature, size and upload checks apply).
The dry run maps headers (by name or alias, ignoring case and punctuation), parses typed cells, runs the importer's
row checks and records an `ImportJob` with the counts and the first 1,000 errors. Confirm raises `ImportQueued`. The
handler re-runs the checks, then applies row by row, stopping at the first failure unless the importer allows partial
imports.

Both handlers run inside `IRunAsUser`. Infrastructure wraps the host's `ICurrentUser` in an overridable one, and
run-as installs a principal built by the sign-in claims factory, so `HasPermission`, `PermissionGuard` and the audit
stamps all see the real person.

Formats are `ITabularWriter`/`ITabularReader` in Infrastructure: CSV (formula-injection safe) and XLSX (OpenXML SDK,
streaming).

## Background work

- **Outbox** (`OutboxProcessor`): due messages are claimed with an atomic lease, so several instances are safe.
  Handlers implement `IDomainEventHandler<TEvent>` and must be idempotent. Failures retry with exponential
  backoff and are dead-lettered after `MaxAttempts`; the system page can retry them. The `outbox-cleanup` job purges
  processed messages after `RetentionDays`.
- **Scheduled jobs** (`IScheduledJob`, run by `JobSchedulerService`): audit, notification, file, document and error-log
  retention, and outbox cleanup. Each job runs on one instance at a time (a lease row in `ScheduledJobs`), in system
  scope, as "System". Every run is recorded with its duration and summary. `/admin/system` shows them and can run one
  now. Turn the scheduler off per host with `Jobs:Enabled`.
- **Hangfire** (optional): scheduled and recurring jobs with SQL storage in the `hangfire` schema; dashboard
  behind `System.Jobs.Manage`.
- **Startup tasks** (`IStartupTask`): migrate (when enabled), default tenant, settings, roles and administrator.
  All idempotent.

## Health and diagnostics

- `/health/live` (process up) and `/health/ready` (database) are anonymous, for orchestrators.
- Every other check is tagged `dependency` and shown on `/admin/system` (and `/api/system/health`) with its data.
  The checks: storage providers, the database file quota, notification provider reachability, Gotenberg, ClamAV,
  outbox, scheduled jobs and Hangfire.
- Unhandled errors get a ticket in the response. The background reporter records them in `ErrorLog` and emails the
  alert recipients. `/admin/system/errors/{ticket}` shows the stack trace and links to the trace and logs
  (`Observability:TraceUrlTemplate`, `LogSearchUrlTemplate`).
- Commands slower than `Database:SlowQueryThresholdMs` are logged with their SQL and trace id and counted as
  `base.db.slow_commands`. EF Core's meter is exported too.

## UI and branding

- `wwwroot/css/design-system.css` holds the tokens (light and dark) and the components. Bootstrap 5.3 is only the
  behaviour layer: its `--bs-*` variables point at our tokens. Pages use the components and `var(--…)`, never raw colours.
- `_Layout` is the app shell (sidebar from `_Sidebar`, topbar, command palette, toasts); `_AuthLayout` wraps the
  sign-in pages. Behaviour is wired in `site.js` through data attributes. `theme-init.js` runs in `<head>` to set the
  light or dark theme before first paint.
- Branding is three tenant settings (`Branding.PrimaryColor`, `DefaultTheme`, `LogoFileId`) read by
  `IBrandingService`. `/theme.css?v={version}` (`ThemePalette`) turns the colour into the accent ramp with WCAG
  contrast text; `/branding/logo` serves the logo. Both are anonymous, so the sign-in page is branded too.
- The CSP stays strict: no inline scripts or styles. Dynamic values are set with `style.setProperty` from script
  or served as CSS.

## Testing strategy

| Project | Scope | Speed |
| --- | --- | --- |
| UnitTests | Domain rules, catalogues (codes ↔ catalogue), ValidationResponse, file validation | Milliseconds |
| ArchitectureTests | Layer dependencies, single entity base, immutable events | Milliseconds |
| IntegrationTests | Real SQL Server (Testcontainers): filters, tenancy, audit, concurrency, transactions, outbox, settings, migrations, user/role administration, notifications (delivery, retry, fallback, opt-outs, overrides), provider HTTP request shapes; the full web pipeline via `WebApplicationFactory` | ~50 s |

EF InMemory is not used: it ignores the SQL translation of filters, row versions, transactions and unique indexes.
