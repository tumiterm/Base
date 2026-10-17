# 0017: Per-tenant lookup lists from a code catalogue, and People as the sample module

**Context.** Every application has small lists people pick from: titles, departments, categories, reasons. FO kept
them in ad-hoc tables per list, with hard-coded ids in code and no way for a client to adjust them. Developers starting
a module from the base also had no complete example to follow, so each module invented its own shape.

**Decision.**
- **One `LookupValue` table, lists declared in code.**
  - `LookupLists` constants and `LookupCatalog` entries name each list and its starting values. Each value has a
    stable code that code may rely on.
  - Values are tenant-owned. Every tenant gets the starting values on startup and when it is created
    (`TenantCreated` handler), idempotently, so a list added later reaches existing tenants.
  - Tenants then rename, reorder, hide and add values at Administration → Lookup lists (`MasterData.Manage`).
  - Records store codes, not ids or names. Renaming relabels everywhere; a hidden value stays valid on records that
    already hold it (`EnsureValidAsync` with `existing`) but can't be chosen again.
  - Values are never deleted, so the history always reads.
  - Reads are cached per tenant.
- **Pickers from the design system:** `asp-lookup` fills a select (with the current value even if hidden), `data-combobox`
  makes it searchable, `_LookupChips` handles multi-select. `/api/lookups/{list}` serves other clients.
- **People is the reference module** ([vertical-slice.md](../vertical-slice.md)): entity, service, validation, lookups,
  export/import, linked documents, audit history, photo, API, list/detail screens and tests, using each convention once.
- **Audit values:** lists are recorded as "a, b" and dates as ISO `yyyy-MM-dd`. Before this change, lists were
  recorded as the .NET type name and dates in a US format.

**Consequences.** A new list is two lines of code plus its defaults. No migration is needed, and tenants get it on the
next start. Lists that need extra columns (a rate, a parent) outgrow this and become their own entity. Projects that
don't want the sample delete it by the checklist in the guide.
