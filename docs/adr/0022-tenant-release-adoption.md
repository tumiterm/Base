# 0022: Tenants adopt releases at their own pace

**Context.** All tenants run the same build, but not every organisation wants a change the day it ships: a new
screen, a changed workflow or a new default can disrupt a team in the middle of its month end. Branching or deploying
per tenant would multiply the code and the servers. The platform also had no way to say which organisations had seen
which changes, or how far behind any of them were.

**Decision.**
- **One build, gated behaviour.** A change that alters what people see or do is a `ReleaseFeature` in
  `ReleaseCatalog`: a code (`ReleaseFeatureCodes`), the product version that introduced it, and a rollout. `OptIn`
  features stay off until the tenant takes their release; `OnByDefault` features are on for everyone unless a tenant
  turns them off. A feature can also need a plan feature (`PlanFeature`), so plans and releases don't conflict.
  Fixes are never gated.
- **Each tenant has an adopted release.** `Tenant.ReleaseVersion` is the last release the tenant took. New tenants
  start on the current release. Taking a release (Administration → What's new) moves the version forward and turns
  on everything introduced up to it, clearing any earlier choices for those features.
- **Choices per feature.** `TenantFeatureChoice` records a tenant turning a feature on early or off. `ReleaseRules`
  decides a feature's state in one order: not in the plan → retired (always on) → newer than the running build (off)
  → the tenant's choice → adopted → the rollout default. The rules are pure functions with unit tests.
- **Behind is a number.** The pending releases are the distinct versions of non-retired features introduced after the
  adopted version and no later than the running build. `Behind` is how many there are; a tenant with nothing pending is
  on the current release, whatever its stored version says, so patch releases with no gated features never make
  anyone look behind.
- **A support window.** The platform setting `Releases.MaxReleasesBehind` (0 = no limit) caps how far a tenant may
  fall behind; the daily `release-window` job moves anyone further behind forward to the oldest release still
  supported.
- **Enforcement.** `ITenantReleases.IsOnAsync(code)` in services, `[RequiresRelease]` on controllers or actions (pages
  get a "not on your release" page, APIs a 404 problem) and `asp-release` in views. State is cached per tenant in
  HybridCache under the `releases` tag and cleared on every change.
- **The platform sees the fleet.** Platform → Releases shows how many organisations are on each release, adoption of
  each feature, and who is behind; the tenants list and tenant pages show each one's release.
- **Retire, then delete.** Once a feature is on everywhere, set `Retired`; it no longer counts as pending and is
  always on. The next release deletes the old code path and then the catalogue entry.
- **The catalogue is empty in the Base.** The machinery ships; projects add entries for their own changes.

**Consequences.**
- One codebase and one deployment serve tenants on different releases; "Acme is two releases behind" is a fact on a
  screen, not a guess.
- Every gated feature keeps two code paths alive until it is retired. Gating only behaviour changes, and retiring
  promptly, keeps that cost small.
- Gated features need tests for both states; `IReleaseCatalog` is injectable so tests can supply their own catalogue.
