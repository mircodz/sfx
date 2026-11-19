# sfx

A terminal UI for [Azure Service Fabric](https://learn.microsoft.com/azure/service-fabric/) clusters, inspired by
[k9s](https://k9scli.io/). Browse nodes, applications, services, partitions and replicas; read health, manifests,
upgrades and load; and run cluster operations from the keyboard, with confirmations and an action log.

## Installation

As a .NET tool (needs the .NET 10 runtime):

```bash
dotnet tool install -g sfx
```

Or download a self-contained binary for your platform from the
[releases](https://github.com/mircodz/sfx/releases), unpack it, and put `sfx` on your `PATH`. On macOS, a binary
downloaded with a browser is quarantined; clear it with `xattr -d com.apple.quarantine ./sfx`.

## Usage

```text
sfx [-c <context>]                           open a saved context (default: the active one)
sfx <endpoint> [--auth <methods>] [options]  open a cluster directly
sfx context <command>                        manage saved contexts
```

```bash
# An unsecured cluster, e.g. a local dev cluster
sfx localhost

# Client certificate, checking the cluster's certificate by common name
sfx mycluster.westeurope.cloudapp.azure.com --auth cert \
  --cert-file ~/certs/client.pfx --server-cn mycluster.westeurope.cloudapp.azure.com

# Microsoft Entra ID (device code sign-in)
sfx https://mycluster:19080 --auth aad --server-thumbprint 0123ABCD...
```

`sfx --help` lists every auth method and its options.

### Contexts

Save clusters once and switch between them with `c` or `:ctx <name>` in the UI. Contexts live in
`~/.sfx/config.toml` (or `$SFX_CONFIG`).

```bash
sfx context add prod mycluster:19080 --auth cert --cert-thumbprint AB12... --server-cn mycluster --readonly
sfx context add dev localhost --use
sfx context list
sfx -c prod
```

A `--readonly` context hides every action that changes the cluster. The riskiest actions (recover from quorum loss,
remove node state, delete…) stay hidden until unlocked with `:power`, or `--power` on the context.

### Keys

| Key | |
| --- | --- |
| `enter` / `l` | open the selected row |
| `d` | describe the selected row |
| `backspace` / `h` / `esc` | back |
| `/` | filter rows, or search the text being read (`n` / `N` step through matches) |
| `:` | command, e.g. `:nodes`, `:apps`, `:cluster`, `:health`, `:ctx prod` |
| `ctrl+p` | command palette |
| `r` | refresh |
| `y` | copy the selected name, or the text being read |
| `?` | every key on the current page |
| `q` | quit |

Actions on the selected entity use the same key everywhere: `ctrl+r` restart, `ctrl+k` remove, `ctrl+d` delete,
`m` move, `x` deactivate, `a` activate. Every change asks for confirmation and is appended to `~/.sfx/actions.log`.

## Development

```bash
make build      # debug build
make test       # unit tests; set SFX_TEST_CLUSTER=localhost to also run against a cluster
make run ARGS="localhost"
make install    # pack and install the tool globally from source
make publish    # self-contained single-file binary for this machine, in dist/<rid>/
```

Releases are cut from `main` by the Release workflow, using the version in [`version`](./version).

## License

[MIT](./LICENSE)
