Style guide: Service Fabric Explorer replacement

This guide describes the visual system used in the approved cluster overview, cluster map, and upgrade progress mocks. Build every screen from these rules so the app looks like one tool. It is written for an agent that will generate the UI.

1. Principles
   Quiet surfaces, dense information. Most of the screen is neutral. Color appears only to carry status or to mark the current action.
   Color encodes meaning, never decoration. Green is healthy or done, amber is warning or verifying, red is error or failed, blue is in progress or selected, gray is pending or neutral.
   Flat. No gradients, no drop shadows (except focus rings), no blur, no glow.
   One weight for emphasis: 500. Body text is 400. Never use 600 or 700.
   Sentence case everywhere, including headings and buttons.
   No emoji. Icons come from Tabler Icons, outline style only.
2. Tokens

Use CSS variables for every color. Never hard-code hex values in components, so light and dark mode work automatically.

Surfaces and text
Token	Use
--surface-2	Raised card or panel background
--bg-neutral	Metric cards, progress track, neutral pills
--text-primary	Body text, names, values
--text-secondary	Supporting text, metadata, sub-labels
--text-muted	Empty-state text and placeholders
--border	Default 0.5px hairline
--border-strong	Hover, open row, active chip
--border-accent	Selected map cell (2px)
--fill-ghost-selected	Background of an active filter chip
Status roles (background + text pair)
Meaning	Background	Text	Used for
Healthy, done	--bg-success	--text-success	Ok health, done domain
Warning, verifying	--bg-warning	--text-warning	Warning health, verifying domain
Error, failed	--bg-danger	--text-danger	Error health, failed domain
In progress, selected	--bg-accent	--text-accent	Upgrading domain
Pending, neutral	--bg-neutral	--text-secondary	Pending domain

Use the background-plus-text pair. Never place --text-* on a --fill-* solid for these status pills.

Progress and meters
Fill color: --fill-accent. Switch to --fill-warning when a meter passes 85%.
Track: --bg-neutral, 6px tall, 3px radius, overflow hidden.
Standalone fallback

If the app runs outside the chat host, define these variables yourself. Values come from the Claude color ramps (light mode):

Success: green-100
#C0DD97, green-800
#27500A
Warning: amber-100
#FAC775, amber-800
#633806
Danger: red-100
#F7C1C1, red-800
#791F1F
Accent: blue-100
#B5D4F4, blue-800
#0C447C
Neutral: gray-50
#F1EFE8, gray-600
#5F5E5A
Surface:
#FFFFFF. Text primary:
#1F1E1D. Border: rgba(0,0,0,0.12).

Provide dark-mode equivalents under @media (prefers-color-scheme: dark) using the 800 stop for backgrounds and the 100 stop for text.

3. Typography
   Family: the system UI sans (var(--font-sans)). Monospace (var(--font-mono)) only for node names, versions, and IDs when they appear in code-like contexts.
   Scale:
   Page or cluster title: 18px, weight 500
   Section label: 13px, --text-secondary, weight 400 (used as a section heading, not in caps)
   Card title and row name: 14px, weight 500
   Body and detail: 13px, weight 400
   Metadata and helper text: 12px, --text-secondary
   Metric value: 24px, weight 500
   Minimum font size is 11px. Line height is 1.5 for detail text.
   Do not use all-caps labels, eyebrow text, or tracked-out headings.
4. Spacing, radius, borders
   Vertical rhythm: 1rem between major blocks, 16px before section headings, 8px between list rows.
   Component-internal gaps: 6px (chips and button groups), 8px (row contents), 12px (card padding).
   Radius: var(--radius) (8px) for controls, pills, and rows. 12px for large cards only.
   Borders: always 0.5px solid var(--border). The one exception is 2px for a selected or featured item, using --border-accent.
   Shadows: none.
5. Layout
   Width: design for 380px first. Use at most two columns of cards or metrics: grid-template-columns: repeat(2, minmax(0,1fr)). Never three columns at this width.
   Map grids: 4 columns of square-ish cells (about 64px tall), with 6px gaps. Group rows by a label (e.g. node type) above each row.
   Wide layouts can increase columns on desktop, but keep the same spacing and component sizes.
   Long lists: no nested scrolling. The page scrolls as one.
   Use minmax(0,1fr) in grids so long names don't overflow.
6. Components
   Header block

Title on the left with a single-line secondary summary below it (name, counts, last refresh). Filter chips or overlay toggles on the right, wrapping on narrow widths.

Filter chips and overlay toggles
12px text, 4px 10px padding, --radius, 0.5px --border-strong border.
Active: background --fill-ghost-selected, aria-pressed="true".
Inactive: transparent background.
Use chips for status filters: All, Healthy, Warning, Error.
Metric cards
Background --bg-neutral, no border, --radius, padding 12px.
Label on top (12px, secondary). Value below (24px, 500).
Two per row on mobile.
List rows (nodes, applications, upgrade domains)
Full-width button element, transparent background, 0.5px border, --radius, padding 10px 12px, 8px bottom margin.
Left: a 18px Tabler icon. Center: name (500) over a secondary line (12px). Right: status pill and a chevron.
Chevron (ti-chevron-down, 16px) rotates 180° when open.
Open rows get --border-strong, and reveal details beneath the summary.
Put aria-expanded on the button. Each row must be a real button.
Expanded details
Start with any note in the status text color.
Then a single secondary metadata line (address, type, uptime).
Then meters, then a labeled list (hosted apps, services).
Use a hairline border-top between sub-items, with padding 8px 0.
Status pills
font-size: 12px; padding: 2px 8px; border-radius: var(--radius); white-space: nowrap.
Text is always the words "Healthy", "Warning", "Error", "Done", "Upgrading", "Verifying", "Pending", or "Failed". Never a symbol alone.
Map cells
Button, 64px tall, --radius, centered content: name (500) over the domain label.
Background and text come from the status role of the cell's current state.
Selected: 2px --border-accent. Unselected: 0.5px --border.
A detail line below the map shows the selected node's name, type, domain, and note. Show a neutral prompt when nothing is selected.
Progress list (upgrade domains)
Overall progress bar at the top of the section.
A state summary line beside the heading (e.g. "In progress · 1 of 4 domains").
Each domain is a row with a border-top, showing the domain name, its nodes in secondary text, and a status pill.
Action bar
Buttons: 13px text, 6px 12px padding, --radius, 0.5px --border-strong border, transparent background.
Disabled: opacity: 0.5, cursor: default, and the disabled attribute.
Order: primary progress action first, then pause/resume, then the destructive action (roll back), then reset. One filled primary action per view at most.
Icons

Tabler Icons outline webfont, loaded from cdnjs or the Tabler CDN:

html
<i class="ti ti-server" aria-hidden="true"></i>

Sizes: 18px for row icons, 16px for chevrons and inline icons, 24px max elsewhere. Icons are decorative (aria-hidden). Icon-only buttons need aria-label.

Icons used in the mock: ti-server (nodes), ti-apps (applications), ti-chevron-down (expand). Pick outline names only; never -filled.

7. Status vocabulary

Use these words exactly:

Concept	Words
Health	Healthy, Warning, Error
Upgrade domain	Done, Upgrading, Verifying, Pending, Failed
Overall upgrade	In progress, Paused, Complete, Rolled back

Do not invent new status words or use color without a word.

8. Copy rules
   Sentence case for every label, heading, and button.
   Buttons are verb-first and name the outcome: "Advance domain", "Pause", "Resume", "Roll back", "Reset demo".
   Helper text ends with a period. Labels and headings do not.
   Errors say what happened and what to do, without "Error:" prefixes or first-person apology.
   Empty states are a short invitation or a plain statement ("No nodes match this filter.").
   Numbers: use integers for counts and percentages without decimals.
9. Interaction patterns
   State lives in one plain object (for example state.filter, state.open, state.paused).
   Every change calls one render() function that rebuilds the affected sections from state. Do not mutate DOM piecemeal.
   Use event delegation on document with data-* attributes (data-filter, data-id, data-action). Check closest() and return early when nothing matches.
   Disabled actions must be ignored in the handler as well as in the markup.
   Selecting a row toggles it. Selecting the open row again closes it.
   Keep transitions out. Use motion only to show a state change the user caused.
10. Accessibility
    Include a visually hidden <h2> with a one-sentence summary at the top of each view.
    Use real <button> elements for every interactive item, with aria-pressed for toggles and aria-expanded for disclosures.
    Show visible focus rings using --focus-shadow (or a 2px accent outline) on keyboard focus.
    Color is never the only signal: every status has a word.
    Touch targets are at least 44px tall on mobile. Row buttons meet this with their padding.
11. Skeleton
    html
    <h2 class="sr-only">One-sentence summary of this view.</h2>
<div style="padding: 1rem 0; font-size: 14px; color: var(--text-primary);">
  <header>
    <div style="font-size:18px; font-weight:500;">Cluster name</div>
    <div style="font-size:12px; color:var(--text-secondary);">Counts · last refresh</div>
    <div id="filters"></div>
  </header>
  <div id="metrics" style="display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:8px;"></div>
  <div style="font-size:13px; color:var(--text-secondary); margin:16px 0 8px;">Section</div>
  <div id="list"></div>
</div>
<script>
const state = { filter: "All", open: null };
function render() { /* rebuild filters, metrics, and lists from state */ }
document.addEventListener("click", e => {
  const f = e.target.closest("[data-filter]");
  if (f) { state.filter = f.dataset.filter; render(); return; }
  const r = e.target.closest("[data-id]");
  if (r) { state.open = state.open === r.dataset.id ? null : r.dataset.id; render(); }
});
render();
</script>
12. Do and don't
Do	Don't
Color plus a word for every status	Color alone, or a symbol alone
500 for emphasis	600 or 700
Flat cards with hairline borders	Shadows, gradients, or blur
Sentence case	Title Case or ALL CAPS
Two columns on mobile	Three columns at 380px
aria-pressed, aria-expanded	Clickable divs
One primary action per view	Several equal-weight primary buttons
Tabler outline icons	Filled icons or hand-drawn SVG paths
CSS variables for every color	Hard-coded hex values in components
13. Web app: component catalog

Sections 1 to 12 describe the look. This section lists every component the web app (`sfx ui`, in web/) is built from, so new screens are assembled from these and nothing else. If a screen needs something that isn't here, add it here first.

13.1 Decisions
    sfx ui is a desktop control plane first: it's kept open on wide screens and has to show a lot at a glance. Phones still work, but they're the fallback, not the design target (sections 5 and 6 above were written for a 380px widget; where they disagree, this section wins).
    Navigation is a left sidebar. Cluster status lives on the overview, which is a dashboard of cards.
    Lists are dense: one bordered card per list with single-line rows in aligned columns (name, facts, reason) once the list is wide enough, wrapping to two lines when narrow.
    Every entity (cluster, node, application, service, partition) gets the same detail page: header, then tabs.
    Simple actions confirm in a small dialog. Changes with a form (parameters, version, scaling, cluster upgrades) open a side panel with two steps: edit, then review.
    Light, dark and system themes. The theme toggle sets data-theme on <html>; tokens are defined for both with light-dark().
    Unhealthy state is called "Errors and warnings" everywhere. Never "Why", "Issues" or "Problems".
    Page summaries state facts about the thing ("17 applications · 2 error"), never what the page is for, and never plumbing like config paths; when data was loaded goes in the refresh button's tooltip.

13.2 Shell

Sidebar
232px wide, full height, sticky; hairline right border, --surface-1.
Top to bottom: "sfx" in Cascadia Code's cursive italic (the one flourish); the context block (folder path in 11px secondary, the context name in 500 with its folder label pill; links to the Contexts page, also "c"); the pages, each an 18px Tabler icon and a 13px label, 34px tall, the current one with the ghost background and 500; at the bottom, after a hairline, the power toggle (amber pair while on, or a Read-only row), the theme button, and the version in muted 11px.
A running-operations count sits on Activity as a small accent badge.
Under the context block, "Find" with its shortcut (⌘K, or ctrl+K) in a hairline kbd on the right.

Find
A dialog near the top of the window (640px, 12vh down): one input, then up to 20 results as 40px rows (Tabler icon, name in 500, type or address under it, the kind on the right), the active one on the ghost fill. Arrows move, Enter opens the entity at its place under its application and service, Esc closes. A pasted partition id finds the partition. ctrl+F stays the browser's own find.
Under 1100px the sidebar collapses to 60px of icons with tooltips; under 640px it becomes a scrolling strip across the top.

Page
Fluid beside the sidebar, 32px side padding (24px under 1100px), content up to 1600px wide. Base text is 13px.

Notice
A one-line message under the top bar for the outcome of an action. Background and text from a status pair: success for "requested" and "done", danger for failures. Ends with a period. Has a close icon button. Success notices leave after 8 seconds; failures stay until closed. At most one at a time; a new one replaces the old.

13.3 Page structure

Page header
Breadcrumb (detail pages only), then the title row, then the summary line.
Breadcrumb: 12px, --text-secondary, links without underline (underline on hover), separated by ti-chevron-right at 12px. It lists the parents only; the current page is the title. Long names truncate with an ellipsis in the middle, never wrap.
Title: 18px, 500. Ids (partition and replica ids) in --font-mono.
Summary: 12px, secondary, facts joined with " · ", ending with "updated 14:02:31".
Right side, in this order: the health pill, the page's actions (see Action group), the refresh icon button (ti-refresh, aria-label "Refresh").

Tabs
Directly under the page header, full width, hairline bottom border.
Tab: 13px, 8px 12px padding, --text-secondary; the selected tab is --text-primary with a 2px --border-accent bottom border. role="tablist" and role="tab", aria-selected.
A tab can carry a count ("Replicas 3") in secondary text, or a status word in its status text color ("Health · Error"); never a colored dot alone.
Order on every detail page: Overview, Health, Events, Manifest (only where one exists), Details (raw JSON). Children (services, partitions, replicas) live on Overview.
The selected tab is in the URL (#/apps/orders?tab=health) so links and reloads keep it. A tab loads its data the first time it's opened.

Section
A section label (13px, secondary, 16px above, 8px below) with an optional aside on the right (a count, a link, a toggle). Sections never nest; use a group heading inside a section instead.

Group heading
Inside a list: 12px secondary label (node type, application) with the group's count on the right. 12px above, 6px below.

13.4 Showing data

Metric card: as 6. Metric cards. A card that links somewhere is an <a> and gets --border-strong on hover. The note line may use a status text color when it reports something unhealthy ("2 down").

Row: as 6. List rows, but dense: consecutive rows join into one card with hairlines between them, 40px tall, and once a row is 720px wide (a container query, so it works in dashboard cards too) its name, facts and reason sit in aligned columns on one line; pills have a fixed 72px width so the columns line up. Two variants that look the same:
    Disclosure row: a <button> with aria-expanded and ti-chevron-down; reveals details beneath.
    Link row: an <a> to another page with ti-chevron-right.
Anatomy, left to right: icon (18px, secondary), name (14px, 500) over facts (12px, secondary, " · "), optional reason line (12px, in the status text color) when the entity is unhealthy, then a right-aligned value (12px, secondary, e.g. "3 of 3"), the status pill, the chevron. Pills and chevrons are vertically centered on the first line, never stretched.
Hover: --border-strong. Rows are 8px apart.

List toolbar
Above a list: a search field (ti-search inside, clear button when not empty; "/" focuses it), then filter chips, then layout chips (List, Map) on the right. The result count goes in the section aside ("12 of 41").

Fields
A key-value list (<dl>). Two columns of pairs on wide screens, one on narrow. Label 40% width, secondary; value primary. Values that are ids, versions or paths use --font-mono and show a copy icon button on hover. A value that is another entity (a replica's node) is the link to it: plain text with a 14px ti-chevron-right, underlined on hover, rather than a separate "Open …" link. Missing values show "None" in muted text, never a dash.

Status pill: as 6. Status pills. Two additions for real data: "Unknown" (neutral pair) when the cluster doesn't report health, and the overall upgrade words "In progress", "Paused", "Complete", "Rolled back" (accent, warning, success, neutral).

Meter: as 6. Progress and meters, with the value on the right of the label line ("74 of 100 · 74%"). Metrics without a capacity are shown as fields, not meters. The one exception is comparing nodes on the Load tab: there a metric without a capacity gets grey bars relative to the busiest node (never the accent fill, which means "how full"), and a metric no node reports is one line, not a list of zeros. Metric names drop the built-in "servicefabric:/_" prefix everywhere.

Primary button: white text on --fill-button (#185fa5) in both themes; the dark theme's lighter accent is too pale behind white text.

Loading placeholders: in the shape of what's coming. A page gets crumbs, title, summary and the tab line, then joined 40px rows; the Overview gets six metric cards and dashboard cards. A first load that fails says so as a sentence ("Can't reach the cluster. Connection refused.") with Try again.

Node map: as 6. Map cells.

Domain strip
For an upgrade: one cell per upgrade domain in a row (equal widths, 6px gap, 32px tall, --radius), labelled "UD 0", colored by the domain's state pair. The current domain has the 2px --border-accent. Under it, the current domain's nodes: name, phase, and pending safety checks in --text-warning ("waiting on EnsurePartitionQuorum").

Upgrade card
Title row: target and versions ("fabric:/orders 1.0.0 to 1.1.0"), the overall state pill on the right. Summary: mode, started, health check phase. Then the overall progress bar, the domain strip, errors and warnings if any, and the upgrade's actions. Used on the upgrades page, the overview (without actions) and an application's Overview tab.

Timeline row
For health events, cluster events and activity. Three columns: time (fixed 88px, 12px secondary, no wrap: "14:02" today, "19 Nov 14:02" before), the text (title 13px 500, then a 12px secondary line, then the description in body text, clamped to 3 lines with "Show more"), and the status pill on the right, aligned with the title. Rows are separated by hairlines. Long timelines get day headings (Today, Yesterday, 18 Nov) as group headings.

Errors and warnings
The chain of health evaluations explaining a state. Collapsed by default behind a disclosure button: "Errors and warnings" followed by the counts ("3 errors · 1 warning") in secondary text. Expanded, each evaluation is a line: its status pill in a fixed 64px column, then the description, aligned with the pill's first line; children are indented 16px per level with a hairline guide on the left. Status is always a pill, never a colored word alone. More than 5 siblings collapse into "Show 12 more".
On the overview it sits below the metric cards. On a detail page's Overview tab it sits below the header, collapsed; the Health tab shows it expanded, followed by the health events timeline.

Document
Manifests and raw JSON. A full-width block on --surface-2 with a hairline border and --radius, 12px --font-mono, line height 1.5, scrolling sideways only (the page scrolls vertically). A toolbar above it: the format ("XML", "JSON"), then copy and download icon buttons, and a wrap toggle chip. XML and JSON are syntax-highlighted with the --code-* tokens (tag, attribute, string, key, number, literal, comment, punctuation), which stay clear of the status hues so a tag never reads as a health state.

Empty state: 13px, --text-muted, one sentence ("No replicas match this filter."), optionally followed by a link that resolves it ("Clear the filter").

Loading: placeholder blocks shaped like the content (rows, cards, fields) in --bg-neutral, without animation. Never a spinner, never "Loading…" text for more than a field.

Stale and failed data
Data reloads every 10 seconds while the tab is visible. While reloading, the old data stays and nothing moves. When a reload fails, a danger notice sits above the content saying what failed and that the data shown is from the last success ("Couldn't reach the cluster. Showing data from 14:02.").

13.5 Acting

Button: as 6. Action bar, plus:
    Primary: --fill-accent background, --surface-2 text. At most one per view, and only for the step that moves a flow forward ("Review changes", "Upgrade").
    Danger: --text-danger text on a transparent background; filled with the danger pair only as the final button of a destructive confirmation.
    Icon button: 32px square, transparent, no border, an 18px icon, required aria-label; hover background --fill-ghost-selected.
    Hover on any button: --fill-ghost-selected background.

Action group
An entity's actions, in the page header, right-aligned (or at the bottom right of an expanded row for row-level actions such as a replica's).
Show up to two non-destructive actions as buttons, most common first. Everything else, and every destructive action, goes in a "More actions" menu (icon button, ti-dots). Read-only contexts show no actions at all, not disabled ones.

Menu
A popover under its button: --surface-2, hairline border, 12px radius, 4px padding. Items are full-width buttons, 13px, 36px tall, with an optional 16px icon. Destructive items come last, after a hairline, in --text-danger. Arrow keys move, Enter picks, Esc closes and returns focus to the button.

Confirm dialog
For actions without a form. A centered modal (<dialog>), 440px wide, 12px radius, 16px padding.
Title: what will happen, verb first, naming the target ("Restart replica 1234 on _Node_1").
Effect: one line in secondary text saying what it causes ("Its process restarts; a primary hands over to a secondary first.").
Inputs, if any (see Form fields).
Destructive actions add "Type _Node_1 to confirm." with a text field; the confirm button stays disabled until it matches.
Buttons, left to right: the action (primary, or filled danger when destructive) named as the title's verb ("Restart"), then "Cancel". Esc cancels. Errors appear inside the dialog, which stays open.

Side panel
For changes with a form. Slides in from the right over the page with a dimmed backdrop; 520px wide, full width under 600px. The page behind doesn't scroll; the panel body does (the one allowed nested scroll).
Header: title naming the change and the target ("Change fabric:/orders"), a close icon button, and the steps "Edit" and "Review" as text, the current one in 500.
Edit step: the form. Changed fields are marked (see Form fields). The footer has "Review changes" (primary, disabled until something changed) and "Cancel".
Review step: the diff, then the effect, then how to roll it out (for upgrades: the mode, with the policy behind a disclosure), then the confirmation for destructive changes. The footer has the action named by its outcome ("Upgrade fabric:/orders"), and "Back".
Closing with unsaved edits asks "Discard your changes?" first.

Form fields
Label above (12px, secondary), the control, then help text below (12px, secondary, ending with a period), or the validation error in --text-danger in its place.
Controls are 36px tall, --surface-2, 0.5px --border-strong, --radius, focus ring on keyboard focus.
Types: text, number, choice (chips when there are 4 or fewer options, a select otherwise), flag (checkbox), node (a select grouped by node type, showing UD and status, with "Let the cluster choose" first), version (a select with "current" marked).
A changed field gets a 2px --border-accent left edge, the helper "Was 2.", and a revert icon button (ti-arrow-back-up).
Validation happens as you type; the review step can't be reached while a field is invalid.

Diff
In the review step: one row per change. Name (mono, 12px), then old value (secondary), ti-arrow-right, new value (primary, 500). Additions say "Added", removals "Back to default (x)". Unchanged fields aren't listed; a line under the diff says how many are unchanged.

13.6 Page templates

List page: page header (no breadcrumb), metric cards when they help, list toolbar, grouped rows.
Types page: types as group headings, one disclosure row per version (status, applications using it, parameters); opened, it lists the applications as links, the service types and the default parameters, with create application and unprovision. "Provision a package" (primary) opens a side panel to pick a folder or an .sfpkg/.zip, reads the manifest's type and version before uploading, and the upload's progress shows on Activity.

Image store page: breadcrumbs down the path, the disk meter at the root, then folders (packages with their type, version and whether provisioned; Service Fabric's own folders marked and never deletable) and files, sizes on the right. Rows open folders; their actions sit beside the link, not in it.

Detail page: page header with breadcrumb, actions and refresh; tabs. The Overview tab shows errors and warnings (collapsed), key facts (fields), load (meters), then children as link rows. Once the page is 960px wide (a container query), it splits: errors and warnings and the children on the left, facts and load in a 320px column on the right, one field per line. Narrower, the facts come first, in one column.
Overview (the cluster's page): a dashboard. Metric cards across (nodes, applications, services, partitions, replicas, upgrades running), then a 12-column grid of cards: Health (a stacked health bar per entity kind, then errors and warnings, collapsed) and Unhealthy (link rows with reasons); Nodes (a compact map by upgrade domain, with the cluster's node types, domains and seed nodes in a line under it) and Upgrades (running ones with their domain strips); Load (meters for metrics with a capacity), Recent changes (one line per change) and Events (only when the cluster has an event store). Every card's aside links to the page with the rest. The cluster manifest and cluster actions are on the cluster's tabs and menu, like any other entity.

Card: a dashboard tile on --surface-2 with a hairline border and 12px radius; title (13px, 500) and aside (12px, secondary, often a link) on one line, then the content. Spans 3 to 12 of the dashboard's 12 columns on wide screens; half width under 1100px; full width on a phone.

Health bar: an entity kind's name, its counts in words ("1 error · 2 warning · 13 healthy"), and one 8px stacked bar of the status backgrounds. Color is never the only signal: the words are always there.

13.7 Code

The catalog maps one to one onto web/src, so a screen is assembled, not styled:
    styles/tokens.css: every token, each color defined once with light-dark(); the theme sets color-scheme.
    styles/base.css: reset, text and status helpers (.secondary, .mono, .tone-*, .text-*), layout helpers.
    ui/: one component per file with its own CSS file next to it (Row, PageHeader, Tabs, Fields, Timeline, Overlay…). Components take data, never fetch.
    actions/: the action group, the confirm dialog, and the side panel flow (ChangePanel) with one editor component per change in actions/editors.
    lib/: no UI. api (requests), resource (useResource: load, poll, keep stale data), router (hash path, query state, href builders), format, health, upgrades, theme, highlight, types.
    views/: one file per page, composed from ui/ and actions/.
Rules: no inline styles except data-driven sizes (a progress width); no colors outside tokens.css; page state that should survive a reload (tab, filters, search) lives in the URL through useQueryParam.
