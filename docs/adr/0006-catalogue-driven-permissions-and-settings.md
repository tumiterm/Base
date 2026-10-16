# 0006: Catalogue-driven permissions and settings

**Context.** String-literal permission checks fail silently when misspelt. Settings scattered across config files
need deployments to change.

**Decision.** Permissions are constants plus a catalogue (label, module, defaults, implications). Unit tests check that
codes and catalogue match, and unknown policies throw. Settings are constants plus typed, validated definitions.
Values are stored per platform or tenant, cached, and seeded without overwriting administrator changes.

**Consequences.** Adding a permission or setting is a code change with tests, and needs no migration. Secrets are never settings.
