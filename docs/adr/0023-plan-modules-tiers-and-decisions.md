# 0023: Plans are built from modules, with suggested tiers and recorded decisions

**Context.** Plan features lived in a flat list in code. A new feature appeared on the plan screen by itself, but
existing plans silently took its default: nobody was told, nobody decided whether it was standard or premium, and
nothing grouped the features of one piece of functionality. The people who price plans are often not developers, and
payment integration will come later and needs a clear, stable definition of what each plan includes.

**Decision.**
- **Code says what exists.** Every feature and limit is an `EntitlementCatalog` entry with its **module**
  (`ModuleCodes`: Documents & files, Branding...), the **version it arrived in** (`Since`) and, for a feature, a
  **suggested tier**: `Standard` (meant for every plan) or `Premium` (meant for higher plans). A feature only exists
  if code checks it (`[RequiresFeature]`, `asp-feature`, `ITenantEntitlements`), so versions and the changelog can't
  create one; declaring it once is the automation.
- **People decide who gets it.** A plan *decides* a feature or limit when it is saved with a value for it (a
  `PlanEntitlement` row). Anything added later is **undecided** for every existing plan: it follows its suggestion
  (Standard on, Premium off, a limit's default) and is flagged until someone decides.
- **One place to decide.** Platform > Plans > Plans and features shows every plan side by side, grouped by module,
  with the price above each column. A module can be included in or left out of every plan at once; "Accept the
  suggestions" decides everything outstanding in one click. Saving the grid decides every cell shown. The plans list
  and each plan's page say how many features still need a decision.
- **Release adoption stays separate.** A plan says *who may* have a feature; a release says *when* an organisation
  sees it (ADR 0022). A release feature can name the plan feature it needs, and the grid marks it "By release".
- **Prices stay commercial.** They are typed on each plan, next to what it includes; nothing derives a price from
  features.

**Consequences.**
- A release that adds features shows up on the plans screens as "N new features need a decision" instead of
  changing plans silently, and the suggestion keeps behaviour sensible until then.
- Splitting a module into finer features (Documents, then Documents from templates) is a catalogue entry and a gate.
- Later: **add-ons** (a premium feature sold on its own, with a price, on top of a plan) belong with payment
  integration; the decided-per-plan rows are the starting point.
