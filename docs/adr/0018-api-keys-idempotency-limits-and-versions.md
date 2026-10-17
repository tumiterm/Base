# 0018: API keys per client, idempotency keys, per-caller limits and header versioning

**Context.** The API was only usable by the application's own pages, through the sign-in cookie. Integrations
(billing, HR feeds, reporting) had no way in short of a person's password. Nothing stopped a retried call from
recording a payment twice, one noisy caller could starve the rest, and the API had no version story. The OpenAPI
document existed only in Development.

**Decision.**
- **API clients with keys** (`ApiClient`, Administration → API clients, `Api.Clients.Manage`).
  - Each client has a name, permissions (never more than its creator holds; platform permissions only in the platform
    tenant), an expiry (always; at most `Api.KeyLifetimeDays`) and an optional rate limit.
  - Keys are `bk_{prefix}_{secret}`. The prefix finds the client and the full key is checked against its SHA-256 hash
    in constant time. The key is shown once, in the response to the post: never in a URL, TempData or a cache.
  - A policy scheme picks the cookie or the `ApiKey` handler per request. Keys work only on `/api` and only in their
    tenant. Callers by key carry no cookie, so the antiforgery check (otherwise on every unsafe call) skips them.
  - API access is a plan feature (`Feature.Api`).
  - The creator is reminded two weeks before expiry (job + outbox notification). Rotation and revocation are
    immediate: the client cache is dropped.
- **Idempotency-Key** on any unsafe `/api` call. The first response (non-5xx, up to 256 KB) is stored per tenant and
  caller for `Api.IdempotencyHours`. A retry replays it; the same key with a different body is 422; a concurrent
  repeat is 409. A unique index makes racing retries safe. An hourly job deletes expired records. This is stored
  in the database rather than a cache so that it survives restarts and works across instances.
- **Rate limits per caller** (API client, person, or IP for anonymous calls), as a token bucket with a minute's burst
  refilled every second. Over the limit is 429 with `Retry-After`. The limiter now runs after authentication, so it
  knows who is calling.
- **Versioning with Asp.Versioning** (header or query `api-version`). Unversioned calls are 1.0, so nothing breaks.
  The supported versions are reported. URL segments (`/api/v1`) were rejected: every existing route and client would
  change for no gain today.
- **OpenAPI per version**, with the key scheme, the `Idempotency-Key` header and the shared 401/429 responses
  documented. `Api:Documentation` is `Off`, `SignedIn` or `Public`: off by default outside Development.
- **CORS** is closed unless `Api:AllowedOrigins` lists origins, and credentials are never allowed cross-origin.

**Consequences.**
- Integrations get least-privilege, expiring, revocable credentials and safe retries without extra code in each
  endpoint.
- Two new tables (`ApiClients`, `IdempotencyRecords`) and two jobs.
- Per-client limits live in each instance's memory. A deployment with many instances gets a limit per instance,
  which is acceptable for fairness but not a billing-grade quota.
- Webhooks (pushing events out) are not part of this decision.
