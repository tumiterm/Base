# 0025: Feature flags switch code on and off without a deployment

**Context.** The Base already decides behaviour three ways: plan features (what a customer pays for), release
features (what an organisation has taken, ADR 0022) and settings (what an organisation configures). None of them
answers two everyday engineering questions: "can we ship this half-finished work dark and try it with two friendly
customers first?" and "something is misbehaving in production, can we stop it now?". Using a plan feature or a
release for that mixes billing and product promises with operational control; a setting is in the customer's hands.
Libraries such as Microsoft.FeatureManagement evaluate flags from configuration, but they don't know about tenants,
the platform organisation, audit or a screen, and filters for those would be most of the code anyway.

**Decision.**
- **Flags are code, in a catalogue.** A flag is a constant in `FeatureFlagCodes` (`Flag.*`) and a
  `FeatureFlagDefinition` in `FeatureFlagCatalog`: name, description, owner, kind and default. Flags not in the
  catalogue don't exist, so a typo throws instead of silently reading "off".
- **Two kinds.** A **release flag** is temporary: off by default, it carries a `RemoveBy` date and the platform page
  shows it as overdue once that date passes. A **kill switch** is permanent and on by default, kept so an operator
  can stop something in an emergency. The Base ships one: `Flag.MessageSending` pauses Messages (messages stay
  scheduled and go out once it is back on).
- **Three states.** `Off`, `Everyone`, or `Targeted`: the platform's own staff, chosen organisations, and/or a
  percentage of organisations. The percentage uses a stable bucket (SHA-256 of flag code and tenant id, mod 100), so
  each flag picks different organisations first and growing from 10% to 50% keeps the first 10%. Outside an
  organisation (system work) only `Everyone` counts.
- **State is platform data.** `FeatureFlagState` (table `FeatureFlags`) holds one row per flag that has been changed;
  a flag without a row uses its default. Rows are ordinary audited entities, so every change is in the audit trail
  with who made it. All rows are cached together in HybridCache (tag `flags`) and cleared on every change; other
  servers pick it up within 30 seconds.
- **Configuration overrides.** `FeatureFlags:Overrides:{Code}` = `true`/`false` forces a flag on one server, for
  development, tests and an emergency when the database is the problem. The screen says when this applies.
- **Gates.** `IFeatureFlags.IsOnAsync(code)` in services (or `IsOnForAsync(code, tenantId)` in jobs that work across
  tenants), `[RequiresFlag]` on controllers and actions (a 404, as if the endpoint weren't there), and `asp-flag` in
  views. A flag that is off wins: the code doesn't run, whatever the plan or release says.
- **One screen.** Platform → Feature flags (`Platform.Flags.Manage`, platform-only, administrators) lists every flag
  with who it is on for, the last change, overrides and overdue release flags.
- **Our own code, not a package.** The evaluation is about 40 lines of pure functions with unit tests; the rest is
  the same catalogue, cache and screen pattern as releases and settings.

**Which tool to use.**

| Question | Tool |
| --- | --- |
| Does this customer pay for it? | Plan feature or limit (`EntitlementCatalog`, ADR 0023) |
| Has this organisation taken the change yet? | Release feature (`ReleaseCatalog`, ADR 0022) |
| How does this organisation want it to work? | Setting (`SettingCatalog`) |
| Is this code ready for them / is it safe to run right now? | Feature flag (`FeatureFlagCatalog`) |

**Consequences.**
- Unfinished work can be merged and deployed dark, then tried with the platform's staff and a few organisations,
  without branches or plan changes.
- Operators can stop a misbehaving feature in seconds, and the audit trail shows who did.
- Every release flag is a second code path. The remove-by date, the overdue warning and the rule "delete the flag and
  the old path once it is on everywhere" keep them from piling up.
- `IFeatureFlagCatalog` is injectable, so tests supply their own flags and test both states.
