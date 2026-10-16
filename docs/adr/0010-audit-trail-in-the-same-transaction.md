# 0010: Audit trail written by an interceptor in the same transaction

**Context.** "Who changed this, and what was it before?" must have an answer once administration is delegated.
Audit code written by hand in each service gets forgotten. SQL Server temporal tables or CDC record values but not
the acting user or the tenant, and they are harder to query per record. An audit written after the commit (e.g. via
the outbox) can be lost.

**Decision.** An EF Core interceptor writes one `AuditEntry` per changed row in the same SaveChanges, so the audit
can't be skipped by forgetting it, and it commits or rolls back with the change. Everything `IAuditable` is audited
by default, plus Identity's role and claim tables. Opting out, or redacting values, is explicit. Entries are
append-only, tenant-filtered, and purged by a retention setting.

**Consequences.** Every write costs one extra insert per changed row. Bulk `ExecuteUpdate`/`ExecuteDelete` bypass the
change tracker and are **not audited**: audit them explicitly, or avoid them for business data. Personal data copied
into the audit table follows the retention setting; mark sensitive fields `[AuditRedacted]`.
