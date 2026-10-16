# Base — .NET 10 starter solution

This repo is the reusable base for every new .NET 10 project. The full design memory, with what to keep, what to
improve and what is banned, is in **[docs/base-project-blueprint.md](docs/base-project-blueprint.md)**. Read it
before you change the solution's shape, add a cross-cutting feature or add a package.

## Non-negotiables (summary of the blueprint)

- Clean Architecture: `Domain` ← `Application` ← `Infrastructure` ← `Web`. Domain references nothing. Ports
  (repository/service interfaces) live in Application. Program.cs only composes (`AddApplication`,
  `AddInfrastructure`, `AddWeb`).
- One entity base: `EntityBase<Guid>` (Guid v7) with audit + soft delete + row version. `Code`/`Name` are opt-in.
- Audit/tenant/soft-delete come from the interceptor + `ApplyEntityConventions` (one combined query filter,
  backed by a model test). Current user comes from `ICurrentUser` (claims). Time comes from an injected
  `TimeProvider`, stored as UTC `DateTimeOffset`.
- Repositories never call `SaveChanges`; only `IUnitOfWork.SaveAsync` commits. Soft delete by default. No
  per-table UoW properties.
- Results use one immutable `ValidationResponse` / `ValidationResponse<T>` with `IsSuccess` and factories.
- File bytes go to object storage (S3-compatible / Azure Blob), never SQL. SQL holds a `StoredFile` metadata row. Every
  download goes through an access check.
- Settings and permissions are catalogue-driven (constants + catalogue + seeder + both-ways test). Never use string
  literals for permission checks.
- `dotnet ef migrations` must always work. CI fails on pending model changes.
- `Directory.Build.props` (Nullable, ImplicitUsings, TreatWarningsAsErrors) + `Directory.Packages.props`
  (central versions, no pre-release). The build has zero warnings and tests are green on `main`.
- Secrets never go in committed files: user-secrets locally, env/Key Vault in prod. Use Data Protection for reversible
  encryption.
- xUnit only. Integration tests run against real SQL (Testcontainers), not EF InMemory.

## Banned (see blueprint §11 and §13)

Session-stored users, static mutable request state (`Helper.loggedInUser`), static `Helper` god-classes,
`ViewBag` as a contract, `DateTime.Now`, hard-coded keys/IVs/licence keys, `catch (Exception) { throw; }`,
empty `catch { }`, fire-and-forget work that captures `HttpContext`, `ExecuteSqlRaw` with concatenation,
`EnsureCreated` outside tests, duplicate classes with the same name in different layers, per-file author/date
banners, licensed report engines as base dependencies, repo-root clutter.
