# Changelog

Every change a project built on the Base would notice, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [SemVer](https://semver.org/):

- **Major**: a project has to change its own code to take the release (a public type, setting code, permission or
  configuration key renamed or removed; a rule tightened so existing code no longer builds or passes; a migration
  that changes existing columns).
- **Minor**: something new that existing projects can ignore (a module, a setting, a screen, an optional switch).
- **Patch**: fixes that change nothing a project relies on.

Add a line under **Unreleased** with every change. A release moves those lines under a new version heading, bumps
`BaseVersion` in `Directory.Build.props` and is tagged `vX.Y.Z` (see [ADR 0019](adr/0019-versioning-the-base.md)).

## [Unreleased]

### Added

- **Messages**: write to people by email, SMS, WhatsApp or in-app, from Messages in the navigation or "Send a
  message" on a person's or user's page.
  - Pick people one by one, send to everyone an audience's filters match (People by department, status or skill;
    Users by role, the audience for in-app), or paste or upload a CSV list whose columns become placeholders.
  - A formatting editor with placeholders ({{FirstName}}, {{Department}}...), an SMS text with its part count, and
    WhatsApp through an approved template. HTML is cleaned on the server.
  - Live reach (how many people, and how many on each channel), a preview filled in for the first person, and
    "Send a test to me".
  - Send now or at a set time; scheduled messages can be changed or cancelled, and any message can be sent again.
  - Each message's page shows who got it on which channel, and who was skipped and why.
  - Saved message templates. Duplicate accounts, emails and phone numbers get one message per channel.
  - Plan module **Messaging** (Send messages, Bulk messages, Messages per month), permissions Send messages and Send
    bulk messages, the "Announcements" notification preference, and the `scheduled-messages` job.
  - For projects: register an `IAudienceSource` to make a module's people reachable (ADR 0024).
- Plans are built from modules. Every feature and limit belongs to a part of the product (Organisation, Documents &
  files, Import & export, Branding, Integrations), arrives with a suggested tier (Standard for every plan, Premium for
  higher plans) and the version it came in. Platform > Plans > **Plans and features** shows every plan side by side
  with its price, lets a module be included in or left out of every plan at once, and saves everything together.
  Features added since a plan was saved are marked "Needs a decision" (they follow their suggestion until then), the
  plans list counts them, and "Accept the suggestions" decides them all. The plans list shows each plan as a card.
- New premium feature **Documents from templates** (`Feature.DocumentGeneration`): generating documents from
  templates, on the page and through the API, is now a plan feature of its own. Plans saved before it ask for a
  decision; until then they leave it out.
- `GET api/platform/plans/modules`, and the module, version and tier of each entry in `api/platform/plans/entitlements`.
- Organisations work on your own machine: Development runs multi-tenant, `localhost` opens the platform (new
  `Tenancy:DefaultTenantHosts`), and a domain such as `mtn.dev.localhost` opens its organisation at
  `https://mtn.dev.localhost:7262` with no hosts-file entry or certificate warning. The tenant screens explain this,
  and an unknown name says how to add it in Development.
- Invitation links can be copied, to send by WhatsApp, text or any other channel: a link icon next to everyone who
  hasn't accepted yet (Users, the user's page and the tenant's People), and `POST api/users/{id}/invite-link`. Each
  copy is a fresh single-use link; if the clipboard is blocked the link is shown on a page with WhatsApp and text
  buttons. Locking an account cancels every link handed out.
- Tenants adopt releases at their own pace. Changes listed in `ReleaseCatalog` can be held back until an organisation
  takes their release, tried early or turned off; Administration → What's new shows what is waiting, and a banner
  tells administrators when they are behind. Platform → Releases shows how many organisations are on each release
  and who is behind, and the tenants list shows each one's release. The `Releases.MaxReleasesBehind` setting and the
  daily `release-window` job keep everyone within a support window. Gate features with `ITenantReleases.IsOnAsync`,
  `[RequiresRelease]` or `asp-release` (ADR 0022). The Base's catalogue is empty.

### Changed

- The administration log of sent notifications is now called **Delivery log** in the navigation.
- The sidebar is shorter: administration links sit in three collapsible groups (Access, Configuration, Activity)
  that open on the current page and remember how they were left; the icon rail still shows every link.
- The System, Errors, error details, Roles, role, Messages (log, templates, template), Notifications, notification
  preferences, Audit trail, Import, import results, My exports, Generate a document and Change password screens are
  rebuilt on the design system.
- The audit trail names the records it lists (people, users, roles, documents) instead of showing their ids, and
  role assignments and permission changes appear under the user or role they belong to.

### Fixed

- Domain fields accept a pasted address (`https://mtn.dev.localhost:7262`) and keep the host name, with a clearer
  message when it isn't one; tenant addresses and links keep the platform address's port.
- Invitation links for another organisation's users open that organisation (its verified domain) instead of the
  platform's address, where they said the link was invalid. An organisation with no verified domain gets a clear
  message instead of a link that can't work, and welcome emails link to the organisation's own sign-in page.
- The user page no longer claims that resending an invitation cancels the earlier links; it says how to cancel them.
- Source folders named `Releases` are no longer ignored by git.
- Forms that list every validation message no longer trip the content security policy with an inline style.

## [1.0.0] - 2026-10-17

The first versioned release: everything the Base has today.

### Foundations

- .NET 10 solution in four layers (Domain, Application, Infrastructure, Web) with architecture tests that enforce
  the dependency rules; warnings are errors, analyzers on, NuGet audit, central package versions.
- `EntityBase` with audit columns, soft delete, row version and domain events; SQL Server-sequential GUID keys.
- `TimeProvider` everywhere and UTC `DateTimeOffset` storage; `DateTime.Now`/`UtcNow` banned at compile time.
- Repositories and `IUnitOfWork`, `ValidationResponse` results mapped to pages and problem details.
- `scripts/new-project.cs` creates a renamed copy; it now records the Base version it came from.

### Security and identity

- ASP.NET Core Identity with invitations (users set their own password), password reset, lockout and NIST-style
  password rules; current user from claims only.
- Permissions as data (`PermissionCatalog`, `[HasPermission]`), roles, and `PermissionGuard` so nobody grants what
  they don't hold.
- Strict content security policy (no inline script or style), security headers, antiforgery on every form.

### Tenancy and subscriptions

- Tenants resolved by domain, isolated by a named query filter that fails closed.
- Plans with features and limits, subscriptions with trials, grace periods, suspension and reinstatement; payments
  recorded from outside.
- Tenant pages list each tenant's users with their status, show a getting-started checklist (address,
  administrator, email delivery, first sign-in), and send the first administrator a welcome email with what to do
  next. Administrators can be invited and welcomes resent from the tenant.

### Data and audit

- Audit trail of every change with redaction (`[AuditRedacted]`) and noise filtering (`[NotAudited]`).
- Transactional outbox for side effects after commit, with idempotent handlers.
- Lookup lists for pick-lists, with defaults per tenant.

### Files and documents

- `IFileService` over local disk, S3 and Azure Blob, a capped database provider for small files, signature checks
  and pluggable upload checks.
- Versioned documents linked to any record, finalising, archiving, generation from templates.
- Documents open in the browser: PDFs, images, text and generated HTML are previewed on the page and in a drawer
  from any list (sandboxed, framed by the application only); other files offer a download.
- Linked records are shown by name ("Thandi Nkosi") instead of their id, through `IRecordLabelProvider`.

### Notifications

- `INotificationService` with templates per channel (email, SMS, WhatsApp, in-app), fallbacks, preferences,
  retries and a delivery log; providers for SMTP, SendGrid, Twilio, Clickatell, BulkSMS and Meta WhatsApp.
- Email can be set up on the settings screen (SMTP server, sign-in, sender) with a test email that reports the
  server's answer at once.

### Settings

- Settings by category on one screen, with typed editors, switches, defaults, "changed" markers and reset.
- Platform-wide settings are shown to, and can only be changed by, the platform tenant.
- Secret settings (the SMTP password) are encrypted with Data Protection, never shown again and kept out of the
  audit trail.
- New switches: password reset by email, document previews, and an announcement banner across every page.

### Data exchange and operations

- Exports (`IExporter`) and imports (`IImporter`) through existing use cases, in the background when large.
- Scheduled jobs with leases and history, health checks, error log with ticket numbers, system page.
- The running version is shown on the System page and in the account menu, and returned by `GET api/system/version`.

### API

- API clients with hashed keys, per-caller rate limits, `Idempotency-Key` replay, versioning and OpenAPI per version,
  CORS allowlist.

### Interface

- Design system (tokens, components, light and dark, phone layouts) and per-tenant branding.
- People sample module showing the vertical slice end to end.
- Settings, users, invitations, documents and tenant pages rebuilt on the design system; row menus inside tables
  are no longer cut off.
