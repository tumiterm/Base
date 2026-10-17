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
