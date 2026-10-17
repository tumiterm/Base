# 0016: Tenants, plans and subscriptions in the base, with no payment gateway

**Context.** Tenancy existed (host resolution, the `Tenant` filter), but nothing created or ran tenants: no screens,
no provisioning, no idea of what a tenant had paid for. Every SaaS built on the base would rebuild the same things:
plans, trials, a lifecycle, limits, and gating modules by plan. Roles are shared by all tenants, so a tenant
administrator with `Roles.Manage` could change permissions for every tenant.

**Decision.**
- **The default tenant is the platform tenant.** Its users can hold *platform permissions* (`IsPlatform` in the
  catalogue): `Platform.Tenants.View/Manage`, `Platform.Plans.Manage`, and now `Roles.Manage` (roles are shared).
  The claims factory drops platform permissions for every other tenant's users, whatever their roles say. The
  escalation guard ignores them outside the platform tenant, so a tenant can still manage its own administrators.
- **Tenant administration** (`ITenantAdministration`) creates a tenant in one step. It adds the tenant, its verified
  domain and its subscription in one transaction. Then, acting as the new tenant, it sets `General.PublicBaseUrl` to
  that domain and invites the first administrator, so the invitation link opens the right tenant. Domains can be
  added, verified and removed (the host index is filtered on not-deleted, so a host can be reused). Tenants can be
  deactivated (403 on every request).
- **Plans** are platform data: prices per cycle, trial days, and entitlements from `EntitlementCatalog`. A feature
  is on (1) or off (0); a limit is a maximum, with null meaning unlimited. Entitlement rows are updated in place,
  never deleted, so the audit trail shows every change. Changing a plan applies to its subscribers immediately.
  A retired plan keeps its subscribers but can't be chosen.
- **One subscription per tenant** (`TenantSubscription`). The lifecycle is
  Trialing → Active → PastDue → Suspended, or Cancelled / Expired. It lives in the entity:
  `RecordPayment`, `ExtendTrial`, `Suspend`, `Reinstate`, `Cancel(atPeriodEnd)`, `Resume`, and `Advance(now, grace)`.
  An hourly `IScheduledJob` sends the trial reminder (`Subscriptions.TrialReminderDays`) and advances
  subscriptions: past due after an unpaid trial or renewal, suspended after `Subscriptions.GraceDays`. Status changes
  raise `SubscriptionStatusChanged`, and the tenant's contact is emailed through the notification templates.
- **No payment gateway.** A payment is recorded by an operator, or by a billing integration calling
  `POST /api/platform/tenants/{id}/subscription/payments`. An integration listens to `SubscriptionStatusChanged`
  to tell its billing system. Gateways differ per project and country; the lifecycle doesn't.
- **Entitlements are enforced where the action happens.** `ITenantEntitlements` (cached, invalidated on every
  plan or subscription change):
  - **Limits:** user invites check `Limit.Users`; `FileService` checks `Limit.StorageMb` before storing anything.
  - **Features:** `[RequiresFeature]` gates controllers (Documents, Import & export, Appearance) and `asp-feature`
    hides their navigation.
  - **Access:** `SubscriptionGateMiddleware` sends a suspended, cancelled or expired tenant's users to
    `/subscription` (402 for the API). Signing in and out still works.
  - **Unrestricted tenants:** a tenant without a subscription, such as the platform tenant or a single-tenant
    deployment, has every feature and no limits.

**Consequences.**
- A new SaaS starts with tenant onboarding, trials, plans and limits working, and a project adds its own features
  and limits to the catalogue.
- Single-tenant projects see none of it: no subscription means no limits.
- Tenants can't define their own roles. Per-tenant roles would need tenant-scoped role names in Identity and are
  left for later.
- Proration, invoices and taxes belong to the billing system, not here.
