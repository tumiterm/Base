# 0026: Organisations pay for their plan online through hosted payment gateways

**Context.** Subscriptions had a full lifecycle (ADR 0016) but no way to take money: an operator recorded each payment
by hand. South African customers expect Payfast (card, Instant EFT, QR codes such as SnapScan and Zapper, Apple Pay
and Samsung Pay) and Yoco. Each provider has its own checkout, signatures and notifications, and more will come, so
the subscription code must not learn any provider's details. Handling card numbers ourselves would put the whole
application in PCI DSS scope.

**Decision.**
- **Hosted checkout only.** People pay on the gateway's own page (redirect, or a form the browser posts by itself).
  No card data passes through or is stored by the application. Wallets and QR methods come with the gateway's page:
  they are switched on in the merchant account, not built here.
- **One port, many gateways.** `IPaymentGateway` (Application) starts a checkout and reads the gateway's
  notifications; each provider is an Infrastructure adapter registered only when configured under
  `Payments:Gateways:{Code}`. Merchant credentials are deployment secrets (user-secrets or environment), never
  settings or committed config.
- **A payment is a record.** `Payment` (tenant-owned) holds our short reference (what the gateway sees), the plan and
  billing cycle it pays for, the amount in the plan's currency, the gateway's reference and a status: Pending →
  Succeeded, Failed, Cancelled or Expired. Only the first success counts, and a success wins over a cancel or an
  expiry (the money was taken).
- **The gateway's notification decides, never the browser.** Gateways post to `payments/notify/{gateway}`: anonymous,
  without antiforgery, small bodies only. The adapter verifies it (signature, source, and the gateway's own
  confirmation where it offers one); the service then drops repeats (`PaymentNotification`, unique per gateway and
  event), checks the amount against the payment, and records the outcome in the payment's own tenant. Every
  notification is logged with its outcome (Applied, Duplicate, Rejected, Ignored), without card or personal data.
  The return page only shows the status; coming back from "cancel" marks a still-pending payment cancelled.
- **Applying is an event.** A succeeded payment raises `PaymentSucceeded`; its outbox handler applies it once
  (`AppliedAt`): the same plan and cycle extend the subscription after the current period; another plan or cycle
  switches to it with a fresh period from today. The organisation's contact and the payer get a receipt
  (`Subscriptions.PaymentReceived`). The `payment-expiry` job marks payments nobody answered about as expired.
- **Who pays.** `Subscription.Pay` (administrators by default) on the Subscription page, which stays open to
  suspended organisations so they can pay their way back in. Platform → Payments shows every organisation's payments.
- **A Test gateway.** `Payments:Gateways:Test:Enabled` (on in Development) adds a pretend checkout page in the
  application that reports back through the same notification path, protected with Data Protection, so the flow can
  be tried and tested without a provider account. Never enable it in production.
- **Payfast** is the first real gateway: a signed form posted to Payfast's checkout, and ITNs accepted only after the
  four checks Payfast requires (signature, source address, amount, server confirmation). Its signature code is
  tested against Payfast's own PHP samples. Setup is in `docs/payments.md`.
- **Yoco** is the second: its Checkout API (the server creates a checkout; the person is sent to Yoco's page from a
  page of ours, so no CSP entry is needed) and Standard Webhooks signatures with a freshness window. Yoco's webhook
  names its checkout, so a payment is also found by the gateway reference its checkout started with. Live Yoco
  payments must come from a verified domain, so a checkout reached by a link is kept on the payment and the person
  goes through our anonymous forwarding page (`payments/go/{reference}`), which also serves as a shareable payment
  link while the payment waits.
- **Automatic renewal is the gateway's.** A `RecurringPayment` records the agreement (plan, cycle, amount, the
  gateway's token) set up by a first payment with "Renew automatically"; the gateway charges each cycle by itself
  (Payfast Subscriptions) and each charge's notification becomes a renewal `Payment`, applied like any other. We keep
  no card details and run no charging schedule. One renewal per organisation: a newer one, a one-off payment for
  another plan or cycle, the subscription ending, or the organisation turning it off stops it at the gateway first
  (`IPaymentGateway.CancelRecurringAsync`); a stop the gateway refused stays visible with its reason. Gateways
  without recurring billing simply don't offer it.
- **Not yet:** refunds and invoices. Without automatic renewal, renewal is a "Pay" each period; the lifecycle job
  moves unpaid subscriptions to past due and suspended as before.

**Consequences.**
- Adding a provider is one adapter and its configuration; subscription, screens and tests don't change.
- The notification endpoint is the one anonymous write in the application. It can only change the payment a
  verified notification names, by the amount that payment expects.
- Gateways whose checkout is a posted form or a redirect after a form post must be allowed in the CSP's
  `form-action`; each adapter declares its hosts.
