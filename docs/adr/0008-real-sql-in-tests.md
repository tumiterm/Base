# 0008: Integration tests on real SQL Server

**Context.** EF InMemory doesn't translate query filters to SQL, has no row versions, transactions or unique
indexes. FO also mixed xUnit and MSTest, and carried known failures on main.

**Decision.** Use xUnit v3 only. Integration tests start one SQL Server container (Testcontainers) per run and one database
per test class. Web tests use `WebApplicationFactory<Program>`. Main must be green.

**Consequences.** Running integration tests locally and in CI needs Docker. The suite takes about 30 seconds.
