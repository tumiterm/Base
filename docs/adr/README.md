# Architecture decision records

Short records of decisions that shape the base. Add one when you make a decision a future reader would
otherwise have to reverse-engineer. Format: context → decision → consequences.

| # | Decision |
| --- | --- |
| [0001](0001-clean-architecture-four-projects.md) | Clean Architecture in four projects |
| [0002](0002-repository-and-small-unit-of-work.md) | Generic repository + small unit of work over EF Core |
| [0003](0003-sequential-guid-keys.md) | SQL Server-sequential GUID keys, not Guid v7 |
| [0004](0004-named-query-filters-fail-closed-tenancy.md) | Named query filters; tenancy fails closed |
| [0005](0005-files-in-object-storage.md) | File bytes in object storage, metadata in SQL |
| [0006](0006-catalogue-driven-permissions-and-settings.md) | Catalogue-driven permissions and settings |
| [0007](0007-transactional-outbox.md) | Transactional outbox for domain events |
| [0008](0008-real-sql-in-tests.md) | Integration tests on real SQL Server |
| [0009](0009-invite-only-onboarding-and-escalation-guard.md) | Invite-only onboarding, escalation guard, NIST passwords |
| [0010](0010-audit-trail-in-the-same-transaction.md) | Audit trail written by an interceptor in the same transaction |
| [0011](0011-notifications.md) | One notification service, pluggable providers, outbox delivery |
| [0012](0012-documents-and-capped-database-storage.md) | Documents on top of files, capped database storage, engine-free generation |
| [0013](0013-exports-and-imports.md) | Exports and imports through registered exporters and importers, run as the requester |
| [0014](0014-operations-and-health.md) | One job scheduler, dependency health checks, a recorded error log |
| [0015](0015-design-system-and-tenant-branding.md) | Own design system on Bootstrap's behaviour, per-tenant branding as generated CSS |
| [0016](0016-tenants-plans-and-subscriptions.md) | Tenants, plans and subscriptions in the base, with no payment gateway |
| [0017](0017-lookup-lists-and-the-sample-module.md) | Per-tenant lookup lists from a code catalogue, and People as the sample module |
| [0018](0018-api-keys-idempotency-limits-and-versions.md) | API keys per client, idempotency keys, per-caller limits and header versioning |
| [0019](0019-versioning-the-base.md) | SemVer for the Base, a changelog, tags, and projects that know their Base version |
| [0020](0020-secret-settings-and-email-from-settings.md) | A settings screen, encrypted secret settings, email set up from settings |
| [0021](0021-document-previews-and-record-labels.md) | Sandboxed in-browser previews and named record links |
| [0022](0022-tenant-release-adoption.md) | Tenants adopt releases at their own pace: gated features, choices and a support window |
| [0023](0023-plan-modules-tiers-and-decisions.md) | Plans built from modules, with suggested Standard/Premium tiers and recorded decisions |
| [0024](0024-messages-audiences-and-scheduling.md) | Messages people write go to audiences modules declare, now or at a set time |
