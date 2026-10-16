# 0009: Invite-only onboarding, escalation guard, NIST passwords

**Context.** Administrators who set initial passwords know other people's credentials. Reset links built from the
request's `Host` header can be redirected to an attacker's domain. A delegated administrator with "manage users"
could otherwise hand out any role, including Administrator. Composition rules ("one digit, one symbol") push
people towards weak, predictable passwords.

**Decision.**
- Users are invited and choose their own password from an emailed, single-use, 72-hour link. Reset links last 1 hour.
  Tokens are generated when the outbox sends the email, so they are never stored, and links use the
  `General.PublicBaseUrl` setting.
- `PermissionGuard`: you can only grant, remove, or manage access you hold yourself. The last active administrator
  and the system roles are protected.
- Passwords: at least 12 characters, at least 4 distinct, no composition rules, lockout after 5 failures (NIST SP 800-63B).

**Consequences.** Onboarding needs working email (the dev fallback only logs it). Set `General.PublicBaseUrl` per
environment, and per tenant in multi-tenant deployments. A breached-password check (k-anonymity lookup) is the
natural next hardening step.
