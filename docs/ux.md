# sfx UX design

Status: draft · Covers: the whole interactive UI and the command line · Builds on: 0.0.2

sfx is to Service Fabric what k9s is to Kubernetes: the fastest way to understand a cluster and act on it from a
terminal. This document describes what it should look and feel like when it's done, so features land in one
coherent shape instead of one at a time.

## Contents

1. [Principles](#1-principles)
2. [What 0.0.2 gets right, and what to change](#2-what-002-gets-right-and-what-to-change)
3. [Screen anatomy](#3-screen-anatomy)
4. [Navigation and commands](#4-navigation-and-commands)
5. [Keys](#5-keys)
6. [Views](#6-views)
7. [Health](#7-health)
8. [Slices: filtering, grouping and cross-cutting views](#8-slices-filtering-grouping-and-cross-cutting-views)
9. [Changing things: one flow for every change](#9-changing-things-one-flow-for-every-change)
10. [Upgrades](#10-upgrades)
11. [Configuration changes](#11-configuration-changes)
12. [Lifecycle: provision, create, scale, delete](#12-lifecycle-provision-create-scale-delete)
13. [Operations on nodes, partitions and replicas](#13-operations-on-nodes-partitions-and-replicas)
14. [Events, history and repairs](#14-events-history-and-repairs)
15. [Safety](#15-safety)
16. [Live data and performance](#16-live-data-and-performance)
17. [Contexts, auth and settings](#17-contexts-auth-and-settings)
18. [The command line](#18-the-command-line)
19. [Phasing](#19-phasing)
20. [Open questions](#20-open-questions)
21. [Cellar: what moves into the framework](#21-cellar-what-moves-into-the-framework)

---

## 1. Principles

1. **Answer "is it OK, and if not, why?" in one glance.** Every screen shows cluster health, and every unhealthy
   thing shows its reason next to it. A red cell without a reason is a bug.
2. **Everything is a table you can slice.** Any list can be filtered, sorted and grouped by any column, and the same
   filter language works everywhere: in the UI, in `:` commands and on the command line.
3. **One way to change things.** Edit, see the diff, pick how to roll it out, confirm, then watch it happen. This
   flow is the same whether the change is a parameter, a service setting, an upgrade or a naming property.
4. **Never a dead end.** Every entity links to its parents, its children, its node, its health and its upgrade.
   You can always go *to* whatever is related, and always go *back*.
5. **Safe by default, fast when you mean it.** Reading is free; changing asks; destroying makes you type the name.
   Read-only contexts can't change anything, and the riskiest actions stay locked until unlocked.
6. **Live.** Data refreshes on its own and changes are highlighted. Long-running work (upgrades, moves, restarts)
   shows progress where you are, without switching screens.
7. **The keyboard is the interface, the mouse is a courtesy.** Every action has a key or a `:` command, and appears
   in `?` help and the `ctrl+p` palette from the same declaration (as 0.0.1 already does).
8. **Discoverable.** The footer shows what the selected row offers. Nothing should need the README.
9. **The UI and the command line do the same things.** Every action lives in `Sfx.Core` once; the TUI and the
   CLI are two front ends to it.

---

## 2. What 0.0.2 gets right, and what to change

**Keep**

- One declaration per action, driving keys, `:` commands, palette, help and footer.
- Risk levels (none, confirm, type-to-confirm), read-only contexts, `:power`, and the action log.
- The health header, `:health` with reasons, `ctrl+f` find, `!` for the last error, `o` to jump to a node.
- The form dialog (edit in place, revert one field, apply all at once) and the upgrade editor that shows every
  parameter of the target version.
- Consistent verbs: `e` edit, `u` upgrade, `s` scale, `ctrl+n` new, `ctrl+d` delete, `ctrl+r` restart.

**Change**

| In 0.0.2 | Problem | Instead |
| --- | --- | --- |
| Creating an app asks the name, then all parameters as one `a=b; c=d` line in the footer; creating a service asks name, count and partitions one prompt at a time | Two styles of input for the same kind of task; typing parameters blind; can't go back a step | Every multi-field input is a **form** (§9). The footer prompt is only for a single value. |
| `:set` means both "app parameters" and "service settings"; `:resume`, `:rollback` and `:upgrade-mode` exist for both apps and the cluster | The same word does different things depending on the page | One grammar (§4): **views are nouns, actions are verbs on the selection**. Verbs act on what's selected; the cluster is selectable like anything else. |
| Resuming a manual upgrade asks for the domain name as free text | You have to know or guess the name | Resume shows the domains with their state; pick one (the next is preselected). |
| Upgrade progress is a tab on the describe page | You lose it as soon as you navigate; you can't see several upgrades at once | A first-class **upgrades view** (§10) and an **activity strip** that follows you around. |
| Upgrade plans are four presets | Real upgrades need the actual timeouts and health policy | Presets fill a form with every policy field; change any of them. |
| Health shows a colored word per row | When everything is red (screenshot: every app in Error), the color says nothing | Health cells carry the reason ("Error · 2/3 partitions"), and `w` explains any row (§7). |
| `:health` and `:cluster-health` | Two names for halves of one question | One health view, with the evaluation tree and the unhealthy list as two modes (§7). |
| Digits switch describe tabs | Fine, but they're the obvious key for jumping between top-level views | `tab` / `shift+tab` and `[` / `]` switch tabs; digits jump to views (§5). |
| The command line mixes host options and auth options in one `--key value` space; `sfx <endpoint>` connects but `sfx context add` saves | Hard to discover; typos become auth options | Subcommands and declared options (§18). |

---

## 3. Screen anatomy

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ prod-weu  SF 10.1.2493  ● Warning · 2 apps, 1 node    nodes 12/12   apps 41   ⟳ 2 upgrades  │ header
│ apps › fabric:/orders › Counter                                              read-only    │ breadcrumb
├─ partitions [5] /role=Primary ───────────────────────────────────────────────────────────────┤
│ ID                                    KIND      REPLICAS  STATUS  HEALTH                     │
│ 1f0c…a3  Int64 0–204                  Stateful  3/2       Ready   ● Ok                       │ table
│ 7be2…19  Int64 205–409                Stateful  3/2       Ready   ● Error · quorum loss      │
│ …                                                                                            │
├─ preview: partition 7be2…19 ─────────────────────────────────────────────────────────────────┤
│ Error since 14:02 (4m) · System.FM: partition is in quorum loss, 1 of 3 replicas up           │ preview
│ replicas: _Node_3 Primary Down · _Node_1 Secondary Up · _Node_4 Secondary Down               │ (p toggles)
├──────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⟳ fabric:/orders 1.0.0→1.1.0 UD 2/5 · health check 1:42 left        restart replica 12345… ✓│ activity
│ updated 14:06:31            enter open  d describe  w why  ctrl+r restart  m move  ? help    │ footer
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Header** (always): context, cluster version, aggregated health with *what* is unhealthy, node and app counts,
  and in-flight upgrades. Each part is clickable and has a `:` command (`:health`, `:nodes`, `:upgrades`). A
  context can set a color and a label (`PROD`), shown here and as the border color, so production never looks like
  dev.
- **Breadcrumb**: where you are; click or `backspace` to go up. Shows the mode: `read-only`, `POWER`.
- **Table title**: the view, row count, and the active filter and grouping.
- **Preview** (`p` toggles, off by default on small terminals): the selected row's health reason and key facts,
  so you can scan a list without opening each row. Same content as the describe page's Overview, condensed.
- **Activity strip**: operations in flight and the last outcomes, one line each, newest right. Hidden when idle.
  `ctrl+a` opens the full activity list (§14).
- **Footer**: status on the left (updated time, or errors, or the open prompt); on the right the keys the
  *selected row* offers, most useful first. When the terminal is narrow, keys drop off the right first.

Sizing: everything works at 80×24. The preview pane appears automatically from about 40 rows, and side by side
instead of below from about 200 columns.

---

## 4. Navigation and commands

### Moving around

- **Drill down**: `enter` opens the row's children (app → services → partitions → replicas), or its describe page
  when it has none.
- **Describe**: `d` opens the details of the selected row: tabs for overview, health, events, manifest, load, raw
  JSON. Its actions are the same as the row's.
- **Go to related**: `o` offers what's related to the selection: its node, its application, its service, its
  primary, its upgrade, its type. With one candidate it goes straight there.
- **Back**: `backspace`/`h`/`esc`. The breadcrumb remembers the selection and scroll position of every level.
- **Jump**: `ctrl+f` searches every node, application, service and partition id and goes there, building the
  breadcrumb on the way (0.0.2). Also accepts addresses (`10.0.0.4`), replica ids and service DNS names.
- **Marks**: `ctrl+o`/`ctrl+i` go back and forward through the places you've been (like vim's jump list), across
  views and contexts.

### Command grammar

`:` opens the command line. Commands are either a **view** (a noun) or an **action** (a verb).

```
:<view> [filter…]          go to a view, optionally filtered
:<verb> [arguments]        act on the selected row (or the page, for page actions)
```

Views have a full name and short aliases, like k9s resources:

| View | Aliases | |
| --- | --- | --- |
| `:overview` | `:ov`, `:home` | cluster dashboard (§6) |
| `:nodes` | `:no` | nodes |
| `:apps` | `:app`, `:ap` | applications |
| `:services` | `:svc` | every service in the cluster |
| `:partitions` | `:pt` | every partition |
| `:replicas` | `:rep`, `:rs` | every replica |
| `:types` | `:at` | application types and versions |
| `:health` | `:he` | what's unhealthy and why |
| `:upgrades` | `:up` | cluster and application upgrades |
| `:events` | `:ev` | cluster events |
| `:repairs` | `:rt` | repair tasks |
| `:imagestore` | `:is` | image store contents |
| `:naming` | `:nm` | naming store |
| `:cluster` | `:cl` | cluster details, manifest, settings, load |
| `:load` | `:ld` | load per metric and node |
| `:placement` | `:pl` | where replicas are, as a matrix |
| `:activity` | `:act` | operations sfx started, and the action log |
| `:contexts` | `:ctx` | saved clusters; `:ctx prod` switches |

Filters after a view use the slice language (§8): `:replicas role=Primary node=_Node_3`, `:apps health!=Ok`,
`:services app=fabric:/orders`. `tab` completes view names, verbs, column names and values.

Verbs are the same words everywhere and always act on the selection: `:upgrade`, `:rollback`, `:resume`,
`:edit`, `:scale`, `:restart`, `:move`, `:delete`, `:activate`, `:deactivate`, `:recover`, `:report-health`.
The cluster is an entity like any other: on `:cluster` (or the header), `:upgrade` upgrades the cluster. That
removes 0.0.2's duplicated `:resume` / `:provision-cluster` style commands.

The **palette** (`ctrl+p`) lists every view and every action available here, fuzzy-matched, with its key. It's the
answer to "how do I…?".

---

## 5. Keys

Global keys never change meaning. Entity keys mean the same thing on every kind of entity that supports them;
the footer shows which ones the selected row offers.

**Global**

| Key | |
| --- | --- |
| `:` | command line |
| `/` | filter rows (in tables) or search text (in describe pages); `n`/`N` next/previous match |
| `ctrl+p` | palette: every view and action |
| `ctrl+f` | jump to any entity |
| `?` | keys for this page |
| `1`–`7` | overview, nodes, apps, health, upgrades, events, types |
| `c` | contexts |
| `r` | refresh now |
| `y` | copy: the selected name; `Y` copies the row as JSON |
| `!` | the last error in full |
| `ctrl+a` | activity |
| `p` | toggle preview pane |
| `q`, `ctrl+c` | quit |

**Moving**

| Key | |
| --- | --- |
| `j`/`k`, arrows | down/up |
| `g`/`G`, `home`/`end` | first/last row |
| `ctrl+d`/`ctrl+u` in describe pages, `pgdn`/`pgup` | half page |
| `enter`/`l` | open |
| `backspace`/`h`/`esc` | back (`esc` clears the filter first) |
| `tab`/`shift+tab`, `[`/`]` | next/previous tab |
| `ctrl+o`/`ctrl+i` | back/forward through the jump list |
| `space` | mark the row (for bulk actions, §9) |
| `shift+←`/`shift+→` | sort by the previous/next column; again flips the order |

**On the selected entity**

| Key | Meaning | Applies to |
| --- | --- | --- |
| `d` | describe | everything |
| `w` | why: explain its health | everything with health |
| `o` | go to related | everything |
| `e` | edit its settings/parameters | apps, services, naming properties, contexts |
| `u` | upgrade, or control the running upgrade | apps, cluster |
| `s` | scale | services |
| `m` | move | replicas, primaries of a partition |
| `x` / `a` | deactivate / activate | nodes |
| `ctrl+r` | restart | nodes, replicas, code packages, partitions |
| `ctrl+k` | remove | replicas, node state |
| `ctrl+d` | delete | apps, services, types, naming entries |
| `ctrl+n` | new | on lists: a new app, service, property… |
| `=` | group the list by the selected column | tables |

`ctrl+d` means delete on rows and half-page in text views; they never share a screen.

---

## 6. Views

**Overview** (`1`, `:overview`, the start page). The answer to "how's the cluster?" on one screen:

```
┌ prod-weu ────────────────────────────────────────────────────────────────────────────────────┐
│ ● Warning   2 of 41 apps unhealthy · 1 of 12 nodes · cluster health policy: 0% unhealthy apps │
├ nodes ─────────────────────────────┬ upgrades ─────────────────────────────────────────────────┤
│ UD  0 ■■■  1 ■■■  2 ■■□  3 ■■■     │ ⟳ fabric:/orders  1.0.0→1.1.0  UD 2/5  health check 1:42  │
│ FD  0 ■■■■  1 ■■■■  2 ■■□■          │ ⟳ fabric:/search  1.1.0→1.2.0  UD 4/5  waiting for you    │
│ ■ up  □ down/disabled  ▲ warning    │ ✓ cluster 10.1.2493 (3 days ago)                          │
├ unhealthy ─────────────────────────┴───────────────────────────────────────────────────────────┤
│ ● Error    app      fabric:/legacy-worker   2 code packages failed to start (exit 1)          │
│ ● Error    service  fabric:/search/Counter  p99 latency 2.4s, above the 1s objective         │
│ ▲ Warning  node     _Node_2                 Memory use at 91% for 15 minutes                  │
├ load ──────────────────────────────────────────────────────────────────────────────────────────┤
│ Ticks      ▁▂▃▅▂▁▂▃  balanced      Requests  ▁▁▇▂▁▁▁▁  node _Node_2 at 74% of capacity      │
├ recent events ─────────────────────────────────────────────────────────────────────────────────┤
│ 14:02  partition 7be2… lost quorum · 13:58 _Node_3 went down · 13:40 orders upgrade started   │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Every block is a link: `enter` on a block (or its number) opens the full view.

**Lists** are the core; every list is a table that can be filtered, sorted and grouped (§8):

| View | Columns (default; more with `:columns`) | Opens |
| --- | --- | --- |
| Nodes | name, status, health+reason, type, UD, FD, seed, version, uptime, replicas, load | deployed apps on it |
| Apps | name, type, version, status, health+reason, upgrade progress | services |
| Services | name, type, kind, partitions, replicas/instances, status, health+reason | partitions |
| Partitions | id, key range or name, replicas (target/min), status, health+reason, primary node | replicas |
| Replicas | id, node, role, status, health+reason, address, since | its describe page |
| Types | name, version, status, apps using it, params | its apps |
| Deployed apps, packages, code packages (per node) | as 0.0.2, plus restarts and last exit | down the hierarchy |
| Upgrades, events, repairs, image store, naming, activity | §10, §14, §12, §11 | |

**Describe pages** are tabs, loaded when shown and refreshed while shown: Overview, Health (evaluation tree and
events), Events (this entity's), Load, Manifest (or Description), Upgrade (for apps and the cluster), Raw (JSON).
Pages add their own tabs: an app type has Parameters, Service types and Apps; the cluster has Settings, Config,
Health policy and Load.

---

## 7. Health

The question is always "what's wrong, since when, and why?". Three tools answer it:

**1. The health cell.** Everywhere a health state appears, it carries a short reason and an age:

```
● Ok
▲ Warning · Disk 87% full · 2h
● Error · 2/3 partitions in quorum loss · 4m
● Error · from children: 3 services
```

The reason is the first unhealthy evaluation, or the worst health event (source and property), shortened. When
the cause is in the children it says so, with counts. Symbols as well as colors, so it works without color.

**2. `w`: why.** On any row, opens the evaluation chain from that entity down to the events that cause it:

```
┌ why is fabric:/search Error? ──────────────────────────────────────────────────────────────────┐
│ ● fabric:/search                  Error   1 of 2 services unhealthy (policy: 0% allowed)       │
│   ● Service fabric:/search/Counter Error  event from sfx-demo                                  │
│     └ Latency  Error  "p99 latency 2.4s, above the 1s objective"  reported 14:01, never expires│
│   ● Service fabric:/search/Frontend Ok                                                         │
│                                                                                                │
│ enter: open the entity   c: clear this report (if sfx's own)   esc: close                      │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

It includes the **policy** that turned the children's health into the parent's (e.g. "MaxPercentUnhealthy
Applications: 0%"), because "why is the cluster Error when only one app is?" is the most common question.

**3. The health view** (`4`, `:health`), with two modes (`tab`):

- **Unhealthy** (default): every unhealthy node, app, service, partition, replica and deployed package, worst first,
  with reason and age. Enter goes to it. This is 0.0.2's `:health`, extended down to partitions and replicas.
- **Tree**: the whole health hierarchy, collapsible (`←`/`→`), with healthy branches collapsed. This replaces
  `:cluster-health`.

Filters apply: `:health kind=partition`, `:health app=fabric:/orders`, `:health source=System.FM`.

**Acting on health**: `:report-health` sends a report (state, source, property, description, TTL) to the selected
entity, for testing alerts or flagging something for others; `c` in the why view clears a report sfx sent. Health
events show TTL and whether they expire.

**Implementation**: one call to the health chunk API (`POST /$/GetClusterHealthChunk` with filters) returns the
whole tree, instead of one call per entity as 0.0.2 does; the reasons for visible rows are fetched lazily.

---

## 8. Slices: filtering, grouping and cross-cutting views

"Show me everything on node 3", "all primaries in UD 2", "every replica of DemoAppType that isn't Ready": these are
slices. They come from three pieces that work together.

### 8.1 One filter language

Used by `/`, by `:view <filter>`, and by the command line's `--where`:

```
text                    any column contains "text" (what / does today)
col=value               equals (case-insensitive); col!=value
col~text                contains; col!~text
col>n  col<n            numbers and durations (uptime>1d, restarts>3)
a=x b=y                 and
col=x,y                 or, within one column
health>=Warning         ordered states: Ok < Warning < Error
```

Columns can be referred to by header or alias (`node`, `role`, `health`, `ud`, `fd`, `type`, `app`, `svc`,
`status`, `version`). `tab` completes names and the values present in the data. The active filter shows in the
table title and the breadcrumb, and `esc` clears it.

### 8.2 Group by

`=` on a column (or `:group ud`) groups the table by that column's value, with counts and the worst health per
group; `enter` on a group header collapses it. `:group` alone removes grouping.

```
┌ replicas [64] group: node ─────────────────────────────────────────────────────────────────────┐
│ ▼ _Node_0   14 replicas · 5 primaries   ● Ok                                                   │
│     12345…  fabric:/orders/Counter  Primary    Ready  ● Ok                                     │
│     …                                                                                          │
│ ▶ _Node_1   13 replicas · 4 primaries   ● Ok                                                   │
│ ▼ _Node_3   12 replicas · 0 primaries   ● Error · node down                                    │
```

### 8.3 Cross-cutting views

The hierarchy (app → service → partition → replica) is how SF stores things, but problems cut across it. These
views flatten it:

- `:services`, `:partitions`, `:replicas` list **every** service, partition or replica in the cluster, with their
  owners as columns, so `:replicas node=_Node_3 role=Primary` answers "what's primary on node 3?".
- **On a node**: `enter` on a node shows what's deployed there; `:replicas node=<name>` shows every replica on it,
  across apps.
- **Upgrade and fault domains**: `:nodes` grouped by `ud` or `fd`, and `:replicas ud=2` ("what restarts if UD 2
  goes down").
- **Node types**: `:nodes group=type`, with capacity and load per type.
- **App types**: `:apps type=DemoAppType`, and on a type, every app and version using it.
- **Placement** (`:placement`, also a tab on an app or service): partitions as rows, nodes as columns, the cell is
  the replica's role. Shows imbalance, partitions without enough replicas, and what a node going down would cost.

```
┌ placement: fabric:/orders/Counter ─────── nodes: _Node_0  _Node_1  _Node_2  _Node_3  _Node_4 ─┐
│ 1f0c…  0–204                               P        S        S        ·        ·           │
│ 7be2…  205–409                             ·        S        ·        P↓       S↓          │
│ 30aa…  410–614                             S        ·        P        S        ·           │
│ P primary  S secondary  I instance  ↓ down  · none        UD:   0        1        2        3        4 │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Load** (`:load`): per metric, per node: capacity, load and balance, as bars; per partition the top consumers.
  Group or filter like any table.

**Saved slices**: `:save <name>` stores the current view, filter, grouping and sort; `:<name>` opens it again.
They're kept per context in the config file, so a team can share "primaries-on-ud2" style views.

---

## 9. Changing things: one flow for every change

Every change, from a parameter to a cluster upgrade, goes through the same five steps. Simple changes skip the
steps that don't apply.

```
  1. Edit  ──▶  2. Review  ──▶  3. Roll out  ──▶  4. Confirm  ──▶  5. Track
  a form        the diff        how (upgrades       y/n, or type      in the activity strip,
                                only)               the name          the upgrades view, the log
```

**1. Edit: forms.** Any input with more than one value is a form (0.0.2's `FormDialog`, used everywhere):

```
┌ edit fabric:/orders/Counter ───────────────────────────────────────────────────────────────────┐
│   Target replicas            3                                                                 │
│ › Min replicas               2 → 3            changed                                          │
│   Replica restart wait       (cluster default)                                                 │
│   Quorum loss wait           (cluster default)                                                 │
│   Placement constraints      NodeType == apps                                                  │
│   Default move cost          Low  ◀ ▶                                                         │
│                                                                                                │
│ enter edit  u revert  ctrl+s review  esc cancel           Min replicas: a whole number ≥ 1     │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Fields know their type (number, duration, choice, text, flag), validate as you type, show the default and the
current value, and explain themselves in the footer. Choices cycle with `←`/`→` or open a list with `enter`.

**2. Review: the diff.** Before anything is sent, sfx shows exactly what changes, and what it will cause:

```
┌ review: update fabric:/orders/Counter ─────────────────────────────────────────────────────────┐
│ Min replicas      2 → 3                                                                        │
│                                                                                                │
│ Effect: applied immediately, no upgrade. Partitions with fewer than 3 replicas up will be      │
│ below their minimum and stop accepting writes until replicas come back.                        │
│                                                                                                │
│ enter continue   b back to the form   esc cancel                                               │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The effect line matters most: "rolling upgrade, services restart domain by domain", "applied immediately",
"replicas are added", "requires an app upgrade". It comes from what the change is, not a generic warning.

**3. Roll out** (upgrades only): the upgrade policy form (§10.2).

**4. Confirm**: as today: `y` for changes, typing the target's name for destructive ones.

**5. Track**: the operation appears in the activity strip; long-running ones (upgrades, scaling, moves) show
progress until done, and the outcome goes to the action log.

**Bulk**: mark rows with `space` (or `*` to mark all filtered rows), then act: the review lists every target, and
operations run one at a time by default, stopping at the first failure. For node operations, bulk actions offer
"one upgrade domain at a time, waiting for health between domains".

**Undo**: where an inverse exists, the outcome offers it: deactivate → activate, scale → scale back, parameter
change → change back. `ctrl+z` runs the last one, after confirming.

---

## 10. Upgrades

Upgrades are the riskiest routine operation, so they get the most visibility.

### 10.1 Upgrades view (`5`, `:upgrades`)

Every upgrade, cluster and applications, running or recent:

```
┌ upgrades [3] ─────────────────────────────────────────────────────────────────────────────────┐
│ TARGET            FROM → TO        MODE              PROGRESS          STATE                  │
│ fabric:/orders    1.0.0 → 1.1.0    monitored, rb     ■■□□□ UD 2/5      health check 1:42      │
│ fabric:/search    1.1.0 → 1.2.0    manual            ■■■■□ UD 4/5      waiting for you        │
│ cluster           10.1 → 10.1 cfg2 monitored, rb     ■■■■■             done 3 days ago        │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### 10.2 Starting an upgrade

`u` on an app (or on the cluster) opens one form with three sections, then the review:

1. **Target**: version, picked from the provisioned ones, with what changed between them: which service packages,
   code, config and data packages differ (from the manifests), so you know what will restart. `:types` provisions
   one if needed.
2. **Parameters**: every parameter of the target version, showing current value, new default, and your override;
   parameters that no longer exist are listed as dropped (0.0.2's upgrade editor).
3. **Policy**: a preset ("monitored, roll back on failure", "monitored, stop on failure", "unmonitored, automatic",
   "unmonitored, manual") that fills every field, each editable: health check wait, stable and retry durations,
   upgrade domain and overall timeouts, replica set check timeout, force restart, and the health policy (max
   unhealthy services/partitions/replicas per service type, warnings as errors). The values the cluster would use
   by default are shown, so you know what you're overriding.

For the cluster: target code version and/or config version among the provisioned ones (0.0.2), the same policy
section, plus the cluster health policy and delta health policy.

**Azure-managed clusters** (detected from the cluster manifest's infrastructure section): cluster code and config
upgrades are owned by the Azure resource provider; starting one through the cluster's own API fights it. sfx shows
the cluster upgrade read-only there and says to use the Azure portal or ARM.

### 10.3 Watching and controlling an upgrade

`enter` on an upgrade (or the Upgrade tab, or the activity strip) opens its live page:

```
┌ upgrade fabric:/orders 1.0.0 → 1.1.0 ─────────────────────────────── monitored, roll back ───┐
│ started 13:40 (26m) · timeout in 1h34m · UD timeout in 18m                                    │
│                                                                                               │
│ UD 0  ✓ done 6m      UD 1  ✓ done 7m      UD 2  ⟳ health check    UD 3  ·      UD 4  ·          │
│                                                                                               │
│ UD 2 · _Node_2  upgrading  pre-upgrade safety check: EnsurePartitionQuorum (partition 7be2…)  │
│        wait  ■■■■■■■■□□ 1:42 left   stable 2:00   retry 10:00                                 │
│                                                                                               │
│ health: ▲ Warning · fabric:/orders/Counter 1 partition below target (allowed: 0%)             │
│                                                                                               │
│ u controls   w why   o go to node   r refresh                                                 │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

It shows each domain's state, the nodes in the current domain with their phase and **pending safety checks** (the
usual reason an upgrade is stuck), the health check timers counting down, and the health evaluation that would
make it fail or roll back.

**Controls** (`u` while running): *resume* into the next domain (a pick of domains with their state, the next one
preselected), *change policy* (the policy form, prefilled with the running values, sent as an update), *roll back*,
and for the cluster *start a new upgrade over it*. Each is a normal change: review, confirm, track.

When an upgrade **fails or rolls back**, the activity strip and header say so until acknowledged, and the upgrade
page leads with the failure reason, the domain and the health evaluation at the time.

---

## 11. Configuration changes

What "configuration" means in a Service Fabric cluster, and where each lives in sfx. All go through §9.

| What | Where | Effect shown in the review |
| --- | --- | --- |
| **Application parameters** | `e` on an app | A rolling upgrade to the same version; services restart domain by domain. Policy form included. |
| **Service settings**: replica/instance counts, min counts, restart and quorum-loss waits, standby keep, placement constraints and policies, move cost, scaling policies, DNS name, load metrics | `e` on a service | Applied immediately, no upgrade; replicas added or dropped to match. |
| **Partition count** (named and Int64 partitioning on recent versions) | `s` on a service | Applied immediately where supported; refused otherwise, with the reason. |
| **Cluster settings** (fabric settings in the cluster manifest) | `:cluster` → Settings tab, `e` on a setting | Standalone clusters: a cluster configuration upgrade. Azure clusters: read-only, with a pointer to ARM. |
| **Cluster configuration JSON** (standalone) | `:cluster` → Config tab, `e` opens `$EDITOR` | A configuration upgrade with the JSON diff in the review. |
| **Naming properties** | `:naming`, `e` / `ctrl+n` / `ctrl+d` | Applied immediately. |
| **Health policies** | Part of upgrades (§10.2) | Only through an upgrade, as in SF. |

For large or free-form values (manifests, JSON, long constraints) `E` opens the value in `$EDITOR`; sfx validates
it when the editor closes and goes on to the review.

The Settings tab lists every section and parameter with its value, and marks values that differ from the
defaults for that SF version where known; `/` searches it.

---

## 12. Lifecycle: provision, create, scale, delete

- **Types** (`7`, `:types`): every application type and version, which apps use each, and status.
  - `ctrl+n` or `:provision <path>`: upload a package folder (or an `.sfpkg`) to the image store and provision it,
    with upload progress per file; or provision a path already in the image store. Validates the manifests locally
    first and shows errors before uploading.
  - `ctrl+d` unprovisions a version; refused, with the list, while apps use it.
  - `d` describes a version: parameters, service types, manifests, and a **diff against another version**
    (`:diff 1.0.0`).
- **Create an app**: `ctrl+n` on a type: one form with the name (suggested from the type), every parameter with its
  default, then review. Replaces 0.0.2's two footer prompts.
- **Create a service**: `ctrl+n` in an app's services: a form with the service type, name, kind-dependent counts,
  partitioning (singleton, Int64 range with count and key range, named with the names), placement constraints and
  load metrics. `:clone` prefills the form from the selected service.
- **Scale**: `s` on a service: count (and min), with the review spelling out what happens to replicas.
- **Delete**: `ctrl+d` on an app or service: type the name; `f` in the confirmation forces it. Deleting an app
  lists its services and their state in the review.
- **Image store** (`:imagestore`): browse folders and files with sizes, delete leftovers from failed provisions,
  see free space.

---

## 13. Operations on nodes, partitions and replicas

As in 0.0.2, with the §9 flow and bulk:

- **Nodes**: activate, deactivate (pause, restart, remove data, with what each means), restart (with optional
  crash dump), stop and start for a while (fault analysis), remove node state (power). Bulk by UD with health waits.
- **Partitions**: restart (all or only secondaries), move the primary, move a secondary, recover from quorum loss
  (power), reset load, and data loss / quorum loss testing (fault analysis, power) for test clusters.
- **Replicas**: restart, remove (force with power), move (to a picked node, which shows each node's UD, FD, type,
  load and whether placement constraints allow it), report health.
- **Code packages**: restart, and see last exit code, restarts and activation failures (already in 0.0.2).
- **Chaos** (power, test clusters): start, stop, schedule, with the chaos events as a live list.

Every operation's outcome shows what it actually did when SF tells us (e.g. "primary moved to _Node_4").

---

## 14. Events, history and repairs

- **Events** (`6`, `:events`): a live timeline of cluster events from the event store (node up/down, partition
  reconfigurations, upgrades, health reports, app lifecycle) when the cluster runs it, filterable like any table,
  and on every describe page for that entity. `f` follows new events (tail). Without an event store the view says
  so and falls back to health event history.
- **Activity** (`ctrl+a`, `:activity`): what sfx did in this session, with outcome and duration, followed by the
  action log (`~/.sfx/actions.log`) across sessions, filterable by context, user and action; `u` on an entry
  undoes it where possible.
- **Repair tasks** (`:repairs`): infrastructure and patching work (Azure maintenance, the patch orchestration
  application): state, impact, which nodes; approve or cancel where allowed. "Why is this node disabled?" usually
  ends here, so a node's describe page links to its repair task.

---

## 15. Safety

- **Risk levels** as today: none (reads), confirm (`y`), destructive (type the name). Power actions are hidden
  until `:power`, which relocks after 15 minutes idle.
- **Read-only contexts** hide every change; the header says `read-only`.
- **Context colors and labels**: `color = "red"`, `label = "PROD"` per context; the border and header use them.
- **The review step always shows the effect**, not a generic "are you sure".
- **Blast radius in confirmations**: how many replicas, partitions or nodes are affected, and whether quorum is at
  risk (e.g. "this removes the last secondary of 2 partitions").
- **Safe bulk**: one at a time, stop on failure, optional health wait between steps.
- **Action log** with user, context, endpoint, action and outcome, as today.
- **`--dry-run`** on the command line, and `:dry-run` toggles a session mode where every change stops after review
  and prints the REST call it would make.

---

## 16. Live data and performance

- Views refresh on a timer (5s default, `:refresh 2s`, `:refresh off`), only for what's on screen.
- Changes are highlighted for one refresh: cells that changed are bold, new rows marked, removed rows fade out.
  Health that got worse flashes once.
- Ages ("4m") everywhere a timestamp would be.
- Cross-cutting views (§8.3) fan out to many calls; sfx caches the hierarchy per context, refreshes it
  incrementally in the background, shows partial results as they arrive, and limits concurrent requests (default
  8). The table title shows "loading 312/1,200 partitions…".
- Large clusters (thousands of partitions): tables are virtual; filters and grouping run on the cached data;
  health comes from one chunk query.
- Errors never replace the data: a failed refresh keeps the last data, marks it stale with its age, and shows the
  error in the footer (`!` for details).

---

## 17. Contexts, auth and settings

**Contexts** (`c`, `:ctx`): the saved clusters, with health fetched in the background, so the contexts view doubles
as a fleet overview. `enter` switches, `e` edits, `ctrl+n` adds (a form with endpoint, auth method and its options,
tested before saving), `ctrl+d` removes. `:ctx -` goes back to the previous context.

**Config file** (`~/.sfx/config.toml`): contexts, saved slices, key bindings, theme, refresh interval, columns per
view. Written without losing comments or ordering (0.0.1 rewrites the file through a TOML model, which drops
comments).

```toml
context = "dev"

[settings]
refresh = "5s"
theme = "default"

[contexts.prod]
endpoint = "prod.westeurope.cloudapp.azure.com:19080"
auth = ["cert"]
cert-thumbprint = "AB12…"
server-cn = "prod.westeurope.cloudapp.azure.com"
readonly = true
color = "red"
label = "PROD"

[contexts.prod.slices]
primaries-ud2 = ":replicas role=Primary ud=2"

[keys]
"ctrl+r" = "restart"
```

**Auth**: as today (none, cert, Entra ID, Windows, claims command), plus a persistent token cache for Entra ID so
sign-in survives restarts, and browser sign-in where available instead of always device code.

---

## 18. The command line

Today: `sfx`, `sfx <endpoint> --auth … [any --key value]`, and `sfx context add|use|show|list|remove`. Auth options
share the namespace with everything else, so a typo silently becomes an auth option.

Proposed: the TUI stays the default, and the same actions are available as subcommands for scripts and quick
checks. Everything comes from `Sfx.Core`, so the CLI and TUI can't drift apart.

```
sfx                                   open the active context
sfx -c prod                           open a context
sfx connect <endpoint> [auth…]        open a cluster without saving it; --save <name> saves it

sfx context ls|add|edit|rm|use|show|test
sfx get <view> [filter…] [-o table|json|yaml|name] [--watch]
sfx describe <kind> <name> [-o …]
sfx health [<kind> <name>] [--why]
sfx upgrade app <name> --to <version> [--param k=v]… [--policy monitored-rollback] [--wait]
sfx upgrade app <name> resume|rollback|status
sfx upgrade cluster --code <v> --config <v> [--policy …] [--wait]
sfx set app <name> k=v…               parameter change (a same-version upgrade)
sfx set service <name> key=value…     service settings
sfx scale service <name> <count>
sfx restart|move|remove replica|node|partition …
sfx provision <folder|path>
sfx log [--context prod] [--since 1d]
```

Rules:

- `get` takes the same filters as the UI (`sfx get replicas role=Primary node=_Node_3 -o json`).
- Changes print the review and ask unless `--yes`; destructive ones need `--yes=<name>` in scripts.
- `--dry-run` everywhere a change happens. `--wait` waits for long operations, with progress on stderr; the exit code
  says whether it succeeded.
- Auth options are declared per method and validated: `sfx connect host --auth cert --cert-file x.pfx`;
  unknown options are errors, with suggestions.
- Exit codes: 0 ok, 1 failed, 2 usage, 3 unhealthy (for `health`), 4 not found.

---

## 19. Phasing

Each phase is shippable on its own.

| Phase | Theme | Scope |
| --- | --- | --- |
| **0.1** | Foundations | Screen anatomy (header, activity strip, preview), health cells with reasons, `w` why, health view (unhealthy + tree) from the health chunk API, command grammar and view aliases, digits for views, forms for every multi-value input, review step with effects, comment-preserving config |
| **0.2** | Slices | Filter language, group by, `:services`/`:partitions`/`:replicas` with the cached hierarchy, placement matrix, load view, saved slices, column chooser |
| **0.3** | Upgrades and config | Upgrades view and live upgrade page, full policy form, version diff, cluster settings and config editing (standalone), Azure-managed detection, `$EDITOR` for large values |
| **0.4** | Operations at scale | Bulk actions with safe rollout, undo, activity view, events timeline, repair tasks, image store browser |
| **0.5** | Command line | `get`/`describe`/`health`/`upgrade`/`set`/`scale` subcommands, output formats, `--dry-run`/`--wait`, `context test` |
| **later** | | Fleet overview with all contexts' health, chaos, backup and restore, themes and key remapping, plugins |

---

## 20. Open questions

1. **Digits for views** move describe-tab switching from digits to `tab`/`[`/`]`. Worth the change for 0.0.2 users?
2. **Preview pane by default**: on when there's room, or always opt-in with `p`?
3. **Cross-cutting views on big clusters**: is a background cache acceptable, or should `:replicas` require a
   filter (node, app or service) to avoid thousands of calls?
4. **Cluster config changes on Azure**: read-only with a pointer to ARM, or integrate with ARM (needs Azure
   credentials as a second auth)?
5. **Undo**: offer it only for exact inverses (activate/deactivate), or also for "restore previous parameters"
   style changes that are themselves upgrades?
6. **CLI scope for 1.0**: read-only (`get`, `describe`, `health`) first, or changes too?
7. **Power relock**: time-based (15 minutes idle), per action, or only when switching contexts?

---

## 21. Cellar: what moves into the framework

sfx is built on [Cellar](../../cellar), and much of the UX above is generic: any k9s-style app needs a command
line, a status line, a keymap, forms and confirmations. Today sfx builds each of these itself on top of Cellar's
lower-level widgets (`Input`, `StatusBar`, `Table`, `Overlay`). They should live in Cellar, so sfx gets better
versions and other apps get them for free.

**The rule:** Cellar gets anything that doesn't know about Service Fabric. sfx keeps what does: health reasons,
the views, the operations and their effects, and which columns the filter language can refer to.

### 21.1 What Cellar has, and what's missing

| Need (section) | sfx today | Cellar today | Gap |
| --- | --- | --- | --- |
| Command line `:` `/` and prompts (§4) | `Shell` swaps a `Label` + `Input` into the footer, with its own modes | `Input`: cursor, home/end, backspace | No modes, history, completion, validation, or readline keys (`ctrl+w`, `ctrl+u`, `alt+b`…) |
| Status line (§3) | `Shell` builds a `Stack` of a label and `Button`s, and swaps it for errors | `StatusBar`: left/center/right text | No items, priorities, truncation, clickable items, or timed messages |
| One declaration per action (§1.7, §5) | `Actions.cs`: `KeyChord`, `ShellAction`, `ActionLookup` | Nothing | A keymap and action registry |
| Help and palette generated from actions | `Shell.ShowHelp`, `OpenPalette` | `CommandPalette` (a list of commands) | Help overlay, and the palette fed from the registry |
| Forms (§9) | `FormDialog.cs` (300 lines) | Nothing | A form widget |
| Confirmations, type to confirm (§9, §15) | `ConfirmDialog.cs` | Nothing | A confirm dialog |
| Keep the selection across refreshes (§16) | `ResourceView.ShowRows` re-selects by key after `SetRows` | `SetRows` selects row 0 | Row keys |
| Filter, group, mark rows (§8, §9) | Filter by substring in `ResourceView` | Sort only | Filter, query language, group by, multi-select |
| Changed cells highlighted (§16) | Nothing | Nothing | Row keys make it possible |
| Text with search, `n`/`N` (§6) | `TextView.cs` | `ScrollView`, `Label` | A text viewer |
| One of several widgets in a place | `Slot.cs` | Nothing | Trivial, but every app writes it |
| Activity strip, toasts (§3, §14) | Notices in the footer | `Spinner`, `ProgressBar` | A task/notification model and its strip |

### 21.2 Proposed Cellar components

**`CommandLine`**: the vim/k9s-style prompt, used for `:`, `/` and any single-value question.

- Modes, each with its own prompt, history and completion: `cmd.Open(":", mode)`. Ask mode for one-off questions
  (`await cmd.AskAsync("instances:", initial: "3", validate: …)`).
- Readline editing: `ctrl+a`/`ctrl+e`, `ctrl+w`, `ctrl+u`/`ctrl+k`, `alt+b`/`alt+f`, `alt+backspace`. This goes in
  `Input` itself, so every text box gets it.
- History per mode, `↑`/`↓` and `ctrl+r` search, persisted by the app through a callback.
- Completion from an `ICompletionSource`: inline ghost text for the best match, `tab` accepts or cycles, and a
  popup above the line listing candidates with descriptions.
- Live `OnChange` (filter as you type) and validation (the text turns red, and the reason shows on the right).

**`StatusLine`**: replaces `StatusBar` (keeping it as a simple preset).

- Items, not three strings: each has text, alignment, a priority and an optional action (clickable).
- When space runs out, the lowest-priority items drop first, then items shorten to their short form.
- Built-in item types: mode badge, key hints (generated from the keymap for the focused widget), spinner/progress,
  and clock or age ("updated 4s ago").
- Messages: `status.Show("done: restart replica 123", Level.Info, TimeSpan.FromSeconds(5))`; errors stay until
  dismissed, with a hint for showing details. Messages temporarily take over the line, then hand it back.
- Hosts the `CommandLine`: one line at the bottom that's either the status or the open prompt, as in vim.

**`Keymap` and actions**: sfx's `Actions.cs`, generalized.

- `Action(id, title, group, run, keys, commands, when, hint, priority)`. `when` decides whether it's available
  (e.g. depending on the selected row).
- Scopes with precedence: focused widget → page → app, so a page can rebind a key.
- Parsing and printing of chords (`"ctrl+r"`, `"g g"` sequences, `"shift+←"`), and user remapping from config.
- Generates the help overlay, feeds `CommandPalette`, `CommandLine` command completion and `StatusLine` key hints,
  so all of them always agree.

**`Form`**: fields with kinds (text, number, duration, choice, flag, secret, long text through `$EDITOR`),
defaults, notes, validation, sections and changed markers; `enter` edits, `u` reverts, `ctrl+s` submits with the
set of changes. A `FormDialog` overlay wraps it.

**`ConfirmDialog`**: title, details, risk (`yes/no` or type a word), and toggles (`f` force). Returns the choice and
toggles.

**`Table<T>` additions**:

- `Key` selector: `SetRows` keeps the selection and scroll position on the same row, and knows which rows are new,
  changed or gone, for highlighting.
- `Filter` predicate, plus a `TableQuery` parser for `col=value col~text n>3` over column headers and aliases, with
  typed comparison from each column's sort key.
- `GroupBy(column)` with collapsible group headers and per-group summaries supplied by the app.
- Marks (`space`, `*`), and `Marked` for bulk actions.
- `EmptyText`, column visibility (a chooser), and virtualization for large row counts.

**`TextViewer`**: a pager with search (`/`, `n`/`N`, match count), line numbers, wrap toggle, a highlighter hook
(XML, JSON), and copy.

**`Switcher`**: shows one of several widgets in the same place (sfx's `Slot`).

**`Activity`**: a model of running and finished tasks (title, progress, outcome), and a strip widget showing it on
one line, expanding to a list.

Later, once these settle: an optional **`AppShell`** that wires header, `Navigator`, `StatusLine`, `CommandLine`,
palette and help from one `Keymap`, so a k9s-style app is a few lines of setup. sfx's `Shell` would become mostly
that.

### 21.3 How to work on both

- Develop them side by side: a `UseLocalCellar` switch in sfx swaps the `Cellar` package for a project reference
  to `../cellar`, so changes in both repos build together without publishing.
- Move one piece at a time, each with Cellar tests and a demo page, then switch sfx to it and delete sfx's copy.
- Order, matching sfx's phases: `Input` readline keys and history → `Keymap` → `StatusLine` + `CommandLine` →
  `Table` keys and `EmptyText` → `Form` and `ConfirmDialog` (all for sfx 0.1); then `TableQuery`, group by, marks
  and change highlighting (sfx 0.2); `TextViewer` and `Activity` (sfx 0.3–0.4).
- Release Cellar (0.3.0) before the sfx release that needs it.
- Cellar targets .NET 8, which is out of support; move it to .NET 10 along the way.
