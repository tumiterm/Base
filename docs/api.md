# The API

Every screen's use cases are also available under `/api`, as JSON with RFC 9457 problem details for errors. This page
is for whoever builds an integration; the reference for each endpoint is at `/scalar` (and `/openapi/v1.json`) when
`Api:Documentation` allows it.

## Authenticating

| Caller | How | Notes |
|---|---|---|
| Another system | `Authorization: Bearer bk_…` or `X-Api-Key: bk_…` | Create an API client at Administration → API clients (`Api.Clients.Manage`; needs the plan's *API access* feature). |
| The application's own pages | The sign-in cookie, plus the antiforgery token as `X-CSRF-TOKEN` on unsafe calls | What `site.js` does. |

- **What a client can do.** A client acts with the permissions it was given, never more than its creator held. It acts
  only in its tenant: a key used on another tenant's address is invalid. Its changes are audited as "API: name".
- **Keys always expire** (at most `Api.KeyLifetimeDays`). The creator is reminded two weeks before.
- **Rotating** issues a new key and kills the old one at once. **Revoking** is permanent.
- **Storage.** Only a SHA-256 hash of the key is stored, and the key is shown once.
- **Errors:** 401 means missing, invalid, revoked or expired. 403 means the client lacks the permission, or the plan
  has no API access.

## Retrying safely: `Idempotency-Key`

Send a unique value (a UUID) with any POST, PUT, PATCH or DELETE. If the call times out, retry it with the **same**
key:

| Situation | Result |
|---|---|
| The first call finished | The retry gets the same status and body, with `Idempotent-Replayed: true`. Nothing runs twice (no second payment, no duplicate person). |
| The first call is still running | 409 with `Retry-After: 1`. |
| Same key, different request | 422. Use a new key for a new request. |
| The first call failed with a server error | Nothing is stored, so the retry runs. |

Keys are remembered per caller for `Api.IdempotencyHours` (24 by default). Requests with a key can be at most 1 MB.

## Limits

- **API clients:** each has a token bucket of its own limit per minute (default `Api.DefaultRequestsPerMinute`).
- **People and anonymous callers:** limited by `Api:UserRequestsPerMinute` and `Api:AnonymousRequestsPerMinute`.
- **Over the limit:** 429 with `Retry-After` (seconds). Back off and retry.

## Versions

Calls without a version are 1.0. Send `api-version: 1.0` (header or query string) to pin one. Responses list
`api-supported-versions` (and `api-deprecated-versions` once there are any). An unsupported version is 400.

Adding version 2 of an endpoint: put `[ApiVersion("2.0")]` on a new controller (or `[MapToApiVersion]` on an
action). Keep 1.0 until clients have moved, mark it deprecated with `[ApiVersion("1.0", Deprecated = true)]`, and the
OpenAPI document gets a `v2` alongside `v1`.

## Calling from a browser on another site (CORS)

Off by default: the API is meant for servers, and the application's own pages are same-site. To let a web app on
another origin call it, list the origins in `Api:AllowedOrigins`. Credentials (cookies) are never allowed cross-site,
so such an app authenticates with a key. That only suits a back-end-for-front-end, never a key in browser code.

## Configuration

| Key | Default | |
|---|---|---|
| `Api:Documentation` | `Off` (`Public` in Development) | `Off`, `SignedIn` or `Public`: who can read `/openapi/v1.json` and `/scalar`. |
| `Api:UserRequestsPerMinute` | 1200 | Per signed-in person. |
| `Api:AnonymousRequestsPerMinute` | 120 | Per IP, for calls that aren't signed in. |
| `Api:AllowedOrigins` | none | CORS origins. |
| Setting `Api.DefaultRequestsPerMinute` | 600 | Per API client without its own limit. |
| Setting `Api.KeyLifetimeDays` | 365 | The longest a key can be valid. |
| Setting `Api.IdempotencyHours` | 24 | How long responses are kept for replay. |
