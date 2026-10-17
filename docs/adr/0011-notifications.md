# 0011: One notification service, pluggable providers, outbox delivery

**Context.** Every project needs to reach people by email, and most also by SMS, WhatsApp or in the app. The
providers differ per client: SMTP, SendGrid, Twilio, Clickatell, BulkSMS, Meta. Sending from inside a request couples
the user's action to a slow or failing gateway. A link-bearing email must not leave its token in a database table.

**Decision.**
- One port, `INotificationService`, takes a template code, a recipient and data. Channels, providers and wording are
  configuration, not code.
- Channels are switched on one by one. Providers are named instances (type + settings); each channel has a default,
  and a tenant can choose another. Projects add providers by implementing `INotificationProvider`.
- Delivery goes through the outbox: queued in the caller's transaction, retried on transient errors, then on to the
  next fallback channel. Every attempt, skip and failure is recorded and can be resent.
- Wording comes from versioned template files, with per-tenant overrides in the database. WhatsApp sends only
  Meta-approved templates. User preferences are respected except for mandatory (security) templates.
- Sensitive templates are sent immediately and stored without content.

**Consequences.** A new provider is one class and a config section. Delivery is at least once: a crash between
"provider accepted" and "row marked sent" can send a message twice. Delivery receipts (webhooks for delivered and
read) and bulk campaigns are not covered yet. Provider integrations are pinned by request-shape tests; check each one
once against a live account before relying on it.
