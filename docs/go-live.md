# Going live

How to put an app built on Base into production, step by step. Development needs no configuration. Production
starts from `appsettings.json`, which is safe but switches almost everything off, so you add what you need with
environment variables or a secret store. Nothing on this page goes into committed config.

Environment variables use `__` in place of `:`. For example, `ConnectionStrings:Default` becomes
`ConnectionStrings__Default`, and `Payments:Gateways:Yoco:SecretKey` becomes `Payments__Gateways__Yoco__SecretKey`.
List items are indexed: `ForwardedHeaders__KnownProxies__0=10.0.0.5`.

The [checklist](#launch-checklist) at the end gathers every step.

## 1. Build and host

- **Image:** build from the repository root with `docker build -f src/Base.Web/Dockerfile -t base-web .`. The image
  runs as a non-root user on port **8080** and caps the heap at 75% of the container's memory, so give the container
  a memory limit.
- **Environment:** set `ASPNETCORE_ENVIRONMENT=Production`. Never use Development in production: that would turn on
  the Test gateway, the sample plans, the seeded admin password and the public API docs.
- **HTTPS:** terminate TLS at a reverse proxy or load balancer (nginx, Caddy, Traefik, Azure Front Door, an AWS ALB)
  and forward to port 8080. Outside Development the app sends HSTS, redirects to HTTPS and issues secure-only cookies.
- **Proxy:** list the proxy's IP addresses in `ForwardedHeaders:KnownProxies`. The app trusts `X-Forwarded-For`,
  `-Proto` and `-Host` only from those addresses. If the list is wrong, HTTPS redirects loop, rate limits count every
  user as the proxy, and Payfast's source-address check fails.
- **Hosts:** set `AllowedHosts` to your domains, separated by semicolons (for example
  `app.example.com;*.example.com`), instead of `*`.
- **Probes:**
  - `GET /health/live` is liveness: the process is up.
  - `GET /health/ready` is readiness: the database answers.
  - Both are anonymous. The other dependencies (storage, email, payment gateways, ClamAV, Gotenberg, the outbox,
    jobs) don't block readiness; they show under **Administration → System**.
- **More than one instance:** this works as is, because Data Protection keys, job leases and the outbox all live in
  the database. Pin nothing to one instance, and don't use local-disk file storage.

## 2. Secrets

These are deployment secrets. Put them in environment variables, or in your platform's secret store (Azure Key Vault,
AWS Secrets Manager, Kubernetes secrets, Docker secrets), never in a settings screen or in git:

| Key | What |
| --- | --- |
| `ConnectionStrings:Default` | SQL Server connection string |
| `Seed:AdminEmail`, `Seed:AdminPassword` | First administrator: first start only, see below |
| `FileStorage:S3:AccessKey` / `SecretKey`, or `FileStorage:AzureBlob:ConnectionString` | File storage |
| `Notifications:Providers:{name}:Password` / `ApiKey` / `AccessToken` | Email, SMS and WhatsApp providers |
| `Payments:Gateways:Payfast:MerchantId` / `MerchantKey` / `Passphrase` | Payfast |
| `Payments:Gateways:Yoco:SecretKey` / `WebhookSecret` | Yoco |

If a key is ever pasted into chat, email or a ticket, regenerate it with the provider before going live.

## 3. Database

- **Server:** use SQL Server 2019 or later, or Azure SQL. Give the app's login rights on its own database only:
  `db_datareader`, `db_datawriter`, plus DDL rights if the app runs its own migrations.
- **Migrations:** pick one of these.
  - Set `Database:MigrateOnStartup=true` and the app migrates itself when it starts. This is simplest for a single
    instance.
  - Better, apply migrations in the release pipeline before the new version starts. Generate the script with
    `dotnet ef migrations script --idempotent --project src/Base.Infrastructure --startup-project src/Base.Infrastructure -o migrate.sql`
    and run it with a login that has DDL rights. The app's own login then needs no DDL rights.
- **Backups:** turn on automated backups with point-in-time restore, and test a restore before launch. The database
  holds everything except file bytes: users, payments, the audit trail, Data Protection keys and job state.
- **Tuning:** `Database:CommandTimeoutSeconds` (30), `MaxRetryCount` (5) and `SlowQueryThresholdMs` (500; slow queries
  are logged and counted).

## 4. First start and the administrator

1. For the first start only, set `Seed:AdminEmail` and `Seed:AdminPassword` (a strong password). The administrator is
   created when the default tenant has no users.
2. Sign in and change the password.
3. Remove `Seed:AdminPassword` from the environment. It is never needed again.
4. Keep `Seed:SamplePlans` off (it is off by default outside Development) and create your real plans and prices in
   **Platform → Plans**.

## 5. Tenancy and addresses

- **`Tenancy:MultiTenant`:** set it to `true` if organisations sign up or are created as tenants. It is off by default
  outside Development.
- **`Tenancy:DefaultTenantHosts`:** the hosts that open the platform (default) tenant, for example
  `["app.example.com"]`. Other hosts resolve to the tenant that owns them (set under **Platform → Tenants**).
- **`Tenancy:FallbackToDefaultTenant`:** keep it `false`. An unknown host should fail rather than open the platform
  tenant.
- **DNS and certificates:** give each tenant domain a DNS record pointing at the proxy, and a certificate. A wildcard
  covers `*.example.com`.
- **`General.PublicBaseUrl`:** this is a setting, under **Administration → Settings → General**. Set it to the public
  `https://` address. Invitation and reset emails link to it, and so do payment links, the URLs the payment
  providers call back on, and Yoco's verified domain. Set it before anything else on this page that depends on it.

## 6. Keys at rest (Data Protection)

Sign-in cookies, invitation tokens and `Secret` settings (such as the SMTP password) are protected by ASP.NET Data
Protection keys, which are stored in the database. That means:

- Every instance shares the same keys, and sign-ins survive restarts and deployments.
- Whoever can read the database can read those keys. Keep database access tight, and encrypt backups.
- Encrypting the keys themselves with a certificate or Azure Key Vault (`ProtectKeysWithCertificate` /
  `ProtectKeysWithAzureKeyVault`) is a small code change in `Base.Infrastructure/DependencyInjection.cs` and isn't on by
  default. Add it if your security policy requires it.

## 7. Services

| What | Configure | Notes |
| --- | --- | --- |
| **File storage** | `FileStorage:S3` (Bucket, Region, ServiceUrl for R2/MinIO, keys) or `FileStorage:AzureBlob` (ConnectionString, Container) | Required in production. Don't use `LocalDisk` with containers: files vanish on redeploy. Set `Files.DefaultProvider` under Settings. Make the bucket private; the app serves files itself. |
| **Email** | `Notifications:Channels:Email:Provider` plus a `Notifications:Providers` entry (`Smtp` or `SendGrid`), or the SMTP server under **Settings → Email** | The default writes email to the log. Nothing is sent until you set this. Set up SPF, DKIM and DMARC for the sending domain. |
| **SMS / WhatsApp** | Channel `Enabled: true` plus a provider (`Clickatell`, `BulkSms`, `TwilioSms`, `TwilioWhatsApp`, `MetaWhatsApp`) | Off by default. |
| **Virus scanning** | `FileScanning:ClamAv:Host` | Optional. Once it is set, uploads are refused while clamd is down. |
| **PDFs** | `Documents:Pdf:Gotenberg:BaseUrl` | Optional. Without it, documents are offered as HTML only. Run Gotenberg on a private network. |
| **Outbox** | `Outbox` (on by default) | Sends the emails and runs the side effects that follow a save. Leave it on. |
| **Housekeeping jobs** | `Jobs:Enabled` (on) | Payment expiry, retention and similar jobs. Runs and failures show under **Administration → System**. |
| **Hangfire** | `Hangfire:Enabled` | Optional. Its dashboard (`/jobs`) needs the *Manage jobs* permission. |

## 8. API, logging and monitoring

- **`Api:Documentation`:** keep it `Off` (the default), or `SignedIn` if API clients need the docs.
- **`Api:AllowedOrigins`:** list browser origins only if another site calls the API from JavaScript.
- **Rate limits:** `Api:UserRequestsPerMinute` and `AnonymousRequestsPerMinute`.
- **Logs:** Serilog writes to the console. Collect container output, or add a sink such as Seq or Application
  Insights in config. The default file sink writes to `logs/` inside the container; drop it or mount a volume.
- **Traces and metrics:** set `OTEL_EXPORTER_OTLP_ENDPOINT` to your collector (Aspire dashboard, Grafana, Honeycomb,
  Azure Monitor).
- **Error links:** `Observability:TraceUrlTemplate` and `LogSearchUrlTemplate` link an error ticket to its trace and
  logs.
- **Alerts:** at minimum, alert when `/health/ready` fails, when the error rate rises, when the outbox backs up, and
  when a job fails.
- **Feature flags:** check `FeatureFlags` overrides and the flags under **Platform → Feature flags**. Every
  release flag should be in the state you mean to launch with.

## 9. Payments

Full details are in [payments.md](payments.md). For production:

- **Test gateway:** keep `Payments:Gateways:Test:Enabled` off. It is on only in Development; never switch it on in
  production.
- **Payfast:**
  1. Set `Enabled=true`, `Sandbox=false`, and the live `MerchantId`, `MerchantKey` and `Passphrase` (the passphrase
     must match the one set in Payfast's dashboard).
  2. Payfast must reach `{PublicBaseUrl}/payments/notify/payfast` over the internet. Leave `VerifySourceAddress` on,
     and make sure `KnownProxies` is correct, or the check sees your proxy's IP address instead of Payfast's.
  3. For automatic renewal, turn on *Recurring billing* in the Payfast dashboard.
- **Yoco:**
  1. Verify your domain (the host of `PublicBaseUrl`) in the Yoco App.
  2. Set `Enabled=true` and the live `SecretKey` (`sk_live_...`).
  3. Register the webhook for `{PublicBaseUrl}/payments/notify/yoco` **with the live key**, then set `WebhookSecret`
     to the `whsec_...` value it returns. That value is shown only once, and test-key webhooks don't carry over.
- **`Payments:PendingFor`:** how long an unpaid checkout stays open (2 hours by default).
- **Checkout options:** after a restart, tenant administrators see the switched-on gateways on **Subscription**. The
  gateway name drops the "(sandbox)" or "(test)" label once live credentials are in.

## 10. Releasing

- **Versions:** the version is `BaseVersion` in `Directory.Build.props`. A release moves the **Unreleased** lines in
  [changelog.md](changelog.md) under a version heading, bumps `BaseVersion`, and pushes a `vX.Y.Z` tag. The release
  workflow checks the tag against the version and the changelog.
- **Deploy order:**
  1. Back up.
  2. Apply migrations.
  3. Roll the new image out, waiting for `/health/ready` on each instance.
  4. Watch errors and the outbox for a while.
- **Rollback:** migrations are forward-only. Roll back by redeploying the previous image only when the new
  migrations are additive; otherwise restore the backup.

## Launch checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production`, HTTPS at the proxy, `ForwardedHeaders:KnownProxies` and `AllowedHosts` set
- [ ] All secrets come from environment variables or a secret store; none are in git; any pasted keys are regenerated
- [ ] Database created, migrations applied, automated backups on and a restore tested
- [ ] First administrator signed in, password changed; `Seed:AdminPassword` removed
- [ ] `General.PublicBaseUrl` is the public `https://` address
- [ ] Tenancy settings, tenant domains, DNS and certificates in place
- [ ] File storage on S3 or Azure Blob; a test upload and download work
- [ ] Email provider on; SPF, DKIM and DMARC set; an invitation email arrives and its link opens the public address
- [ ] `/health/live` and `/health/ready` wired to the orchestrator; **Administration → System** shows everything healthy
- [ ] Logs and traces reach your monitoring; alerts set up
- [ ] API documentation `Off`; CORS only if needed
- [ ] Real plans and prices created; sample plans and the Test gateway off
- [ ] Payfast live: one small real payment (R5.00 or more) is marked paid and the subscription moves on
- [ ] Yoco live: domain verified, webhook registered with the live key; one small real payment (over R2.00), made
      from a link opened on a phone, is marked paid
- [ ] Receipts arrive for both payments
- [ ] The release is tagged `vX.Y.Z` and its changelog section is written
