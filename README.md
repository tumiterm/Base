# Base: .NET 10 starter solution

The starting point for every new .NET 10 project: Clean Architecture, EF Core + SQL Server, ASP.NET Core
Identity with catalogue-driven permissions, optional multi-tenancy, a settings catalogue, object-storage file
handling, a transactional outbox, and a test suite that runs against real SQL Server.

It is harvested from ForekOnline (FO16): what worked was kept and hardened, and what hurt is banned by tooling.
The design record is [`docs/base-project-blueprint.md`](docs/base-project-blueprint.md); how the pieces fit
together is in [`docs/architecture.md`](docs/architecture.md); individual decisions are in [`docs/adr`](docs/adr).

## Start a new project from this base

```bash
dotnet run scripts/new-project.cs -- Acme.Payroll ../Acme.Payroll
cd ../Acme.Payroll && git init && dotnet tool restore && dotnet build && dotnet test
```

The script copies the repository, renames `Base.*` projects, namespaces and the solution to `Acme.Payroll.*`
(leaving `EntityBase`, `ControllerBase`, `Database`… alone), and generates a fresh user-secrets id.

## Run it locally

In Development the app needs no configuration. With no connection string set it uses:

- **Windows:** SQL Server **LocalDB** (installed with Visual Studio's ASP.NET workload), database `Base`.
- **macOS / Linux:** the docker-compose SQL Server (`docker compose up -d sqlserver`).

On startup it applies migrations, seeds the default tenant, settings and roles, and creates the
**`admin@base.local` / `Admin-Dev-Password-1`** account (Development only). Uploads go to local disk under `.data/`,
and emails are written to the log. The API reference is at `/scalar`.

### Windows + Visual Studio

1. Open `Base.slnx` and set **Base.Web** as the startup project.
2. Press **F5** (the `https` profile). Sign in as `admin@base.local` / `Admin-Dev-Password-1`.
3. Tests: **Test Explorer → Run All**. Without Docker, the integration tests use LocalDB and drop their databases
   at the end.

If LocalDB isn't installed: Visual Studio Installer → Modify → Individual components → *SQL Server Express LocalDB*.
Check it with `sqllocaldb info` (it should list `MSSQLLocalDB`).

### Command line, any OS

```bash
docker compose up -d                                   # SQL Server + OpenTelemetry dashboard (http://localhost:18888)
dotnet run --project src/Base.Web --launch-profile https
```

### Using a different database

Set `ConnectionStrings:Default`, which always wins over the fallback:
`dotnet user-secrets set "ConnectionStrings:Default" "Server=.;Database=Base;Trusted_Connection=True;TrustServerCertificate=True" --project src/Base.Web`.
Integration tests can be pointed at any SQL Server with the `BASE_TEST_SQLSERVER` environment variable.
Outside Development a missing connection string is a startup error: there is no fallback.

## Everyday commands

| Task | Command |
| --- | --- |
| Build (warnings are errors) | `dotnet build` |
| All tests (Docker, or LocalDB on Windows) | `dotnet test` |
| One test project | `dotnet test --project tests/Base.UnitTests` |
| Format / style check | `dotnet format --verify-no-changes` |
| Add a migration | `dotnet ef migrations add <Name> --project src/Base.Infrastructure --startup-project src/Base.Infrastructure --output-dir Persistence/Migrations` |
| Check for model drift | `dotnet ef migrations has-pending-model-changes --project src/Base.Infrastructure --startup-project src/Base.Infrastructure` |
| Container image | `docker build -f src/Base.Web/Dockerfile -t base-web .` |

## Solution layout

```
src/
  Base.Domain           Entities, domain events, EntityBase. References nothing.
  Base.Application      Use cases and ports: IRepository/IUnitOfWork, ValidationResponse, permission and setting
                        catalogues, file service, ICurrentUser/ITenantContext. No EF Core, no ASP.NET Core.
  Base.Infrastructure   EF Core (DbContext, conventions, interceptors, migrations), Identity, tenancy, storage
                        providers (S3/R2/MinIO, Azure Blob, local disk), email, outbox, startup tasks, Hangfire.
  Base.Web              Composition root: Program.cs, middleware, auth, controllers, views.
tests/
  Base.UnitTests        Fast tests of Domain and Application.
  Base.ArchitectureTests Layering and convention rules (NetArchTest).
  Base.IntegrationTests Real SQL Server via Testcontainers, plus the full web pipeline via WebApplicationFactory.
scripts/new-project.cs  Creates a renamed copy for a new project.
```

## What you get

- **Persistence:** a single `EntityBase` (audit, soft delete, row version, domain events); SQL Server-sequential
  GUID keys; UTC timestamps from `TimeProvider`; named query filters for soft delete and tenant isolation
  (fail closed); a repository that never commits; a small unit of work with retry-safe transactions; paging and
  projection run in the database.
- **Security:** Identity cookies with lockout, hashed reset tokens and principal refresh every minute;
  `[HasPermission]` backed by a permission catalogue; secure-by-default fallback policy; antiforgery on every
  unsafe request (API included); rate-limited sign-in; CSP and security headers; Data Protection keys in the
  database; secrets only from user-secrets/environment.
- **Users and roles:** admin screens and API to search, invite, edit, lock and unlock users and to manage roles
  with grouped permission checkboxes. Users are onboarded by emailed invite (they choose their own password), and
  there are forgot, reset and change password flows. Nobody can grant or manage access they don't hold, the last
  active administrator is protected, and system roles are locked down. Passwords follow NIST SP 800-63B
  (length over composition rules).
- **Notifications:** one `INotificationService` for **email, SMS, WhatsApp and in-app**. Built-in providers:
  SMTP, SendGrid, Twilio (SMS and WhatsApp), Clickatell, BulkSMS and the Meta WhatsApp Cloud API. Each channel is
  switched on separately; out of the box only email (to the log) and in-app are on. Wording comes from template files,
  and each tenant can override it. Delivery goes through the outbox, with retries and fallback (e.g. WhatsApp, then SMS,
  then email). Users can opt out per category; security messages can't be switched off. There's a delivery log with
  resend, and an in-app inbox with a navbar bell. WhatsApp uses Meta-approved templates only.
- **Audit trail:** every change to an audited record (every `EntityBase` entity, users, roles, role assignments and
  permission grants) is stored with who, when, the trace id and before/after values, in the same transaction as
  the change. Secrets are recorded as "changed" without values. Entries are append-only, tenant-isolated and
  kept for a configurable retention period. Browse them at `/admin/audit`, from a user's or role's History link,
  or through `/api/audit`.
- **Multi-tenancy (optional):** host-based resolution, per-tenant users and settings, cross-tenant writes blocked
  in the interceptor. Single-tenant deployments run as one default tenant.
- **Settings:** a typed, validated catalogue; tenant → platform → default resolution; HybridCache with tag
  invalidation; a seeder that never overwrites an administrator's value.
- **Files:** the bytes go to object storage (S3-compatible, Azure Blob, local disk) and SQL holds only a metadata
  row. The exception is a `Database` provider for small files in categories you choose, with a per-file size cap and a
  per-tenant quota set in settings. Uploads are streamed with size limits, signature (magic-byte) checks and
  SHA-256, plus any extra checks you register (ClamAV virus scanning is built in). Object keys never contain user
  input, every download goes through an access check, downloads use pre-signed URLs, and deleted files are purged
  after a retention period.
- **Exports and imports:** any list to Excel or CSV from an Export button that keeps the list's filters. Big exports
  run in the background, and every export is recorded. Imports upload a file, check every row without saving, show
  the errors by row (with a downloadable "rows to fix" file), then apply in the background as the person who
  uploaded. By default nothing is applied unless every row is valid. Each importer has a template download. Built in:
  exports of users, the audit trail, the notification log and documents, and a bulk user invite.
- **Documents:** titled, typed, tagged documents with versions, Draft → Final → Archived, links to any record and
  retention rules per type. There are screens at `/documents`, an API at `/api/documents`, and a `LinkedDocuments`
  component you can drop onto any page. Documents can be generated from HTML templates: HTML is built in, PDF uses
  Gotenberg, and you can plug in your own engine.
- **Reliability:** transactional outbox (domain events saved with the change, dispatched with retries, backoff and
  dead-lettering, safe across instances); startup tasks; health probes (`/health/live`, `/health/ready`).
- **Operations:**
  - `/admin/system` shows the health of every dependency: database, storage, database file quota, email, SMS and
    WhatsApp providers, Gotenberg, ClamAV, outbox and jobs.
  - It also shows scheduled jobs with their run history and a "Run now" button, database size and the largest tables,
    storage per tenant, outbox dead letters with retry, and recorded errors searchable by ticket, with links to the
    trace and logs.
  - Slow queries are logged and counted.
- **Diagnostics:** ProblemDetails everywhere with a ticket number; correlation ids; redacted logging; throttled,
  HTML-encoded error alert emails; Serilog; OpenTelemetry traces and metrics (OTLP).
- **Quality gates:** central package management; warnings as errors; .NET + Meziantou analyzers; banned APIs
  (`DateTime.Now`, raw SQL strings, `Newtonsoft`, MD5/SHA1…); NuGet vulnerability audit; architecture tests;
  CI with format check, migration-drift check, tests, secret scanning, CodeQL and Dependabot.

## Configuration

| Section | Purpose |
| --- | --- |
| `ConnectionStrings:Default` | SQL Server. Development points at the docker-compose container; elsewhere use secrets/env. |
| `Database` | `MigrateOnStartup`, `CommandTimeoutSeconds`, `MaxRetryCount`, `SlowQueryThresholdMs` (500). |
| `Tenancy` | `MultiTenant`, `DefaultTenantCode`, `FallbackToDefaultTenant`. |
| `FileStorage` | `LocalDisk`, `S3` (Bucket, ServiceUrl, Region, AccessKey, SecretKey, ForcePathStyle), `AzureBlob`. |
| `Notifications` | Channels on/off and provider instances (see below). Default: email to the log, in-app on, SMS/WhatsApp off. |
| `FileScanning:ClamAv` | `Host`, `Port`, `TimeoutSeconds`. Set `Host` to scan every upload with clamd (uploads are refused if it is down). |
| `Documents:Pdf:Gotenberg` | `BaseUrl` (e.g. `http://gotenberg:3000/`), `TimeoutSeconds`. Set it to generate PDFs. |
| `Outbox` | Poll interval, batch size, max attempts, retention. |
| `Hangfire` | Optional job server and dashboard (`Enabled: false` by default). |
| `Jobs` | `Enabled`, `StartDelaySeconds`, `PollSeconds`, `HistoryDays` for the housekeeping scheduler. |
| `Observability` | `TraceUrlTemplate` (`{traceId}`) and `LogSearchUrlTemplate` (`{ticket}`): links from an error to its trace and logs. |
| `Seed` | First administrator (`AdminEmail`; `AdminPassword` from user-secrets only). |
| `Serilog`, `OTEL_EXPORTER_OTLP_ENDPOINT` | Logging and telemetry export. |

### Switching on a notification channel

Each channel names a provider instance; instances are defined once and can be reused by any tenant
(a tenant picks a different instance with the `Notifications.SmsProvider` / `EmailProvider` / `WhatsAppProvider` settings).
Keep the secrets in user-secrets or environment variables:

```json
"Notifications": {
  "Channels": {
    "Email":    { "Enabled": true, "Provider": "smtp" },
    "Sms":      { "Enabled": true, "Provider": "clickatell" },
    "WhatsApp": { "Enabled": true, "Provider": "meta" }
  },
  "Providers": {
    "smtp":       { "Type": "Smtp", "Host": "smtp.example.com", "FromAddress": "no-reply@example.com", "UserName": "...", "Password": "(secret)" },
    "clickatell": { "Type": "Clickatell", "ApiKey": "(secret)" },
    "meta":       { "Type": "MetaWhatsApp", "PhoneNumberId": "...", "AccessToken": "(secret)" }
  }
}
```

Types: `Log`, `Smtp`, `SendGrid`, `TwilioSms`, `TwilioWhatsApp`, `Clickatell`, `BulkSms`, `MetaWhatsApp`. A switched-on
channel that points at a missing provider stops startup with a message naming it. To add your own provider, implement
`INotificationProvider` and register it in DI; its `Name` becomes a valid `Provider` value.

### Storing files and generating PDFs

- To keep a category's files in SQL Server, set the `Files.CategoryProviders` setting, for example
  `avatar=Database,signature=Database`. The `Files.Database.MaxFileSizeKb` (default 1024) and
  `Files.Database.TenantQuotaMb` (default 100, 0 = no limit) settings cap what it accepts. Every other category uses
  `Files.DefaultProvider`.
- For PDFs, run `docker compose --profile pdf up -d` and set `Documents:Pdf:Gotenberg:BaseUrl` to
  `http://localhost:3000/`. Without it, generation offers HTML only. To use another engine, implement
  `IDocumentRenderer` and register it.
- For virus scanning, run `docker compose --profile scan up -d` and set `FileScanning:ClamAv:Host` to `localhost`.

Set the `General.PublicBaseUrl` setting to the address users reach the app at: invite and reset emails link to it.
It is a setting, not the request's `Host` header, so a forged header can't redirect those links.

Business settings (application name, upload limits, alert recipients…) are not configuration. They live in
the settings catalogue and can be changed at runtime through `/api/settings`.
