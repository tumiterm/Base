# 0004: Named query filters; tenancy fails closed

**Context.** EF Core used to allow one query filter per entity, and a second `HasQueryFilter` silently replaced the
first. FO also fell back to "no tenant = see everything".

**Decision.** Use EF Core 10 named filters `SoftDelete` and `Tenant`, applied by convention to `ISoftDeletable` /
`ITenantOwned`. When no tenant is resolved, the tenant id is `Guid.Empty`, which matches no rows, and inserts throw.
Cross-tenant work requires an explicit `BeginSystemScope()`.

**Consequences.** Forgetting tenant resolution shows up as an empty result or an exception, never as a data leak.
Background jobs must set their tenant (the outbox does).
