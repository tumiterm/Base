# 0024: Messages people write go to audiences that modules declare, now or at a set time

**Context.** The notification system sent what the application decided to send (invitations, reminders), from
catalogue templates. People also need to write to others themselves: one person, everyone in a department or role,
or an external list, by email, SMS, WhatsApp or in-app, sometimes at a later time. The obvious design, choosing a
table and a column in the UI, would bypass tenant isolation and permissions, break when the schema changes, mean
nothing to non-technical users, and send duplicates and messages to people who opted out.

**Decision.**
- **Audiences, not tables.** A module that holds people registers an `IAudienceSource`: its name, the filters it
  offers (department, status, role...), the placeholders it fills, and how to read its members through its own use
  cases. The Base ships **People** and **Users** (by role; the only audience that receives in-app). A project adds
  Employees or Students the same way. A **pasted or uploaded CSV list** is the third kind: Name, Email and Phone are
  recognised and every other column becomes a placeholder.
- **One pipeline.** A `Broadcast` holds the wording, channels, audience and time. Sending it as the person who wrote
  it (`IRunAsUser`) reads the audience with their permissions, removes duplicate accounts, email addresses and phone
  numbers per channel, and calls `INotificationService` once per recipient with the wording
  (`Notification.Content`) and the broadcast's id (`NotificationMessage.SourceId`). Preferences ("Announcements"),
  channel settings, retries, the delivery log and the in-app inbox all apply unchanged; progress is counted from the
  messages themselves.
- **Now or later.** "Send now" raises `BroadcastDue`, handled by the outbox straight after the save. A scheduled
  message waits for the `scheduled-messages` job (every minute), which also finishes a send that stopped part-way
  (`Broadcast.Processed`) and one the outbox never picked up. A scheduled message can be changed or cancelled until
  it goes out; a sent one can be "sent again" as a new message.
- **Safe wording.** HTML is cleaned on the server (`IRichText`, HtmlSanitizer: formatting and links only) and turned
  into plain text for the email's text part, in-app and SMS. Placeholders are checked against the audience before
  anything is sent. WhatsApp uses a Meta-approved template whose numbered gaps are filled per recipient.
- **Plans and permissions.** Module *Messaging*: "Send messages" (Standard), "Bulk messages" (Premium) and a
  "Messages per month" limit, checked when sending. Permissions `Messages.Send` (up to 50 chosen people, templates) and
  `Messages.SendBulk` (filtered audiences and lists).
- **Templates people write** (`MessageTemplate`) are separate from the system's notification templates.

**Consequences.**
- A new module makes its people reachable with one class; nobody can message data they couldn't see.
- People records have no notification preferences of their own: they can't opt out the way users can. Projects that
  email the public in bulk should add an unsubscribe link and honour it in their audience.
- Later: recurring sends, and bulk WhatsApp beyond approved templates (Meta's rules).
