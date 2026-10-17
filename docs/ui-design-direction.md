# UI design direction (the minimum bar)

This is the product owner's design brief, kept word for word below. It is the **minimum** every screen in the base,
and in every project built from it, must meet. The second half says how the base meets it, so that a new screen is
built from the design system rather than from scratch.

## How the base implements it

- **Tokens first.** `wwwroot/css/design-system.css` defines every colour, the type scale (13–14px base, Inter),
  spacing (`--s-*`), radii, elevation, control heights and motion as CSS variables for the light and dark themes.
  Bootstrap 5.3 stays only as the behaviour layer (dropdowns, modals, collapse). Its `--bs-*` variables are mapped onto
  the tokens, so no stock Bootstrap look is left. New styles use tokens; never raw hex values or pixel sizes.
- **Per-tenant accent.** Each tenant picks an accent colour, a default theme and a logo (Administration → Appearance,
  the `Branding.*` settings). `/theme.css` turns the colour into an accent ramp (hover, active, subtle, focus ring) with
  WCAG contrast for the text on it. Use `var(--accent*)` for anything that should follow the brand.
- **App shell.** `_Layout` is the sidebar (collapsible to an icon rail, a drawer on phones), a sticky topbar with
  the command palette (Ctrl/Cmd+K), the theme switch and notifications. `_AuthLayout` is the centred card for
  sign-in pages. `_Sidebar` holds the navigation, with each link gated by `asp-permission`.
- **Icons.** `<icon name="users" />` renders one symbol from the self-hosted Lucide sprite (`/img/icons.svg`). Add a
  symbol to the sprite rather than using another icon set.
- **Components.** The building blocks are page header + actions, panels, stats, segmented controls, form sections
  (label and help on the left, controls on the right), input affixes, tables (sticky header, row actions; on phones
  they become cards via `data-label`), status badges, chips, toasts, empty states, skeletons, tabs, drop zones and
  swatches. Reuse them; extend `design-system.css` when a new pattern is genuinely needed.
- **Strict CSP.** No inline `<script>` or `style=""`. Behaviour lives in `site.js` (data attributes), and dynamic
  styling is set with `element.style.setProperty` or served as CSS.
- **Motion and access.** Transitions use `--dur-*` and switch off under `prefers-reduced-motion`. Every control
  is keyboard-reachable with a visible focus ring, icons are `aria-hidden` with a text or `aria-label`, and touch
  targets are at least 40px on phones.

## Checklist for a new screen

1. Page header with title, one-line subtitle, breadcrumbs when nested, primary action on the right.
2. Pick the interaction from the data, not habit: segmented control for 2–4 options, chips for tags, toggle for
   on/off, combobox for long lists, master-detail or drawer instead of a separate edit page where it reads better.
3. Group form fields into sections, keep advanced settings behind disclosure, and validate inline.
4. Tables: sortable headers, filters in a toolbar, row actions in a compact menu, and a phone card layout.
5. Meaningful empty, loading (skeleton) and error states.
6. Check light, dark, a narrow phone (360px) and a tablet width before it ships.

---

# UI/UX Design Direction: Premium Modern SaaS / Enterprise Admin Experience

## 1. Design Philosophy

Design and implement a **modern, ultra-premium, enterprise-grade SaaS/Admin interface** with a strong emphasis on visual refinement, usability, responsiveness, and intentional interaction design.

The interface must feel like a **bespoke, professionally art-directed digital product**, not a generic admin template, conventional Bootstrap dashboard, or automatically generated UI.

Every component, spacing decision, interaction, and visual treatment should feel deliberate, cohesive, and polished.

The design should balance:

- **High information density without visual clutter.**
- **Compact layouts without sacrificing readability or usability.**
- **Visual sophistication without unnecessary decoration.**
- **Functional simplicity without looking basic or dated.**
- **Consistency without repetitive or monotonous component patterns.**

## 2. Visual Design Language

Adopt a **refined, contemporary SaaS design language** featuring:

- Restrained, sophisticated colour palettes with purposeful accent colours.
- Strong typographic hierarchy using modern, highly legible typefaces.
- Carefully calibrated spacing, alignment, proportions, and whitespace.
- Subtle surface differentiation, fine borders, and controlled elevation.
- Consistent border radii, iconography, component dimensions, and visual rhythm.
- Thoughtful use of density, grouping, contrast, and progressive disclosure.
- Minimal visual noise and an emphasis on content clarity.

Avoid excessive gradients, oversized cards, unnecessary shadows, large empty spaces, generic illustrations, and repetitive dashboard compositions commonly associated with AI-generated interfaces.

**The goal is understated sophistication, not visual extravagance.**

## 3. Component Design and Interaction Patterns

Move beyond a traditional form-driven interface that relies excessively on textboxes, dropdowns, and standard buttons.

Use a **diverse, context-appropriate component vocabulary** that reflects the nature of the information being displayed or captured.

Where appropriate, incorporate:

- Segmented controls and toggle groups for mutually exclusive selections.
- Searchable comboboxes and command palettes for complex selections.
- Inline editing for simple data modifications.
- Chips, tags, and selectable pills for categorisation and multi-selection.
- Stepper controls for numerical adjustments.
- Date-range selectors and calendar-based interactions.
- Contextual popovers, drawers, and expandable sections for secondary information.
- Master-detail layouts for browsing and editing records.
- Data tables with intelligent filtering, sorting, and row-level actions.
- Interactive status indicators, progress visualisations, and activity timelines.
- Contextual toolbars and compact action menus.
- Structured selection cards where visual comparison improves decision-making.

**Do not introduce component variety merely for aesthetic purposes.** Select the most appropriate interaction pattern based on the data type, user intent, frequency of use, and complexity of the task.

Forms should feel carefully composed rather than like a vertical stack of identical inputs.

## 4. Layout, Density and Information Architecture

Implement **compact, information-rich layouts with a strong sense of structure and hierarchy**.

Use logical grouping, concise labels, contextual supporting information, and appropriately sized controls.

Prioritise:

- Efficient use of horizontal and vertical screen space.
- Clear separation between primary and secondary actions.
- Contextual navigation and predictable interaction flows.
- Reduced scrolling through intelligent grouping and adaptive layouts.
- Progressive disclosure of advanced or infrequently used settings.
- Sticky toolbars or contextual actions where genuinely useful.
- Meaningful empty states, loading states, and validation feedback.

Avoid both extremes: oversized, unnecessarily spacious layouts and cramped interfaces with insufficient breathing room.

The application should feel **dense but comfortable, compact but elegant**.

## 5. Responsive and Adaptive Experience

Follow a **mobile-first, fully responsive, and adaptive design approach**, rather than merely shrinking desktop layouts to fit smaller screens.

Desktop, tablet, and mobile experiences should be individually considered.

On smaller screens:

- Reflow complex layouts according to available space.
- Transform wide data tables into usable mobile-friendly representations where appropriate.
- Adapt navigation into compact, accessible patterns.
- Use bottom sheets or full-screen selection interfaces when they improve usability.
- Maintain appropriate touch-target sizes and interaction spacing.
- Prioritise the most relevant content and actions.
- Avoid horizontal overflow, excessive scrolling, and unnecessarily nested containers.

The mobile experience must feel purpose-built, not like a compromised desktop interface.

## 6. Microinteractions and Product Polish

Introduce subtle, high-quality microinteractions that provide useful feedback and reinforce the perceived quality of the application.

Examples include:

- Smooth, restrained transitions between interface states.
- Refined hover, focus, pressed, selected, disabled, and loading states.
- Responsive inline validation with clear, actionable guidance.
- Skeleton loading for asynchronous content.
- Optimistic UI updates where technically appropriate.
- Contextual success and error feedback.
- Subtle animations that communicate hierarchy, continuity, or state changes.

Animations should be purposeful, restrained, and respectful of reduced-motion preferences.

## 7. Design System and Implementation Quality

Establish a cohesive, reusable **component-based design system** supported by design tokens for typography, spacing, colour, radii, elevations, sizing, and interaction states.

The design system should support:

- Consistent visual styling across modules.
- Accessible keyboard, screen-reader, and touch interactions.
- Reusable components with appropriate variants and density options.
- Dark and light themes where required.
- Predictable behaviour across different viewports.
- Long-term maintainability without compromising visual quality.

A component library may be used as an underlying accessibility or behaviour foundation, but its default appearance should not dictate the product's visual identity.

Avoid recognisable out-of-the-box Bootstrap styling and generic template aesthetics.

## 8. Expected Design Outcome

The final product should resemble a **mature, design-led SaaS platform built by a dedicated product design team**.

It should feel premium through its precision, visual hierarchy, component quality, intelligent use of space, and polished interaction patterns, rather than relying on excessive styling.

**The guiding principle:**

Build an interface that is visually distinctive but restrained, information-dense but readable, functionally rich but intuitive, and consistently refined across desktop and mobile.

Every design choice must have a purpose.
