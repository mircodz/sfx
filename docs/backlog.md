# Backlog

What's asked for and not done yet, so nothing gets lost. Done items are removed rather than ticked.

## Next

- **View picker in the browser**: the server side of views is built (below); the picker next to the context picker
  is in progress.
- **Applications with many parameters** (hundreds or more): search, set ones first, and "show more", in the editor
  and on the application page. The review step already lists only what changed.
- **Confirmation dialog spacing**: one rhythm between title, effect, fields and buttons.
- **Real upgrade policy defaults** in the policy fields instead of "Default". The server already returns them:
  Service Fabric's monitoring defaults, and the health policy from the application manifest or Service Fabric.
- **Web checks in CI**: style rules, unit tests and the type check. Gallery screenshots stay a local check, since
  they depend on the machine's fonts.

## Release 0.0.2

- Commit everything, including `docs/` (the README links `docs/style.md`), but not `sfx-0.0.2.zip`. Then run the
  Release workflow from `main`.
- Release notes: the .NET tool now needs the ASP.NET Core runtime, and the self-contained binary is about 50 MB.

## After 0.0.2: Cellar 0.3.0

The local branch `sfx-app-shell` (worktree `../cellar-sfx-app-shell`) is Cellar 0.3.0, unreleased: what
`docs/ux.md` §21 moves out of sfx and into the framework. Release Cellar first (merge to main, publish), then move
the terminal UI onto it:

- `Keymap` and `AppAction` replace sfx's `ShellAction` and `Actions.cs`: keys, `:` commands, help and the palette
  in one declaration.
- `CommandLine` and `StatusLine` replace sfx's prompt and footer.
- `ConfirmDialog` replaces `ConfirmDialog.cs`.
- `Form` and `FormDialog`: the edit-then-review flow the browser has.
- `TextViewer` replaces `TextView.cs` and `Highlight.cs`.
- `Switcher`: the contexts page with folders.
- `Activity`, `ActivityStrip` and `ActivityList` replace `Activity.cs`, and track long-running operations.
- `Table<T>`: groups (folders, nodes, applications), row keys, filters and marks.
- `Input`: readline keys, paste and history.

## Later

- **Run a real change end to end** from the browser (scale, move), and check the notice, the activity log and the
  refresh.
- **Track long-running operations** until they finish, not just "requested" (`docs/ux.md` §9, step 5).
- **Health reasons on list rows** (`docs/ux.md` §7).
- **Deployed code packages** on a node's page, with restart (the terminal has it).
- **Repair tasks** in the browser (the terminal has `:repairs`).
- **Cluster upgrades**: start code and config upgrades with a policy; read-only on Azure-managed clusters
  (`docs/ux.md` §10.2, §11).
- **Placement and load views** (`docs/ux.md` §8.3).

## Open questions

- ESLint and Prettier: dev-only tools, but the call was minimal dependencies.
- The remaining k9s mentions in `docs/ux.md`.

---

## Design: folders and views

Folders are built (config, CLI, terminal, browser). Views are built in the config, CLI, terminal and web API; the
browser's view picker is in progress. Both live in `~/.sfx/config.toml`, so the terminal and the browser read the same
thing, and both are optional: a config without them works as it does today.

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
  segment, `**` across. `apps` includes and `exclude` removes; with only `exclude`, everything else is in. Matching
  ignores case, every pattern starts with `fabric:/`, and a view needs `apps`, `exclude` or both. Views sit beside
  contexts, so one view works on every cluster. `fabric:/System` is filtered like any other application.
- **What it filters.**
  - Applications.
  - Services, partitions and replicas, through their application.
  - Upgrades and events, through their entity. The cluster upgrade and cluster and node events always show.
  - The nodes list stays whole, since nodes are shared. A node's page shows only in-view replicas, saying how many
    are hidden.
- **What it never hides.**
  - Cluster health and errors and warnings: a hidden unhealthy app still shows, as "2 unhealthy outside this view".
  - Actions on what you can see.
  - It's a lens, not access control; read-only and power stay per context. A direct link to an application outside
    the view still opens it, and what's under an opened application isn't filtered.
- **Choosing one.**
  - A context's `view` is its default; `sfx context set-view <name> <view>` (or `--none`) sets it. A context naming a
    view that isn't defined opens without one, with a warning. `sfx view list` prints the views.
  - In the browser, a view picker sits next to the context picker. The choice is per tab, kept in the URL (`?view=`),
    so a link to a slice keeps its view.
  - In the terminal, `:view payments` and `:view` (none), also in the palette. The status line shows the view, and
    switching context goes back to that context's default.
  - On the command line, `sfx -c prod-weu --view payments`; for `sfx ui` it's the browser's starting view.
- **Where it's applied.**
  - On the server: every list endpoint takes `view` and filters before returning, so the browser never gets
    everything and throws most away. `/api/applications`, `/api/replicas`, `/api/upgrades`, `/api/events`,
    `/api/nodes/{name}` (with `hiddenReplicas`), `/api/nodes/{name}/packages` and `/api/overview` (with
    `view: { name, applications, hiddenApplications, hiddenUnhealthy }`) take it. An unknown name is a 400.
    `/api/session` returns `views` and `defaultView`; `/api/contexts` returns each context's `view`.
  - Core gains `ViewFilter` (glob matching, `Includes(applicationName)`), used by both the web API and the terminal's
    views.
  - The replicas cache stays cluster-wide and is filtered per request.
- **Later.**
  - Views could also filter by node type or application type (`node_types`, `app_types`).
  - `docs/ux.md` §8's saved slices (a view plus a page's filters) could be stored under `[views]` too.
