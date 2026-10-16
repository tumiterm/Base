# 0003: SQL Server-sequential GUID keys, not Guid v7

**Context.** The blueprint proposed Guid v7 as "sequential, index-friendly". That holds for databases that compare
GUIDs byte by byte. SQL Server compares `uniqueidentifier` starting with bytes 10–15, so v7's leading timestamp
counts for nothing there and inserts land at random positions in the clustered index.

**Decision.** Use EF Core's SQL Server sequential GUIDs, assigned on add. Use `SequentialGuid.Next()` (same layout)
when an id is needed before the entity is added. `SequentialGuidTests` checks both claims with `SqlGuid`, which sorts like SQL Server.

**Consequences.** Inserts append to the index. Ids are not time-sortable as text; use `DateCreated` for ordering.
If the base ever targets PostgreSQL, switch the generator to v7.
