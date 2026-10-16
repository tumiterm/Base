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

## Background work

- **Outbox** (`OutboxProcessor`): due messages are claimed with an atomic lease, so several instances are safe.
  Handlers implement `IDomainEventHandler<TEvent>` and must be idempotent. Failures retry with exponential
  backoff and are dead-lettered after `MaxAttempts`. Processed messages are purged after `RetentionDays`.
- **Hangfire** (optional): scheduled and recurring jobs with SQL storage in the `hangfire` schema; dashboard
  behind `System.Jobs.Manage`.
- **Startup tasks** (`IStartupTask`): migrate (when enabled), default tenant, settings, roles and administrator.
  All idempotent.

## Testing strategy

| Project | Scope | Speed |
| --- | --- | --- |
| UnitTests | Domain rules, catalogues (codes ↔ catalogue), ValidationResponse, file validation | Milliseconds |
| ArchitectureTests | Layer dependencies, single entity base, immutable events | Milliseconds |
| IntegrationTests | Real SQL Server (Testcontainers): filters, tenancy, audit, concurrency, transactions, outbox, settings, migrations, user/role administration and the emailed invite and reset links; the full web pipeline via `WebApplicationFactory` | ~40 s |

EF InMemory is not used: it ignores the SQL translation of filters, row versions, transactions and unique indexes.
