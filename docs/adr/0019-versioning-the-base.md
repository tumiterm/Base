# 0019: Versioning the Base with SemVer, a changelog and tags

**Context.** Every new project starts as a copy of the Base, and the Base keeps improving. Nothing recorded which
Base a project came from, what changed since, or whether taking a change would break the project. The running
application didn't say which build it was, so support couldn't tell either.

**Decision.**
- **One version, in one place.** `Directory.Build.props` holds `BaseVersion` (SemVer). In the Base, the product
  version (`VersionPrefix`) is the same. `scripts/new-project.cs` keeps `BaseVersion` and starts the new product at
  `0.1.0`, so a project always knows both its own version and the Base it is built on.
- **SemVer as seen by a project that copied the Base.** Major: the project must change its own code to take the
  release (public type, setting, permission or configuration key renamed or removed; a rule tightened; a migration
  that changes existing columns). Minor: new things a project can ignore. Patch: fixes only.
- **`docs/changelog.md`** (Keep a Changelog): every change adds a line under Unreleased in the same commit. New
  projects get an empty changelog of their own and the Base's history as `docs/base-changelog.md`.
- **Releases are tags.** A release moves Unreleased under the new version, bumps `BaseVersion`, and is tagged
  `vX.Y.Z` on main: push the tag, or run the Release workflow by hand on main and it tags the commit itself. The
  workflow refuses a tag that doesn't match the version, a version that is already released, or one with no
  changelog section, and publishes the section as the GitHub release notes.
- **The running build says what it is.** `AppVersion` reads the informational version (with the commit when built
  from git) and `BaseVersion` from assembly metadata. It is shown on the System page and in the account menu, and
  returned by `GET api/system/version`.

**Taking a Base release into a project.**
1. `git remote add base <Base repository>` once, then `git fetch base --tags`.
2. Read `docs/changelog.md` on the Base between your `BaseVersion` and the release you want; majors list what to change.
3. Look at the change set: `git diff v1.0.0 v1.1.0 -- src tests` on the base remote. Apply it with
   `git cherry-pick` or by hand. The renamed project folders (`Acme.*` for `Base.*`) mean patches need
   `git apply --directory` or a rename map, so small, frequent upgrades are easier than big ones.
4. Regenerate migrations in the project rather than copying the Base's, then bump `BaseVersion`.

**Consequences.**
- Projects can say "on Base 1.3" and know what that means; support tickets name the exact build.
- The changelog is a gate on every change; a missing line is a review finding.
- Upgrades stay a deliberate, reviewed step. There is no package to update, because the Base is copied, not referenced.
