# Demo cluster

A throwaway Service Fabric cluster on an Azure VM, filled with demo applications, to try sfx against something
real. It's Service Fabric's local 5-node dev cluster running on one Linux VM: a real cluster, with no
authentication, reached over an SSH tunnel.

Costs about $0.19/hour while running (a `Standard_D4s_v5` VM). It **deallocates itself 30 minutes after each
start**, after which only its disk is billed (about $10/month), until you `destroy` it.

## Use

Needs the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), signed in with `az login`, and an
SSH key at `~/.ssh/id_ed25519.pub`. Creating the cluster accepts the Service Fabric runtime and SDK license terms.

```bash
./cluster.sh create     # VM, Service Fabric, cluster, demo apps, tunnel (~20 minutes)
sfx localhost           # in another terminal

./cluster.sh status     # VM state, time left, tunnel
./cluster.sh extend     # 30 more minutes
./cluster.sh down       # deallocate now
./cluster.sh up         # start it again (~7 minutes)
./cluster.sh tunnel     # reopen the tunnel, e.g. after a network change
./cluster.sh destroy    # delete everything
```

Settings, as environment variables:

| Variable | Default | |
| --- | --- | --- |
| `SFX_DEMO_RG` | `sfx-dev` | resource group, and the VM's name prefix for the tunnel |
| `SFX_DEMO_VM` | `sfx-dev` | VM name |
| `SFX_DEMO_LOCATION` | `westeurope` | Azure region |
| `SFX_DEMO_SIZE` | `Standard_D4s_v5` | VM size; 8 GB isn't enough for the demo apps |
| `SFX_DEMO_EXPIRE_MINUTES` | `30` | minutes after each start before the VM deallocates |
| `SFX_DEMO_PORT` | `19080` | local end of the tunnel |
| `SFX_DEMO_SSH_KEY` | `~/.ssh/id_ed25519.pub` | public key for the VM's `azureuser` |

## What's in it

`deploy.py` (also usable on its own, against any unsecured cluster) creates 16 applications:

- 8 of `DemoAppType`: a stateful counter (1–5 partitions, 3 or 5 replicas) and a stateless frontend, both .NET,
  in versions 1.0.0 and 1.1.0
- 7 of `GuestAppType`: guest executables, from one instance to one on every node
- `fabric:/legacy-worker`, whose process exits as soon as it starts, so the cluster reports failed activations
- health warnings on `fabric:/inventory` and a node, and an error on the search service
- `fabric:/orders` upgrading to 1.1.0, slowly enough to watch the upgrade domains go by

`./deploy.py --remove` deletes them again.

## How it works

- Service Fabric's Linux dev packages stop at 9.1, for Ubuntu 20.04, so that's what the VM runs.
- Port 19080 is never exposed: only SSH is, with key-only login, and sfx reaches the cluster as
  `localhost:19080` through the tunnel.
- Shutting down from inside a VM leaves it allocated, and billed. So the VM gets a managed identity that may only
  manage VMs in its resource group, and a systemd timer (`sfx-expire.timer`) calls Azure to deallocate it.
  The timer starts at every boot; `extend` restarts it.
- A nightly auto-shutdown at 23:00 CET is a backstop in case the timer fails.
