# 0020: Settings screen, encrypted secret settings and email set up from settings

**Context.** Settings existed (`SettingCatalog`) but could only be changed through the API, so administrators had no
screen for defaults and switches. Email went to the log until someone edited configuration on the server, and the
rule "secrets are never settings" meant an SMTP password could only live there. Operators expect to set up email
from the application. Platform-wide settings could also be changed from any tenant through the API.

**Decision.**
- **A settings screen** (Administration → Settings) with every catalogued setting by category: typed editors
  (switches, numbers with limits, choices, colours, time zones), the default, a changed marker and a reset. Saving a
  form is all or nothing, and values equal to the current one aren't written, so a tenant doesn't turn inherited
  values into overrides by pressing Save. Branding stays on the Appearance page.
- **Platform-scope settings belong to the platform tenant.** Only it sees them on the screen, and the service refuses
  changes from anywhere else.
- **Secret settings** (`SettingDataType.Secret`, platform scope only) are for credentials an operator types in. They
  are stored encrypted with Data Protection under their own purpose, in a separate `[AuditRedacted]` column, never
  returned by `GetAllAsync`, and can only be replaced or removed. Deployment secrets (connection strings, signing
  keys, provider API keys) still belong in configuration. In production, protect the Data Protection key ring
  (certificate or Key Vault), or a database copy holds everything needed to decrypt.
- **Email from settings.** `Email.Smtp.*`, `Email.FromAddress` and `Email.FromName` describe an SMTP server. Email
  goes, in order, to: the tenant's chosen provider (`Notifications.EmailProvider`), then this SMTP server when
  switched on, then the channel default from configuration. The screen shows where email goes now, and a test
  email is sent straight through that route so the server's answer shows at once.
- **Switches are boolean settings** read with `GetBoolAsync` where the behaviour lives (the service, not the page),
  for example `Security.PasswordResetEnabled` and `Documents.PreviewEnabled`.

**Consequences.**
- Email can be set up and tested without a deployment. The Data Protection key ring becomes something to back up
  and protect: losing it means typing the SMTP password again (it reads as unset).
- One migration (`ApplicationSettings.ProtectedValue`). The rule in CLAUDE.md changes from "secrets are never
  settings" to "deployment secrets are never settings".
