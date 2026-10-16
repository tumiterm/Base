# 0002: Generic repository and a small unit of work

**Context.** FO's `IUnitOfWork` grew a property per table (700+ lines), and its repositories called `SaveChanges`
themselves, which broke transactional grouping.

**Decision.** `IRepository<T>` stages changes and never saves. `IUnitOfWork` exposes `Repository<T>()`, `SaveAsync`
and `ExecuteInTransactionAsync` (inside EF's execution strategy, so connection retries stay valid). Specialised
repositories are added only when they carry real queries.

**Consequences.** One commit point per use case. Complex reads can still use a specialised repository with full EF Core.
