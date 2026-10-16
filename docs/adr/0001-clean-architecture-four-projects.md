# 0001: Clean Architecture in four projects

**Context.** In FO, business code depended on `DbContext`, session objects and a static `Helper`, so it was hard to test and to change.

**Decision.** Use four projects: Domain ← Application ← Infrastructure ← Web. Ports live in Application and adapters in Infrastructure.
Web is only the composition root. Architecture tests enforce the direction.

**Consequences.** Business code is testable without a database or web host. Adding an adapter (e.g. a new storage
provider) never touches Application. There are slightly more files per feature.
