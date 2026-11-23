#!/usr/bin/env bash
# A throwaway Service Fabric dev cluster on an Azure VM, filled with demo applications, for trying sfx for real.
#
#   ./cluster.sh create [for]   VM, Service Fabric, 5-node dev cluster, demo apps, tunnel (~20 minutes)
#   ./cluster.sh up [for]       start the VM again and reopen the tunnel (~3 minutes)
#   ./cluster.sh extend [for]   restart the expiry countdown
#   ./cluster.sh status         VM state, time left, tunnel
#   ./cluster.sh tunnel         reopen the tunnel, e.g. after a network change
#   ./cluster.sh down           deallocate now
#   ./cluster.sh destroy        delete everything
#
# [for] is how long it stays up: 90m, 2h, 24h, 1d or minutes (default SFX_DEMO_EXPIRE_MINUTES, 30). The VM then
# deallocates itself, so a forgotten cluster only costs its disk. A run past 22:00 UTC turns the nightly shutdown off
# for that run. The cluster port stays private: sfx reaches it as localhost:19080 through an SSH tunnel.
#
# Needs the Azure CLI, signed in (az login), and an SSH key.
set -euo pipefail

RG="${SFX_DEMO_RG:-sfx-dev}"
VM="${SFX_DEMO_VM:-sfx-dev}"
LOCATION="${SFX_DEMO_LOCATION:-westeurope}"
SIZE="${SFX_DEMO_SIZE:-Standard_D4s_v5}"
EXPIRE_MINUTES="${SFX_DEMO_EXPIRE_MINUTES:-30}"
SSH_KEY="${SFX_DEMO_SSH_KEY:-$HOME/.ssh/id_ed25519.pub}"
ADMIN=azureuser
PORT="${SFX_DEMO_PORT:-19080}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SOCKET="$HOME/.ssh/sfx-demo-tunnel-${SFX_DEMO_RG:-sfx-dev}.sock"

say() { printf '\033[1m%s\033[0m\n' "$*"; }

# "24h" -> 1440: minutes from 90m, 2h, 1d or a plain number of minutes.
minutes() {
  local value="${1:-$EXPIRE_MINUTES}"
  [[ "$value" =~ ^([0-9]+)(m|min|h|d)?$ ]] || { echo "Can't read '$value' as a duration: use e.g. 90m, 2h, 24h or 1d." >&2; exit 2; }
  local n="${BASH_REMATCH[1]}"
  case "${BASH_REMATCH[2]}" in
    h) n=$((n * 60)) ;;
    d) n=$((n * 1440)) ;;
  esac
  (( n >= 5 )) || { echo "At least 5 minutes, so there's time to use it." >&2; exit 2; }
  echo "$n"
}

# The nightly auto-shutdown (22:00 UTC) backs up the expiry timer, but would cut a long run short.
minutes_to_nightly() { python3 -c 'import datetime as d; n=d.datetime.now(d.timezone.utc); t=n.replace(hour=22,minute=0,second=0,microsecond=0); t+=d.timedelta(days=t<=n); print(int((t-n).total_seconds()//60))'; }

nightly_enabled() {
  [ "$(az resource show -g "$RG" -n "shutdown-computevm-$VM" --resource-type microsoft.devtestlab/schedules \
    --query properties.status -o tsv 2>/dev/null)" = Enabled ]
}

# Deallocate <minutes> from now: the timer's duration goes in a drop-in, so it changes without recreating the VM.
expire_in() {
  local min="$1"
  remote "sudo mkdir -p /etc/systemd/system/sfx-expire.timer.d && printf '[Timer]\nOnActiveSec=\nOnActiveSec=%smin\n' $min | sudo tee /etc/systemd/system/sfx-expire.timer.d/duration.conf >/dev/null && sudo systemctl daemon-reload && sudo systemctl restart sfx-expire.timer"
  if (( min >= $(minutes_to_nightly) )); then
    if nightly_enabled; then
      az vm auto-shutdown -g "$RG" -n "$VM" --off -o none
      echo "nightly shutdown: off for this run (the expiry timer still deallocates it)"
    fi
  elif ! nightly_enabled; then
    az vm auto-shutdown -g "$RG" -n "$VM" --time 2200 -o none
    echo "nightly shutdown: back on (22:00 UTC)"
  fi
}

ip() { az vm show -d -g "$RG" -n "$VM" --query publicIps -o tsv; }

remote() { ssh -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new "$ADMIN@$(ip)" "$@"; }

wait_for_ssh() {
  for _ in $(seq 1 40); do
    remote true 2>/dev/null && return
    sleep 10
  done
  echo "SSH to the VM didn't come up." >&2
  exit 1
}

tunnel_open() { ssh -S "$SOCKET" -O check x 2>/dev/null; }

tunnel_up() {
  tunnel_open && return
  rm -f "$SOCKET"
  ssh -f -N -M -S "$SOCKET" -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
    -o StrictHostKeyChecking=accept-new -L "$PORT:localhost:19080" "$ADMIN@$(ip)"
  echo "tunnel: localhost:$PORT -> cluster"
}

tunnel_down() { ssh -S "$SOCKET" -O exit x 2>/dev/null || true; }

wait_for_cluster() {
  say "Waiting for the cluster's 5 nodes"
  for _ in $(seq 1 60); do
    up=$(curl -s -m 5 "http://localhost:$PORT/Nodes?api-version=6.0" 2>/dev/null \
      | python3 -c 'import json, sys; print(sum(n["NodeStatus"] == "Up" for n in json.load(sys.stdin)["Items"]))' 2>/dev/null || echo 0)
    [ "$up" = 5 ] && { echo "cluster is up"; return; }
    sleep 10
  done
  echo "The cluster didn't come up; see 'journalctl -u servicefabric' on the VM." >&2
  exit 1
}

# "in 23h 59min (2026-11-24 18:30 UTC)". The timer counts from when it started, so only list-timers knows the time.
time_left() {
  remote "systemctl list-timers sfx-expire.timer --no-legend" 2>/dev/null \
    | awk '{ left = ""; for (i = 5; i <= NF && $i != "left"; i++) left = left (left ? " " : "") $i; print "in " left " (" $2 " " substr($3, 1, 5) " " $4 ")" }' \
    || true
}

create() {
  local min; min=$(minutes "${1:-}")
  [ -f "$SSH_KEY" ] || { echo "No SSH key at $SSH_KEY; set SFX_DEMO_SSH_KEY." >&2; exit 1; }
  say "Creating resource group $RG in $LOCATION"
  az group create -n "$RG" -l "$LOCATION" --tags purpose=sfx-demo-cluster -o none
  rg_id=$(az group show -n "$RG" --query id -o tsv)

  # The identity may only manage VMs in this group, which is what lets the VM deallocate itself.
  say "Creating VM $VM ($SIZE, Ubuntu 20.04)"
  az vm create -g "$RG" -n "$VM" \
    --image Canonical:0001-com-ubuntu-server-focal:20_04-lts-gen2:latest --size "$SIZE" --os-disk-size-gb 64 \
    --admin-username "$ADMIN" --ssh-key-values "$SSH_KEY" --public-ip-sku Standard --nsg-rule SSH \
    --assign-identity '[system]' --role "Virtual Machine Contributor" --scope "$rg_id" -o none
  # A backstop in case the expiry timer fails: deallocate every night at 22:00 UTC.
  az vm auto-shutdown -g "$RG" -n "$VM" --time 2200 -o none
  ssh-keygen -R "$(ip)" >/dev/null 2>&1 || true

  say "Installing Service Fabric and starting the dev cluster (~10 minutes)"
  wait_for_ssh
  scp -q -o StrictHostKeyChecking=accept-new "$HERE/vm-setup.sh" "$ADMIN@$(ip):/tmp/vm-setup.sh"
  remote "sudo bash /tmp/vm-setup.sh $EXPIRE_MINUTES"
  expire_in "$min"

  tunnel_up
  wait_for_cluster
  say "Deploying the demo applications"
  "$HERE/deploy.py" --endpoint "http://localhost:$PORT"
  done_message
}

up() {
  local min; min=$(minutes "${1:-}")
  say "Starting $VM"
  az vm start -g "$RG" -n "$VM" -o none
  wait_for_ssh
  expire_in "$min"
  tunnel_down
  tunnel_up
  wait_for_cluster
  done_message
}

done_message() {
  echo
  say "Ready. Deallocates $(time_left); './cluster.sh extend [for]' for more time."
  echo "  sfx localhost:$PORT"
  echo "  sfx context add demo localhost:$PORT --use   # once, then just: sfx"
}

status() {
  state=$(az vm get-instance-view -g "$RG" -n "$VM" --query "instanceView.statuses[1].displayStatus" -o tsv 2>/dev/null || echo "not found")
  echo "vm:      $state"
  if [ "$state" = "VM running" ]; then
    echo "expires: $(time_left)"
  fi
  if tunnel_open; then echo "tunnel:  open (localhost:$PORT)"; else echo "tunnel:  closed"; fi
}

case "${1:-}" in
  create) create "${2:-}" ;;
  up) up "${2:-}" ;;
  extend) expire_in "$(minutes "${2:-}")"; echo "Deallocates $(time_left)." ;;
  status) status ;;
  tunnel) tunnel_down; tunnel_up ;;
  down) tunnel_down; say "Deallocating $VM"; az vm deallocate -g "$RG" -n "$VM" -o none ;;
  destroy) tunnel_down; say "Deleting resource group $RG"; az group delete -n "$RG" --yes --no-wait; echo "deleting in the background" ;;
  *) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
