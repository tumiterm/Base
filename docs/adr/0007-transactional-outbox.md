# 0007: Transactional outbox for domain events

**Context.** Side effects (emails, integrations) triggered inside a request either run before the commit (and can
fire for a rolled-back change) or are lost if the process dies after the commit.

**Decision.** Entities raise domain events. An interceptor stores them as `OutboxMessage` rows in the same transaction.
`OutboxProcessor` claims due rows with a lease, dispatches them to `IDomainEventHandler<T>` in the original tenant,
retries with exponential backoff, and dead-letters after the maximum number of attempts.

**Consequences.** Delivery is at least once, so handlers must be idempotent. Delivery latency is roughly the poll interval.
