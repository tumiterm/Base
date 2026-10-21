# Online payments

Organisations pay for their plan from **Subscription** (people with *Pay for the subscription*); the platform sees
every payment under **Platform → Payments**. How it works is in [ADR 0026](adr/0026-online-payments-through-hosted-gateways.md).
This page is how to set a gateway up.

## Gateways

| Code | What it is | Configure under |
| --- | --- | --- |
| `Test` | A pretend checkout page in the app (Pay / Decline / Cancel). On in Development. **Never in production.** | `Payments:Gateways:Test:Enabled` |
| `Payfast` | Payfast's hosted checkout: card, Instant EFT, QR codes (SnapScan, Zapper, Scan to Pay), Apple Pay, Samsung Pay and others the merchant account has switched on. Rand only, R5.00 minimum. | `Payments:Gateways:Payfast` |

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

Refunds, invoices and Yoco.
