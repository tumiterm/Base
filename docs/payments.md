# Online payments

Organisations pay for their plan from **Subscription** (people with *Pay for the subscription*); the platform sees
every payment under **Platform → Payments**. How it works is in [ADR 0026](adr/0026-online-payments-through-hosted-gateways.md).
This page is how to set a gateway up.

## Gateways

| Code | What it is | Configure under |
| --- | --- | --- |
| `Test` | A pretend checkout page in the app (Pay / Decline / Cancel). On in Development. **Never in production.** | `Payments:Gateways:Test:Enabled` |
| `Payfast` | Payfast's hosted checkout: card, Instant EFT, QR codes (SnapScan, Zapper, Scan to Pay), Apple Pay, Samsung Pay and others the merchant account has switched on. Rand only, R5.00 minimum. Automatic renewal. | `Payments:Gateways:Payfast` |
| `Yoco` | Yoco's Checkout API: Yoco's hosted payment page (cards and the wallets Yoco offers there). Rand only, R2.00 minimum. Once-off payments (no automatic renewal). | `Payments:Gateways:Yoco` |

A gateway appears on the Subscription page once it is enabled, for plans in a currency it accepts.

## Payfast

### 1. Credentials

Merchant ID, merchant key and passphrase are secrets: keep them in user-secrets (development) or environment variables
/ your secret store (production), never in committed config.

```bash
cd src/Base.Web
dotnet user-secrets set "Payments:Gateways:Payfast:Enabled" "true"
dotnet user-secrets set "Payments:Gateways:Payfast:Sandbox" "true"
dotnet user-secrets set "Payments:Gateways:Payfast:MerchantId" "<merchant id>"
dotnet user-secrets set "Payments:Gateways:Payfast:MerchantKey" "<merchant key>"
dotnet user-secrets set "Payments:Gateways:Payfast:Passphrase" "<salt passphrase>"
```

For the sandbox, use the merchant ID, key and passphrase from your own sandbox account (sandbox.payfast.co.za →
Settings, where you also set the "Salt Passphrase"), or the shared test credentials in Payfast's documentation
(*Testing and Tools → Test transaction setup*). The sandbox buyer pays from a test wallet. In production set
`Payments__Gateways__Payfast__...` environment variables and `Sandbox` to `false`.

Always set a passphrase: it salts the signature, and Payfast requires one for recurring billing.

### 2. Payfast must reach the notification URL

Payfast confirms each payment by posting an ITN to `{public address}/payments/notify/payfast`, where the public
address is `General.PublicBaseUrl` as it applies to the paying organisation (its own verified domain, or the
platform's). Only that notification marks a payment paid (the page the person returns to never does), so:

- `General.PublicBaseUrl` (Administration → Settings) must be a public HTTPS address that reaches the app: the
  platform's (its host in `Tenancy:DefaultTenantHosts`) or the organisation's verified domain.
- On a developer machine, expose the app with a tunnel (ngrok, Expose...), add the tunnel's host as the
  organisation's verified domain (or to `Tenancy:DefaultTenantHosts`) and set `General.PublicBaseUrl` to it while
  testing.
- Behind a reverse proxy, configure `ForwardedHeaders:KnownProxies`/`KnownNetworks` so the app sees Payfast's address.

### 3. What is checked on every ITN

As Payfast requires, an ITN counts only if all four checks pass; otherwise it is answered 400 (Payfast retries) and
logged as *Rejected* with the reason:

1. **Signature**: MD5 of the posted fields, in the order posted, with the passphrase (`PayfastSignature`, tested
   against Payfast's own PHP samples).
2. **Source**: the sender's address resolves from `www`, `sandbox`, `w1w` or `w2w.payfast.co.za`, or is listed in
   `AllowedSourceAddresses` (IPs or CIDR ranges from Payfast's *Ports and IP Addresses* page).
   `VerifySourceAddress: false` turns this off; only do so on a machine where the tunnel hides the sender.
3. **Amount**: `amount_gross` equals the payment's amount.
4. **Server confirmation**: Payfast's `/eng/query/validate` answers `VALID` for the same fields.

Repeats are recognised (`pf_payment_id` + status) and change nothing.

### 4. Options

| Key | Default | |
| --- | --- | --- |
| `Methods` | Card, Instant EFT, QR code | What the pay-with card lists. Add `Apple Pay`, `Samsung Pay`... once switched on in the Payfast account. |
| `PaymentMethod` | (all) | Limit the checkout to one Payfast method code (`cc`, `ef`, `ap`, `sp`, `ss`, `zp`, `mp`...). |
| `AllowedSourceAddresses` | none | Payfast's published addresses, if its host names don't resolve to them from your network. |
| `VerifySourceAddress` | `true` | |

The browser posts the checkout form to Payfast, so the Content Security Policy's `form-action` includes the
configured Payfast host automatically.

### 5. Going live

Switch to live credentials, set `Sandbox` to `false`, check `General.PublicBaseUrl`, and make one small real payment
(Payfast's minimum is R5.00; its fees on test payments aren't refunded).

## Yoco

Yoco's **Checkout API**: the server creates a checkout (`POST https://payments.yoco.com/api/checkouts` with the secret
key), the person pays on Yoco's page, and Yoco reports the outcome with a webhook. (Yoco's *payment links* are a
different API, the Yoco Business API with app tokens; they aren't used here.)

### 1. Keys

In the Yoco App, open **Manage → Connect → Checkout API → How to connect** and copy the **Test secret key**
(`sk_test_...`). Live keys (`sk_live_...`) unlock once your domain is verified under **Verified Domains**. Keys are
secrets: user-secrets or environment variables only.

```bash
cd src/Base.Web
dotnet user-secrets set "Payments:Gateways:Yoco:Enabled" "true"
dotnet user-secrets set "Payments:Gateways:Yoco:SecretKey" "<sk_test_...>"
dotnet user-secrets set "Payments:Gateways:Yoco:WebhookSecret" "<whsec_...>"
```

### 2. Register the webhook (once per key)

Yoco sends webhooks to an address registered for your account, not per checkout. Register
`{public address}/payments/notify/yoco` with your secret key; the answer contains the signing secret (`whsec_...`)
for `WebhookSecret`:

```bash
curl -X POST https://payments.yoco.com/api/webhooks \
  -H "Authorization: Bearer <sk_test_...>" -H "Content-Type: application/json" \
  -d '{"name": "base", "url": "https://<your public address>/payments/notify/yoco"}'
```

The public address follows the same rules as Payfast's (a public HTTPS address that reaches the app; a tunnel on a
developer machine). Register again with the live key when going live, and use that secret in production.

- Yoco shows the secret **only once**, in that answer: save it straight into your secret store.
- Register **one** webhook per key; delete any you don't want with Yoco's delete-webhook endpoint.
- Yoco expects a 2xx within 15 seconds. The app answers as soon as the event is recorded (the subscription is moved
  on afterwards, in the background).
- Without a 2xx, Yoco retries at once, then after 5 seconds, 5 minutes, 30 minutes, 2 hours, 5 hours and twice after
  10 hours. **If no event succeeds for 5 days, Yoco turns the webhook off** and it must be registered again (with a
  new secret): after a long outage, check it is still on.
- Yoco sends every payment event on the account, including ones for other integrations. Events for checkouts that
  aren't ours are answered 200 and logged as *Ignored*, so they never count as failures.

### 3. Verified domain and payment links

Live Yoco payments must come from a website domain verified with Yoco (Yoco App → How to connect → Verified Domains):
Yoco checks which page sent the person to its checkout. So the person is never redirected straight to Yoco. They pass
through our forwarding page, `{public address}/payments/go/{reference}`, which loads on our domain and then continues to
Yoco by script, as Yoco recommends. **Verify the domain of `General.PublicBaseUrl`**: the platform's, plus any
organisation's own domain where that organisation's public address is set to it.

The same page is the **payment link**: while a payment is waiting, its page under Subscription shows the link to copy
and send by email or WhatsApp to whoever pays. It works without signing in (the reference is the key, as Yoco's own
link would be) and stops working once the payment is paid, declined, cancelled or expired (`Payments:PendingFor`,
two hours by default).

Keep `Referrer-Policy` as the app sets it (`strict-origin-when-cross-origin`), serve the public address over HTTPS,
and don't add `rel="noreferrer"` to links to it: any of those hides the domain from Yoco and live payments fail. Test
keys skip the domain check.

### 4. What is checked on every webhook

- **Signature** (Standard Webhooks): HMAC-SHA256 of `webhook-id.webhook-timestamp.body` with the `whsec_` secret,
  compared with each `v1,` entry in `webhook-signature`.
- **Freshness**: `webhook-timestamp` within 5 minutes, against replays.
- **Amount**: the payment's amount in cents equals the payment's.
- **Which payment**: Yoco's `metadata.checkoutId`, matched against the checkout id kept when the checkout was created
  (as Yoco recommends), or our own reference when the metadata carries it.

`payment.succeeded` marks the payment paid; `payment.failed` declined; anything else is logged and ignored.
Repeats (same `webhook-id`) change nothing.

### 5. Testing

Pay on Yoco's test page with the **test card** shown in the Yoco App (*How to connect → Test card details*). Test
payments move no money.

### 6. Going live

Verify your domain in the Yoco App, switch to the live secret key, register the webhook with it and set its secret.
Then make a small real payment (over R2.00) through a payment link sent to yourself by email or WhatsApp, opened on a
phone, to confirm the domain check passes.

## Automatic renewal

When the chosen gateway can charge by itself (Payfast, and the Test gateway), the pay form offers **Renew
automatically** (on by default). The first payment sets up the renewal; after that the gateway charges the same amount
every month or year and each charge arrives as a notification: it is recorded as a renewal payment, moves the
subscription on a period and emails a receipt. Subscription shows the renewal with **Update card** (Payfast's card
page) and **Turn off**.

There is at most one renewal per organisation, for the plan it pays for. It is stopped, at the gateway first:

- when the organisation turns it off;
- when a newer renewal is set up, or a one-off payment is made for another plan or period (one for the same plan
  and period just pays ahead);
- when the subscription is cancelled or expires.

If the gateway stops it by itself (the payer cancels at Payfast...), the organisation's contact is emailed. If the
gateway can't be reached to stop it, the renewal stays on with the reason on the Subscription page: try again, or
cancel it in the gateway's dashboard. A charge that still arrives is recorded, since the money was taken.

**Payfast** uses its Subscriptions (recurring billing): `subscription_type=1`, `frequency` 3 (monthly) or 6 (annual),
`cycles=0` (until cancelled), and the ITN's `token`. It needs the passphrase. Turning a renewal off calls Payfast's
API (`PUT https://api.payfast.co.za/subscriptions/{token}/cancel`, `?testing=true` against the sandbox, signed with
the merchant ID, version and timestamp headers sorted alphabetically). Check the cancel once against your sandbox
before going live; if Payfast changes it, the renewal shows why it couldn't be stopped.

**Test gateway**: the renewal panel has **Simulate the next charge**, which sends the notification a real gateway
would at the end of the period.

## Not yet

Refunds and invoices; automatic renewal through Yoco (its Checkout API takes once-off payments).
