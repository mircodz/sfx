# Backlog

What's asked for and not done yet, so nothing gets lost. Done items are removed rather than ticked.

## Release 0.0.2

- Cellar 0.3.0 first: merge the `sfx-app-shell` branch and publish it to nuget.org, since sfx now builds against it
  (locally through a gitignored `nuget.config` pointing at a packed copy).
- Then commit everything and run the Release workflow from `main`. Release notes: the .NET tool now needs the
  ASP.NET Core runtime, and the self-contained binary is about 50 MB.

## Cellar 0.3.0 (in progress, ships in 0.0.2)

The terminal UI runs on the local branch `sfx-app-shell` (worktree `../cellar-sfx-app-shell`), Cellar 0.3.0,
unreleased: keys, commands, help and the palette come from one `Keymap`; the prompt and footer are `CommandLine` and
`StatusLine`; confirmations are Cellar's `ConfirmDialog`; describe pages are `TextViewer`; tables use `Table<T>` groups,
row keys, filters and marks; running changes show in an `ActivityStrip`; the prompts keep a history. sfx's own
versions are gone. Left:

- Release Cellar: commit the two `CommandPalette` fixes on the branch (stable order, title ellipsis before the
  subtitle), merge it to main and publish 0.3.0 to nuget.org; CI can't build sfx before that.
- An application upgrade and parameter edit form in the terminal, as in the browser: the logic lives in `Sfx.Web`,
  so it needs to move to Core first.
- Settle checks are written twice, in the browser's and the terminal's (`Tracking.cs`); move them to Core.
- Key remapping from the config (`Keymap.Remap` is there).
- `:group` is forgotten when leaving a page.

## Later

- **Run a real change end to end** from the browser on a bigger cluster (scale, move, an application upgrade), and
  check the notice, the activity log and the refresh.

## Parked

- **Views**: named filters on applications (design below). Built once and taken out again: how they're managed
  (config tables, a per-context default, a picker per tab) didn't feel right yet. Rethink before building again.

## Open questions

- ESLint and Prettier: dev-only tools, but the call was minimal dependencies.
- The remaining k9s mentions in `docs/ux.md`.

---

## Design: folders and views

Folders are built (config, CLI, terminal, browser); views are parked (see above). Both live in `~/.sfx/config.toml`, so the terminal and the browser read the same thing, and both are optional: a
config without them works as it does today.

### Folders

Called folders rather than groups (Azure resource groups), namespaces (which would scope names, and ours stay
global) or environments (flat, and not every folder is one).

```toml
[contexts.prod-weu]
endpoint = "prod-weu.westeurope.cloudapp.azure.com"
folder = "prod/europe"

[folders.prod]
label = "PROD"
color = "danger"
readonly = true
```

- **Shape.** `folder` is a path; `/` nests. A folder is the set of contexts that name it, and needs no table of its
  own unless it sets something. Context names stay flat and unique, so `sfx -c prod-weu` and `:ctx prod-weu` don't
  change.
- **`[folders.<path>]` is optional** and holds what its contexts inherit:
  - `label` and `color`: a status role, not a hex value. Shown next to the context everywhere: the top bar, the
    terminal header, the picker.
  - `readonly`, as a default the context can override.
  - The nearest folder wins: `prod/europe` overrides `prod`.
- **Core.** `ClusterContext` gains `Folder`. `ContextStore` reads and writes `folder`, reads `[folders]`, and resolves
  the inherited `readonly`, `label` and `color`.
- **CLI.** `sfx context add … --folder prod/europe`. `sfx context list` prints contexts by folder, indented.
  `sfx context move <name> <folder>`.
- **Terminal.** `:ctx` lists contexts as a tree: folders collapse, and filtering by text searches names and paths.
  The header shows the label in its color.
- **Browser.** The context picker is a searchable list under folder headings; the current context's label and color
  sit in the top bar. `/api/contexts` returns `folder`, `label` and `color` per context.

### Views

```toml
[views.payments]
apps = ["fabric:/payments", "fabric:/payments/*", "fabric:/shared-*"]

[views.no-system]
exclude = ["fabric:/System"]

[contexts.prod-weu]
view = "payments"
```

- **What a view is.** A named filter on applications, by glob on the `fabric:/` name: `*` matches within a path
  segment, `**` across. `apps` includes and `exclude` removes; with only `exclude`, everything else is in. Views
  sit beside contexts, so one view works on every cluster.
- **What it filters.**
  - Applications.
  - Services, partitions and replicas, through their application.
  - Upgrades and events, through their entity.
  - The nodes list stays whole, since nodes are shared. A node's page shows only in-view replicas, saying how many
    are hidden.
- **What it never hides.**
  - Cluster health and errors and warnings: a hidden unhealthy app still shows, as "2 unhealthy outside this view".
  - Actions on what you can see.
  - It's a lens, not access control; read-only and power stay per context.
- **Choosing one.**
  - A context's `view` is its default.
  - In the browser, a view picker sits next to the context picker. The choice is per tab, kept in the URL (`?view=`),
    so a link to a slice keeps its view.
  - In the terminal, `:view payments` and `:view` (none).
  - On the command line, `sfx -c prod-weu --view payments`.
- **Where it's applied.**
  - On the server: every list endpoint takes `view` and filters before returning, so the browser never gets
    everything and throws most away.
  - Core gains `ViewFilter` (glob matching, `Includes(applicationName)`), used by both the web API and the terminal's
    views.
  - The replicas cache stays cluster-wide and is filtered per request.
- **Later.**
  - Views could also filter by node type or application type (`node_types`, `app_types`).
  - `docs/ux.md` §8's saved slices (a view plus a page's filters) could be stored under `[views]` too.
