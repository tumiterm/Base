# 0015: Own design system on Bootstrap's behaviour, per-tenant branding as generated CSS

**Context.** The screens were stock Bootstrap: a navbar, default buttons, default tables. That is the
"generic admin template" look that `docs/ui-design-direction.md` rules out. Each tenant also needs its own colour
and logo without a deployment. The CSP is strict (`script-src 'self'; style-src 'self'`), so a theme can't be
injected as an inline `<style>` or `style=""` attribute.

**Decision.**
- **Our own tokens and components on top of Bootstrap 5.3.** Bootstrap stays for behaviour and accessibility
  (dropdowns, modals, collapse, focus handling). `design-system.css` defines the tokens (neutral ramp, surfaces,
  status colours, type scale, spacing, radii, elevation, control sizes, motion) for light and dark, maps
  Bootstrap's `--bs-*` variables onto them, and adds the app shell and components. A component framework (Tailwind,
  a JS component library) was rejected: it adds a build step to every project and doesn't remove the need for our
  own visual language.
- **Self-hosted assets.** The Inter variable font (OFL) and a Lucide icon sprite (ISC) live in `wwwroot`, so there's
  no CDN, nothing for the CSP to allow and nothing to track users. `<icon name="…" />` renders a sprite symbol.
- **App shell.** A sidebar that collapses to an icon rail and becomes a drawer on phones, a sticky topbar, a
  command palette (Ctrl/Cmd+K, built from the navigation the user can see), toasts for flash messages, and tables
  that turn into labelled cards on phones. Light, dark or follow-device, remembered per browser.
- **Per-tenant branding is settings plus generated CSS.** `Branding.PrimaryColor` (a new `Color` setting type),
  `Branding.DefaultTheme` and `Branding.LogoFileId` are tenant settings, edited at Administration → Appearance
  (`Settings.Edit`). `/theme.css?v={hash}` derives the accent ramp (hover, active, subtle, ring) for light and dark,
  and picks white or dark text by WCAG contrast. It is served as an external stylesheet, so the CSP is unchanged,
  and cached for a year because the version changes with the settings. The logo is stored through `IFileService`
  (category `branding`; PNG, JPEG or WebP up to 512 KB; SVG refused because it can carry script) and served from
  `/branding/logo`.
- **Static assets are anonymous.** The secure-by-default fallback policy also applied to `MapStaticAssets`, which
  sent the sign-in page unstyled. `MapStaticAssets().AllowAnonymous()` fixes it, and a test covers it.

**Consequences.** New screens are built from the tokens and components. They pick up the tenant's brand and dark
mode without extra work, and `CLAUDE.md` makes the design direction the minimum bar. Only the accent is per tenant:
neutrals, status colours and type stay fixed so every tenant's app stays readable and consistent. A tenant can't
upload a vector logo. A project that wants a different look changes the tokens, not the pages.
