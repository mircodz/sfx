#!/bin/bash
# Runs on the VM as root (cluster.sh copies it there): installs Service Fabric and its local 5-node dev cluster,
# and a timer that deallocates the VM a while after each boot. Safe to run again.
#
# Installing accepts the Service Fabric runtime and SDK license terms on your behalf.
set -euo pipefail

EXPIRE_MINUTES="${1:-30}"
export DEBIAN_FRONTEND=noninteractive

# Service Fabric's Linux dev packages stop at 9.1, which targets Ubuntu 20.04.
if ! dpkg -s servicefabricsdkcommon >/dev/null 2>&1; then
  wget -q https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
  dpkg -i /tmp/packages-microsoft-prod.deb
  apt-get update -q
  apt-get install -y -q apt-transport-https moby-engine moby-cli dotnet-runtime-8.0
  echo "servicefabric servicefabric/accepted-eula-ga select true" | debconf-set-selections
  echo "servicefabricsdkcommon servicefabricsdkcommon/accepted-eula-ga select true" | debconf-set-selections
  apt-get install -y -q servicefabricsdkcommon
fi

if ! systemctl is-enabled servicefabric >/dev/null 2>&1; then
  /opt/microsoft/sdk/servicefabric/common/clustersetup/devclustersetup.sh
fi

# Shutting down from inside would leave the VM allocated, and billed; deallocating goes through Azure, using the
# VM's managed identity.
cat > /usr/local/bin/sfx-expire <<'EOF'
#!/bin/bash
set -euo pipefail
imds=http://169.254.169.254/metadata
id=$(curl -sf -H Metadata:true "$imds/instance/compute/resourceId?api-version=2021-02-01&format=text")
token=$(curl -sf -H Metadata:true "$imds/identity/oauth2/token?api-version=2018-02-01&resource=https://management.azure.com/" \
  | python3 -c 'import json, sys; print(json.load(sys.stdin)["access_token"])')
logger -t sfx-expire "deallocating $id"
curl -sf -X POST -H "Authorization: Bearer $token" -H "Content-Length: 0" \
  "https://management.azure.com$id/deallocate?api-version=2024-07-01"
EOF
chmod +x /usr/local/bin/sfx-expire

cat > /etc/systemd/system/sfx-expire.service <<'EOF'
[Unit]
Description=Deallocate this VM to stop paying for it

[Service]
Type=oneshot
ExecStart=/usr/local/bin/sfx-expire
EOF

# Counts from when the timer starts: at boot, or when 'cluster.sh extend' restarts it.
cat > /etc/systemd/system/sfx-expire.timer <<EOF
[Unit]
Description=Deallocate this VM ${EXPIRE_MINUTES} minutes after it starts

[Timer]
OnActiveSec=${EXPIRE_MINUTES}min
AccuracySec=30s

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable sfx-expire.timer >/dev/null 2>&1
systemctl restart sfx-expire.timer
echo "VM deallocates in ${EXPIRE_MINUTES} minutes; 'cluster.sh extend' restarts the countdown."
