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

Prerequisites: .NET 10 SDK, Docker.

```bash
docker compose up -d                                   # SQL Server + OpenTelemetry dashboard (http://localhost:18888)
cd src/Base.Web
dotnet user-secrets set "Seed:AdminPassword" "<a 12+ character password>"
dotnet run --launch-profile https
```

In Development the app applies migrations, seeds the default tenant, settings, roles and the `admin@base.local`
account, and stores uploads on local disk under `.data/`. The API reference is at `/scalar`.

## Everyday commands

| Task | Command |
| --- | --- |
| Build (warnings are errors) | `dotnet build` |
| All tests (needs Docker for SQL Server) | `dotnet test` |
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
- **Multi-tenancy (optional):** host-based resolution, per-tenant users and settings, cross-tenant writes blocked
  in the interceptor. Single-tenant deployments run as one default tenant.
- **Settings:** a typed, validated catalogue; tenant → platform → default resolution; HybridCache with tag
  invalidation; a seeder that never overwrites an administrator's value.
- **Files:** the bytes go to object storage and SQL holds only a metadata row. Uploads are streamed with size
  limits, signature (magic-byte) checks and SHA-256. Object keys never contain user input, every download goes
  through an access check, and downloads use pre-signed URLs.
- **Reliability:** transactional outbox (domain events saved with the change, dispatched with retries, backoff and
  dead-lettering, safe across instances); startup tasks; health probes (`/health/live`, `/health/ready`).
- **Diagnostics:** ProblemDetails everywhere with a ticket number; correlation ids; redacted logging; throttled,
  HTML-encoded error alert emails; Serilog; OpenTelemetry traces and metrics (OTLP).
- **Quality gates:** central package management; warnings as errors; .NET + Meziantou analyzers; banned APIs
  (`DateTime.Now`, raw SQL strings, `Newtonsoft`, MD5/SHA1…); NuGet vulnerability audit; architecture tests;
  CI with format check, migration-drift check, tests, secret scanning, CodeQL and Dependabot.

## Configuration

| Section | Purpose |
| --- | --- |
| `ConnectionStrings:Default` | SQL Server. Development points at the docker-compose container; elsewhere use secrets/env. |
| `Database` | `MigrateOnStartup`, `CommandTimeoutSeconds`, `MaxRetryCount`. |
| `Tenancy` | `MultiTenant`, `DefaultTenantCode`, `FallbackToDefaultTenant`. |
| `FileStorage` | `LocalDisk`, `S3` (Bucket, ServiceUrl, Region, AccessKey, SecretKey, ForcePathStyle), `AzureBlob`. |
| `Smtp` | Mail server. Empty `Host` logs emails instead of sending them. |
| `Outbox` | Poll interval, batch size, max attempts, retention. |
| `Hangfire` | Optional job server and dashboard (`Enabled: false` by default). |
| `Seed` | First administrator (`AdminEmail`; `AdminPassword` from user-secrets only). |
| `Serilog`, `OTEL_EXPORTER_OTLP_ENDPOINT` | Logging and telemetry export. |

Set the `General.PublicBaseUrl` setting to the address users reach the app at: invite and reset emails link to it.
It is a setting, not the request's `Host` header, so a forged header can't redirect those links.

Business settings (application name, upload limits, alert recipients…) are not configuration. They live in
the settings catalogue and can be changed at runtime through `/api/settings`.
