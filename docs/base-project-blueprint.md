# .NET 10 Base Project — Blueprint (harvested from FO16 / ForekOnline, Oct 2026)

Use this as the CLAUDE.md (or a linked doc) of a new base solution. It records what to carry over from
FO16, how to improve it on the way, and what must never come across.

---

## 1. Solution shape (keep)

Clean Architecture, four projects, dependencies point inward only:

```
{Name}.Domain          entities, enums, value objects, domain policies — references NOTHING
{Name}.Application     use-case services, interfaces (ports), DTOs/records, validators, catalogues, jobs
{Name}.Infrastructure  EF Core DbContext, repositories, interceptors, file-storage adapters, DI extension
{Name}.Web (or .Api)   composition root only: Program.cs, controllers, middleware, auth handlers
{Name}.Tests           xUnit + real SQL (LocalDB/Testcontainers) for repo/model tests
```

- Repository + service **interfaces live in Application**, implementations in Infrastructure.
- Business code never sees `DbContext`, `BlobServiceClient` or `HttpClient` directly.
- Infrastructure exposes one `services.AddXInfrastructure(configuration)` extension; Program.cs stays thin.

## 2. Entity base (keep, it's good)

From `ForekOnline.Domain/Shared`:

- `IIdentifiable<TKey>` → `TKey Id`
- `IAuditable` → `DateCreated`, `DateModified` (`DateTimeOffset`), `UserCreated`, `UserModified`
- `ISoftDeletable` → `IsDeleted`, `DateDeleted`
- `IEntity<TKey> : IIdentifiable<TKey>, IAuditable, ISoftDeletable` + `Code` (50), `Name` (250), `RowVersion`
- `EntityBase<TKey> : IEntity<TKey>` — implements all of the above, `[Timestamp] RowVersion`,
  identity-based `Equals`/`GetHashCode` (transient entities with default Id are never equal).
- `ITenantOwned` → `Guid TenantId` (only if the base is multi-tenant).
- `[SkipAuditInterceptor]` marker attribute for tables the interceptor must leave alone.

Improvements for the base:
- Make `Code`/`Name` opt-in (`INamedEntity`) rather than forcing them on every table.
- Default key `Guid`. ~~v7 via `Guid.CreateVersion7()` — sequential, index-friendly~~ **Corrected during the build:** v7 is
  *not* sequential in SQL Server's `uniqueidentifier` order, so the base uses SQL Server-sequential GUIDs (ADR 0003).
- **Only one entity base.** FO16 has a legacy `Base` class (string `CreatedOn`, `IsActive`, 27 entities) living
  beside `EntityBase` — never again.

## 3. EF Core conventions (keep)

- `AuditableEntityInterceptor : SaveChangesInterceptor` — on Added sets created/modified + user; on Modified
  sets modified and marks `DateCreated`/`UserCreated` as not modified; manages `DateDeleted`; assigns
  `Guid` Ids when empty; caches the skip-attribute check per type in a `ConcurrentDictionary`.
- `modelBuilder.ApplyEntityConventions(this)` at the END of `OnModelCreating` — reflection over every
  `IEntity<>`: row-version, `SYSDATETIMEOFFSET()` defaults, and ONE combined global query filter
  `!IsDeleted && (!HasResolvedTenant || TenantId == CurrentTenantId)`. Reason it's combined: `HasQueryFilter`
  **replaces** rather than adds, so separate filters silently drop tenant scoping. Back it with a model test.
- `SaveChanges` override: stamp `TenantId` on Added, throw on cross-tenant Modified/Deleted.
- `UseSqlServer(... EnableRetryOnFailure, CommandTimeout)`, sensitive logging only under DEBUG.

Improvements:
- Resolve the current user from `ICurrentUser` (claims), NOT by deserialising a session blob (FO16 reads
  `Session["SessionUser"]` JSON in the interceptor).
- Use `TimeProvider` (inject it) and store **UTC** `DateTimeOffset`; convert to local time only in the UI.
  FO16 uses `DateTimeHelper.GetCurrentSastDateTimeOffset()` and still has ~30 `DateTime.Now`.
- Use `IEntityTypeConfiguration<T>` classes + `ApplyConfigurationsFromAssembly`; no 1 600-line `OnModelCreating`.
- Keep `dotnet ef migrations` working from day one (FO16's snapshot went stale, forcing hand-written SQL
  migrations). Add a CI step that fails if the model has pending changes.

## 4. Repository + Unit of Work (keep the idea, tighten the shape)

What the user likes (`IRepository<T>` in Application, `Repository<T>` in Infrastructure):

```csharp
Task<IReadOnlyList<T>> GetAllAsync(Expression<Func<T,bool>>? filter = null, string[]? includeProperties = null,
    Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null, int? skip = null, int? take = null,
    bool asNoTracking = false, CancellationToken ct = default);
Task<T?> GetAsync(Expression<Func<T,bool>> filter, string[]? includeProperties = null, bool asNoTracking = false, CancellationToken ct = default);
Task<T> AddAsync(T entity, CancellationToken ct = default);
Task Update(T entity);  Task Attach(T entity);
Task<bool> RemoveAsync(T entity, CancellationToken ct = default);
Task<bool> ExistsAsync(Expression<Func<T,bool>>? predicate = null, CancellationToken ct = default);
Task<int> CountAsync(Expression<Func<T,bool>>? predicate = null, CancellationToken ct = default);   // DB-side counts
```
plus a private `BuildQuery(filter, includes, asNoTracking)` helper; `IUnitOfWork.SaveAsync()` commits.

Fix on the way into the base:
- **`RemoveAsync` must not call `SaveChangesAsync`** — only the Unit of Work commits. FO16's does, which
  breaks transactional grouping.
- **Soft delete by default**: `RemoveAsync` sets `IsDeleted = true` for `ISoftDeletable`; a separate
  `HardDeleteAsync` exists only where needed.
- Add projection overloads: `GetAllAsync<TResult>(filter, Expression<Func<T,TResult>> selector, ...)` and a
  `PagedResult<T>` (`Items`, `Total`, `Page`, `PageSize`) — FO16 needed DB-side counts/paging everywhere.
- Strongly-typed includes (`params Expression<Func<T,object>>[]`) alongside the string form.
- `Update`/`Attach` are sync → name them `Update(T)`/`Attach(T)` returning `void`, not `async Task` with no await.
- `ExecuteUpdateAsync` / `ExecuteDeleteAsync` passthroughs for bulk ops; raw SQL only via `FormattableString`
  (`ExecuteSqlInterpolatedAsync`) — never `ExecuteSqlRaw` with concatenated strings.
- `BeginTransactionAsync()` on the Unit of Work.
- **No God-UoW.** FO16's `IUnitOfWork` is 700+ lines with a property per table. In the base:
  `IUnitOfWork { IRepository<T> Repository<T>() where T : class; Task<int> SaveAsync(ct); BeginTransactionAsync(ct); }`
  with repositories cached per type, plus explicit specialised repos (`IStudentRepository : IRepository<Student>`)
  registered in DI only when they add real queries.
- Remove the half-built "To-be phased in 2025" block (`GetEntityAsync`, `SaveEntityAsync` — whose validity
  check is inverted — `ExecuteCommand`, `GetProvider`) and the `try { } catch (Exception ex) { throw; }` noise.

## 5. ValidationResponse (keep, formalise)

FO16 convention: bare ctor = success, `new ValidationResponse("message")` = error; callers branch on
`IsError`, never on `Message`. Fields: `IsValid`, `IsError`, `ErrorCode`, `ErrorDescription`,
`Errors` (collection of `ValidationError`), `Message`, `MessageTitle`, `ReturnCode`, `Data`.

Base version — same spirit, less ambiguity:
- Immutable with factories: `ValidationResponse.Success(message?)`, `.Success<T>(data)`, `.Failure(error)`,
  `.Failure(IEnumerable<ValidationError>)`, `.NotFound(...)`, `.Forbidden(...)`.
- Generic `ValidationResponse<T>` for the payload instead of `object Data`.
- One truth flag: `IsSuccess` (derive `IsError => !IsSuccess`); drop the overlapping `IsValid`/`IsError` pair.
- `ValidationError(Field, Message, Code)` so FluentValidation results map straight in and MVC can
  `ModelState.AddModelError(field, message)`.
- Extension to convert to `IActionResult` / `ProblemDetails` for API endpoints.

## 6. File storage — provider-agnostic, NEVER in the database

Keep the abstraction, drop the DB provider:
- `IFileStorageProvider { string ProviderName; UploadAsync(UploadFileRequest); DownloadAsync(fileId);
  DeleteAsync(fileId); GeneratePresignedUrlAsync(fileId, minutes); }`
- `IFileStorageProviderResolver` — dictionary of providers by name, default = first registered.
- `IFileUploadService` — façade: picks provider from settings (per tenant / per document type), and on
  download/delete looks up which provider the file lives in (so files survive a provider switch).
- Records: `UploadFileRequest(Stream, FileName, ContentType?, Metadata?, ProviderHint?, ExpiryDate?, TenantId?, DocumentType?)`,
  `UploadFileResponse(FileId, UploadTimestamp, ProviderName, FileSizeBytes, FileUrl?, AdditionalMetadata?)`,
  `DownloadFileResponse`.
- **Metadata row only in SQL** (`StoredFile : EntityBase<Guid>` — ProviderName, ProviderKey/object key,
  FileName, ContentType, SizeBytes, Sha256, MetadataJson, ExpiryDate, TenantId). Bytes live in object storage.
- Providers for the base: **S3-compatible** (covers Cloudflare R2 + AWS S3 + MinIO for local dev) and
  Azure Blob; `LocalDisk` only for dev/tests. **No `DatabaseFileStorageProvider`** — FO16's production DB
  filled up from DB-stored uploads (Sept 2026).
- Object keys: `{tenant}/{documentType}/{yyyy}/{MM}/{guid}{ext}`; never the user's filename.
- Access guard (from `IStoredFileAccess`): every download endpoint asks "may this user have this fileId for
  this purpose?" — never serve by raw fileId alone. Prefer short-lived pre-signed URLs for downloads.
- Validate size + content-type + magic bytes on upload; compute SHA-256 (enables tamper checks / dedupe).

## 7. Settings catalogue (keep — excellent pattern)

`SettingCodes` (constants) + `SettingCatalog` (one `SettingDefinition` record per setting: Code, Name,
DataType, DefaultValue, Category, Description, Scope Tenant/Platform, Allowed/Min/Max, IsSecret) +
`ApplicationSetting` table + `IApplicationSettingService` (memory-cached). A startup **seeder** inserts
missing catalogue entries and refreshes descriptions but **never overwrites an admin's value**.
Adding a setting = constant + catalogue entry, **no migration**. Secrets: never in this table in plain
text — reference Key Vault / user-secrets.

## 8. Permissions (keep — excellent pattern)

- `PermissionCodes` nested static classes of constants (`Module.Thing.Action`) — misspelt string keys
  silently deny everyone, so never gate on literals.
- `PermissionCatalog` of `PermissionDefinition(Key, Label, Module, Description, GrantedByDefaultTo[], Implies[])`;
  a test asserts codes ↔ catalogue match both ways. Declare a permission only when code enforces it.
- `PermissionPolicyProvider` turns `[Authorize(Policy = "perm:X")]` into a policy on the fly +
  `PermissionAuthorizationHandler` + `IPermissionSet` (per-request, cached).
- Tag helpers that hide links/elements the user can't use (HTTP-method aware); `NavCatalog` drives menus.
- Roles are data; permissions are grantable and default to Admin — never hard-code "PlatformAdmin only".
- Resource-ownership attributes (`[OwnLearnerRecord]`-style) for "users may only see their own record".

## 9. Cross-cutting web pieces (keep)

- **ExceptionMiddleware**: ticket number on the error page, correlation id, structured log, request
  query/body **redaction** (`RequestRedaction`), alert email **throttled per fingerprint**, recorded as an
  operational event so a System Health page can group errors. Base: emit `ProblemDetails` for API routes.
- **TenantResolutionMiddleware** (if multi-tenant): host → verified tenant domain; optional default-tenant
  fallback that logs loudly; 404 for unknown hosts; `/health` bypass. `ITenantContext` scoped.
- Rate limiter on auth endpoints; hashed, expiring reset tokens; PBKDF2/ASP.NET `PasswordHasher`.
- `PrincipalFreshness` cookie validation (role/permission changes take effect without logout).
- Hangfire with SQL storage, dashboard behind an auth filter, recurring jobs in UTC, configurable queues/workers.
- `PlaceholderText.Clean()` — blank out "N/A", "NONE", "-", etc. on optional fields at save time.
- Public `/verify/{code}` pattern (QRCoder + SHA-256 register) for any generated document.
- Health checks endpoint; System Health / operations dashboard (DB size forecast, backups, failed jobs).

## 10. Testing (keep)

- xUnit only (FO16 mixes xUnit + MSTest — pick one).
- Integration tests on a throwaway real SQL DB per test class (`EnsureCreated`), not EF InMemory —
  InMemory ignores query filters/row-version semantics the base relies on.
- Model tests: tenant filter present on every `ITenantOwned`; permission catalogue ↔ codes; setting catalogue.
- The solution must **build with zero errors and tests green on main** (FO16 HEAD carries 12 known failures).

## 11. NEVER bring these into the base

1. Session-stored user objects (`Session["SessionUser"]` JSON, `Helper.loggedInUser`) — use claims +
   `ICurrentUser`. Session is for transient UI state only.
2. A static `Helper` god-class; `ViewBag` as the data contract (133 uses) — use typed view models.
3. File bytes in SQL tables.
4. A second/legacy entity base class or string-typed dates.
5. Local-time (`DateTime.Now`, SAST-baked) timestamps in storage.
6. The God `IUnitOfWork` with one property per table; repos that call `SaveChanges` themselves.
7. Hand-written migrations because the snapshot rotted; `script.sql` dumps at repo root.
8. Connection strings / secrets in committed `appsettings.json` (FO16's has pointed at production) — use
   user-secrets locally, environment/Key Vault in prod, and a LocalDB/Docker default in `appsettings.Development.json`.
9. Pre-release / mismatched packages (`8.0.0-rc.*`, `Microsoft.AspNet.Mvc 5.3` in a Core app, mixed 8/9/10 versions).
   Use `Directory.Packages.props` (central package management) + `Directory.Build.props`
   (`Nullable enable`, `TreatWarningsAsErrors`, `ImplicitUsings`).
10. `catch (Exception ex) { throw; }` blocks; swallowing exceptions in `catch { }`.
11. Foreign keys to data that lives in another system (FO16 employees come from BaseAPI) — store the external
    Guid + a name snapshot.
12. Non-mapped "bridge" ids (`[NotMapped] StudentId`) and legacy source-system ids reused as local FKs.
13. Firing two DB-touching services with `Task.WhenAll` on one scoped `DbContext`.
14. Heavy licensed report engines (Stimulsoft, Syncfusion, SelectPdf) as base dependencies — add per project.
15. Repo-root clutter (brochures, `.py` generators, `.pdf`) — keep docs in `/docs`.
16. File header banners with author/date per file — git already records that.

## 12. Suggested base package set (.NET 10)

EF Core 10 (SqlServer), FluentValidation, Hangfire (optional module), Serilog or built-in logging +
OpenTelemetry, AWSSDK.S3 (R2/S3/MinIO), Azure.Storage.Blobs (optional), QRCoder, xUnit + Testcontainers.MsSql,
Mapster or hand-written mapping (avoid AutoMapper's new commercial licence from v15).

---

## 13. Addendum — verified against `tumiterm/FO` (HEAD `10ee8b9`, 7 Jun 2026)

The GitHub copy of FO is an **older snapshot** than the FO16 this draft was harvested from. Sections 2–6 and 9
(entity base, interceptor, conventions, repository, ValidationResponse, file storage, ExceptionMiddleware,
Hangfire) were confirmed in the code. The settings catalogue, permission catalogue, tenant resolution,
`PlaceholderText`, `PrincipalFreshness` and `RequestRedaction` (sections 7–9) are **not in that repo**. Take them
from the newer FO16 source when building the base.

### Also worth carrying over

- **`IMapFrom<TEntity>` / `IMapTo<TEntity>` + `ManualMapper`** — explicit, compile-checked hand mapping
  (`ToViewModel`, `ToViewModelList`, `ToEntity`, `ApplyTo`). This is the "hand-written mapping" choice from §12;
  use it as the default and drop AutoMapper.
- **DB-backed job queue**: `IBackgroundJobQueue.TryClaimNextAsync(queue, workerId, lockDuration)` + 
  `IBackgroundJobHandler { string JobType; HandleAsync(payloadJson, ct) }` + a drain job that a recurring Hangfire
  job runs. It's a simple outbox: the request writes a row in the same transaction, and a worker claims the row
  with a lease. Keep it, and add retry count/backoff and a dead-letter state.
- **Subscription/licence gate middleware** (`TenantSubscriptionMiddleware`): active → pass, grace period →
  banner via `HttpContext.Items`, expired → redirect; excludes auth/static paths. Make it an optional module.
- **Hangfire config from settings** (`Hangfire:WorkerCount|ServerName|Queues|Dashboard:Enabled|Route`), with
  `CompatibilityLevel.Version_180`, recommended serializer settings and `DisableGlobalLocks`.
- **ExceptionMiddleware details**: response-already-started guard, `X-Correlation-ID` header round-trip,
  exception chain (inner exceptions) in the alert, API-vs-HTML detection (`/api` prefix or `Accept: json`).
- `[SkipAuditInterceptor]` is honoured by **both** the interceptor and `ApplyEntityConventions` — keep that
  symmetry.

### Defects found there that the base must fix

- `ExceptionMiddleware` sends the alert email **fire-and-forget (`_ = SendAsync(...HttpContext...)`)**, so it uses
  `HttpContext` after the request has ended. Capture a plain DTO first and queue it (Channel/job). It also sets a 500 and
  then `Redirect` (which makes it a 302). Use `UseExceptionHandler` + `IExceptionHandler` (.NET 8+) and
  `UseStatusCodePagesWithReExecute`. HTML-encode values in the email, and keep the "production only" guard
  (it's commented out in FO).
- Ticket numbers come from a timestamp (`T-yyyyMMddHHmmssfff`), so two errors in the same millisecond collide. Use
  `Activity.Current.TraceId`, or a short random suffix.
- `RowVersion` exists on `EntityBase`, but `[Timestamp]` is commented out. The base should configure the
  row version in one place (the convention) and add a test that checks it's a concurrency token.
- `ApplyEntityConventions` assigns `HasQueryFilter` per entity. Any later `HasQueryFilter` call replaces it
  (see §3), so the model test is mandatory, not optional.
- **Two different `ValidationResponse` classes** exist (Web `ValidationAttributes` and Application `Validations`)
  with different shapes. The base has exactly one, in Application.
- `public static string loggedInUser` in `Helper` holds per-user state in a **static field shared by every request**
  (users see each other's name). `CurrentUserMiddleware` copies it into Session. Never use static mutable state for
  request data.
- `EncryptionHelper` hard-codes the AES key **and a fixed IV** in source. The base uses ASP.NET Core
  **Data Protection** (`IDataProtector`) for reversible protection and `PasswordHasher` for passwords.
- Third-party **licence key hard-coded in `Program.cs`** (Syncfusion). Licence keys are secrets → configuration.
- **Real credentials committed** in `appsettings*.json` (storage account key, prod/test SQL logins, SMS API
  key, JWT password). They're in git history, so **rotate them**. Also add secret scanning (gitleaks / GitHub
  push protection) to the base's CI.
- `AddJsonFile($"appsettings.{Env}")` is missing `.json`, so it silently never loads. It's redundant anyway, because
  `WebApplication.CreateBuilder` already loads env files. Don't re-add configuration sources by hand.
- `Console.WriteLine(EnvironmentName)` in Program.cs. Use the logger.
- Startup does work inline in Program.cs (`EnsureCreatedAsync` on a SQLite cache, the settings seeder). In the base,
  seeders run as `IHostedService`/startup tasks behind one `app.RunStartupTasksAsync()`, and **never `EnsureCreated`
  outside tests**.
- `IDesignTimeDbContextFactory` is missing, along with anything that keeps the snapshot current. That's the root cause of §11.7.
- ~40 one-by-one `AddScoped<IX, X>()` lines in Program.cs. Each layer registers its own services
  (`AddApplication()`, `AddInfrastructure(config)`, `AddWeb()`).
- Sliding session timeout of 10 h; `UseSession()` comes **after** auth and custom middleware that may read it.
  Pipeline order in the base: ExceptionHandler → HSTS/HTTPS → StaticFiles → Routing → RateLimiter →
  Authentication → Authorization → (Session if used) → tenant/subscription → endpoints.
- `Mapping` interfaces live in the Application project but use the `ForekOnline.Domain.Mapping` namespace.
  Namespaces must match the folder and project (enforce with an `.editorconfig` rule / IDE0130).
- Test project targets packages from four eras (ASP.NET Core 2.3 / 5.0 packages, MSTest 4 + xUnit 2, Moq).
  The base uses xUnit v3 + NSubstitute or Moq (pick one), with no ASP.NET Core 2.x package references.

---

## 14. Implementation status (scaffold, Oct 2026)

Implemented in this repository. See `docs/architecture.md` for how the pieces fit together, and `docs/adr` for the reasons.

| Blueprint item | Where |
| --- | --- |
| §1 four projects + tests | `src/*`, `tests/*` (unit, architecture, integration) |
| §2 entity base, opt-in `INamedEntity`, `ITenantOwned`, `[SkipAudit]` | `Base.Domain/Common` |
| §3 interceptor, conventions, `TimeProvider`/UTC, configurations, migrations + drift check | `Base.Infrastructure/Persistence`, CI |
| §4 repository/unit of work with every listed fix | `IRepository`, `Repository`, `UnitOfWork` |
| §5 immutable `ValidationResponse<T>` + MVC/API mapping | `Application/Common/Results`, `Web/Results` |
| §6 provider-agnostic storage (S3/R2/MinIO, Azure Blob, LocalDisk), guard, magic bytes, SHA-256 | `Application/Files`, `Infrastructure/Files` |
| §7 settings catalogue + seeder | `Application/Settings`, `SettingsSeedTask` |
| §8 permission catalogue, policy provider, tag helper | `Application/Security`, `Web/Security`, `Web/TagHelpers` |
| §9 exception handling, tenancy, rate limit, principal freshness, Hangfire, `PlaceholderText`, health | `Web/*`, `Infrastructure/*` |
| §10 xUnit only, real SQL, model/catalogue tests | `tests/*` |
| §11 "never" list | `BannedSymbols.txt`, analyzers, architecture tests, CLAUDE.md |
| §12 package set | `Directory.Packages.props` |
| §13 FO defects | addressed. Ticket from the trace id; alerts are queued, not fire-and-forget. |

Added beyond the blueprint: transactional outbox; named query filters; fail-closed tenancy; Data Protection keys in SQL;
CSP and security headers; correlation ids; OpenTelemetry; HybridCache; startup tasks; CI with secret scanning, CodeQL
and Dependabot; a Dockerfile (chiseled image) and docker-compose; `scripts/new-project.cs`.

Not yet built (candidates for the next round): the public `/verify/{code}` document register (QRCoder), the System
Health / operations dashboard UI, the subscription/licence gate module, the purge job for soft-deleted files, and an
Aspire AppHost.

**Round 2 (Oct 2026): user and role management.** Admin screens and API for users and roles; invite-only onboarding;
forgot, reset and change password; escalation guard; last-administrator protection; NIST password policy (ADR 0009).

**Round 3 (Oct 2026): audit trail.** Interceptor-written, same-transaction change history with before/after values,
redaction, append-only entries, tenant isolation, retention, the `/admin/audit` screen and API (ADR 0010).

**Round 4 (Oct 2026): development without Docker.** LocalDB fallback on Windows, dev admin seed, LocalDB fallback for tests.

**Round 5 (Oct 2026): notifications.** Email, SMS, WhatsApp and in-app behind one service. Pluggable providers
(SMTP, SendGrid, Twilio, Clickatell, BulkSMS, Meta). Channels switched on individually. Outbox delivery with retry
and fallback, preferences, file templates with tenant overrides, delivery log, inbox (ADR 0011).
