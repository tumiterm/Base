# Base: .NET 10 starter solution

This repo is the reusable base for every new .NET 10 project. Read these before changing structure, adding a
cross-cutting feature or adding a package:
- [docs/architecture.md](docs/architecture.md): how it fits together (layers, pipeline order, persistence, tenancy)
- [docs/adr/](docs/adr/): why it is built this way
- [docs/base-project-blueprint.md](docs/base-project-blueprint.md): what was kept from FO16, and what is banned

## Commands

- Build: `dotnet build` (warnings are errors; NuGet audit fails on vulnerable packages)
- Test: `dotnet test` (all), `dotnet test --project tests/Base.UnitTests` (one). Integration tests need Docker.
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
  New setting = constant in `SettingCodes` + `SettingCatalog` entry (no migration). Secrets are never settings.
- Files only through `IFileService`: bytes go to object storage, never SQL.
- Side effects after a commit go through domain events + `IDomainEventHandler<T>` (outbox; handlers idempotent).
- Every schema change ships a generated migration. Never hand-write migrations or edit the snapshot.
- Package versions live only in `Directory.Packages.props`. No pre-release packages.
- Integration tests use real SQL Server (Testcontainers), never EF InMemory. Main stays green.

## Banned (blueprint §11, §13)

Session-stored users and static mutable request state; static `Helper` god-classes; `ViewBag` as a contract;
local-time timestamps; hard-coded keys, IVs or licence keys; secrets in committed config;
`catch (Exception) { throw; }` and empty `catch { }`; fire-and-forget work that captures `HttpContext`; string-built SQL;
`EnsureCreated` outside tests; duplicate type names across layers; per-file author/date banners; licensed report
engines as base dependencies; repo-root clutter (documents and generators go in `/docs` or `/scripts`).
