#!/usr/bin/env python3
"""Fills a Service Fabric cluster with demo applications for trying out sfx.

Builds the packages in ./packages, uploads them through the cluster's REST API, provisions them and creates a
mix of applications: stateful and stateless .NET services with different partition and replica counts, guest
executables, one that keeps crashing, a few health warnings and a slow upgrade in progress.

Talks to an unsecured cluster, e.g. a dev cluster reached through an SSH tunnel:

    ./deploy.py                       # http://localhost:19080
    ./deploy.py --endpoint http://host:19080
    ./deploy.py --remove              # delete the demo applications and types

Only uses the Python standard library; needs the .NET SDK to build the demo services.
"""

import argparse
import json
import shutil
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"

# (application, type, version, parameters)
APPLICATIONS = [
    ("fabric:/checkout", "DemoAppType", "1.0.0", {"Counter_PartitionCount": "3", "Frontend_InstanceCount": "2"}),
    ("fabric:/inventory", "DemoAppType", "1.0.0", {"Counter_PartitionCount": "4"}),
    ("fabric:/payments", "DemoAppType", "1.0.0", {"Counter_PartitionCount": "2", "Frontend_InstanceCount": "-1"}),
    ("fabric:/orders", "DemoAppType", "1.0.0", {"Counter_PartitionCount": "5"}),
    ("fabric:/catalog", "DemoAppType", "1.0.0", {"Counter_TargetReplicaSetSize": "5", "Counter_MinReplicaSetSize": "3", "Frontend_InstanceCount": "3"}),
    ("fabric:/search", "DemoAppType", "1.1.0", {"Counter_PartitionCount": "2"}),
    ("fabric:/notifications", "DemoAppType", "1.1.0", {}),
    ("fabric:/users", "DemoAppType", "1.0.0", {"Counter_PartitionCount": "3"}),
    ("fabric:/agents", "GuestAppType", "1.0.0", {"Sleeper_InstanceCount": "-1"}),
    ("fabric:/batch", "GuestAppType", "1.0.0", {"Sleeper_InstanceCount": "2"}),
    ("fabric:/reports", "GuestAppType", "1.0.0", {}),
    ("fabric:/exporter", "GuestAppType", "1.0.0", {}),
    ("fabric:/mailer", "GuestAppType", "1.0.0", {"Sleeper_InstanceCount": "3"}),
    ("fabric:/scheduler", "GuestAppType", "1.0.0", {}),
    ("fabric:/audit", "GuestAppType", "1.0.0", {}),
    ("fabric:/legacy-worker", "FlakyAppType", "1.0.0", {}),
]

# (package directory, type, version)
APPLICATION_TYPES = [
    ("DemoApp", "DemoAppType", "1.0.0"),
    ("DemoApp", "DemoAppType", "1.1.0"),
    ("GuestApp", "GuestAppType", "1.0.0"),
    ("FlakyApp", "FlakyAppType", "1.0.0"),
]

UPGRADING = ("fabric:/orders", "1.1.0")


class Cluster:
    def __init__(self, endpoint):
        self.endpoint = endpoint.rstrip("/")

    def call(self, method, path, body=None, api="6.0", raw=None, ok=(200, 201, 202), quiet=()):
        separator = "&" if "?" in path else "?"
        url = f"{self.endpoint}{path}{separator}api-version={api}&timeout=600"
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        headers = {"Content-Type": "application/octet-stream" if raw is not None else "application/json"}
        request = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=900) as response:
                text = response.read().decode() or "null"
                return json.loads(text) if response.headers.get_content_type() == "application/json" else text
        except urllib.error.HTTPError as error:
            detail = error.read().decode()
            code = _error_code(detail)
            if code in quiet:
                return None
            raise SystemExit(f"{method} {path}: HTTP {error.code} {code or ''} {detail[:400]}") from None

    def application_id(self, name):
        return name.removeprefix("fabric:/")


def _error_code(detail):
    try:
        return json.loads(detail)["Error"]["Code"]
    except (ValueError, KeyError, TypeError):
        return None


def build():
    """Stages each application type version as a package directory under ./out."""
    if OUT.exists():
        shutil.rmtree(OUT)
    code = OUT / "DemoServices"
    subprocess.run(
        ["dotnet", "publish", str(HERE / "DemoServices"), "-c", "Release", "-r", "linux-x64",
         "--self-contained", "false", "-p:DebugType=none", "-o", str(code)],
        check=True, stdout=subprocess.DEVNULL)

    for directory, type_name, version in APPLICATION_TYPES:
        target = OUT / f"{directory}-{version}"
        shutil.copytree(HERE / "packages" / directory, target)
        manifest = target / "ApplicationManifest.xml"
        manifest.write_text(manifest.read_text().replace('ApplicationTypeVersion="1.0.0"', f'ApplicationTypeVersion="{version}"'))
        if directory == "DemoApp":
            shutil.copytree(code, target / "DemoPkg" / "Code")


def upload(cluster, directory):
    """Copies a package to the image store the way sfctl does: every file, plus a _.dir marker per folder."""
    files = sorted(p for p in directory.rglob("*") if p.is_file())
    folders = sorted({p.parent for p in files} | {directory})
    for path in files:
        relative = path.relative_to(OUT).as_posix()
        cluster.call("PUT", f"/ImageStore/{urllib.parse.quote(relative)}", raw=path.read_bytes())
    for folder in folders:
        relative = folder.relative_to(OUT).as_posix()
        cluster.call("PUT", f"/ImageStore/{urllib.parse.quote(relative)}/_.dir", raw=b"")
    print(f"  uploaded {directory.name} ({len(files)} files)")


def deploy(cluster):
    build()
    provisioned = {(t["Name"], t["Version"]) for t in cluster.call("GET", "/ApplicationTypes")["Items"]}
    for directory, type_name, version in APPLICATION_TYPES:
        if (type_name, version) in provisioned:
            print(f"  {type_name} {version} already provisioned")
            continue
        package = OUT / f"{directory}-{version}"
        upload(cluster, package)
        cluster.call("POST", "/ApplicationTypes/$/Provision",
                     {"Kind": "ImageStorePath", "ApplicationTypeBuildPath": package.name, "Async": False}, api="6.2")
        cluster.call("DELETE", f"/ImageStore/{package.name}", quiet=("FABRIC_E_FILE_NOT_FOUND",))
        print(f"  provisioned {type_name} {version}")

    existing = {a["Name"] for a in cluster.call("GET", "/Applications")["Items"]}
    for name, type_name, version, parameters in APPLICATIONS:
        if name in existing:
            continue
        cluster.call("POST", "/Applications/$/Create", {
            "Name": name,
            "TypeName": type_name,
            "TypeVersion": version,
            "ParameterList": [{"Key": k, "Value": v} for k, v in parameters.items()],
        })
        print(f"  created {name} ({type_name} {version})")

    report_health(cluster)
    start_upgrade(cluster)


def report_health(cluster):
    """Some warnings and an error that never expire, so the health views have something to show."""
    def report(path, prop, state, description):
        cluster.call("POST", f"{path}/$/ReportHealth", {
            "SourceId": "sfx-demo", "Property": prop, "HealthState": state, "Description": description,
            "RemoveWhenExpired": False,
        })

    report("/Applications/inventory", "Disk", "Warning", "Data disk is 87% full.")
    report("/Services/search~Counter", "Latency", "Error", "p99 latency 2.4s, above the 1s objective.")
    nodes = sorted(n["Name"] for n in cluster.call("GET", "/Nodes")["Items"])
    if len(nodes) > 2:
        report(f"/Nodes/{urllib.parse.quote(nodes[2])}", "Memory", "Warning", "Memory use at 91% for 15 minutes.")
    print("  reported health")


def start_upgrade(cluster):
    """A monitored upgrade that waits minutes in each upgrade domain, so it's visibly in progress for a while."""
    name, version = UPGRADING
    app_id = cluster.application_id(name)
    current = cluster.call("GET", f"/Applications/{app_id}")
    if current["TypeVersion"] == version:
        return
    progress = cluster.call("GET", f"/Applications/{app_id}/$/GetUpgradeProgress")
    if progress.get("UpgradeState", "").endswith("InProgress"):
        return
    cluster.call("POST", f"/Applications/{app_id}/$/Upgrade", {
        "Name": name,
        "TargetApplicationTypeVersion": version,
        "Parameters": [{"Key": "Counter_PartitionCount", "Value": "5"}],
        "UpgradeKind": "Rolling",
        "RollingUpgradeMode": "Monitored",
        "MonitoringPolicy": {
            "FailureAction": "Rollback",
            "HealthCheckWaitDurationInMilliseconds": "PT4M",
            "HealthCheckStableDurationInMilliseconds": "PT1M",
            "HealthCheckRetryTimeoutInMilliseconds": "PT10M",
            "UpgradeTimeoutInMilliseconds": "PT2H",
            "UpgradeDomainTimeoutInMilliseconds": "PT30M",
        },
    })
    print(f"  started upgrade of {name} to {version}")


def remove(cluster):
    names = {name for name, *_ in APPLICATIONS}
    for app in cluster.call("GET", "/Applications")["Items"]:
        if app["Name"] in names:
            cluster.call("POST", f"/Applications/{app['Id']}/$/Delete?ForceRemove=true")
            print(f"  deleted {app['Name']}")
    for _, type_name, version in APPLICATION_TYPES:
        cluster.call("POST", f"/ApplicationTypes/{type_name}/$/Unprovision",
                     {"ApplicationTypeVersion": version, "Async": False},
                     quiet=("FABRIC_E_APPLICATION_TYPE_NOT_FOUND",))
        print(f"  unprovisioned {type_name} {version}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--endpoint", default="http://localhost:19080")
    parser.add_argument("--remove", action="store_true", help="delete the demo applications and types")
    args = parser.parse_args()

    cluster = Cluster(args.endpoint)
    version = cluster.call("GET", "/$/GetClusterVersion")
    print(f"cluster {args.endpoint}, version {version['Version']}")
    remove(cluster) if args.remove else deploy(cluster)
    print("done")


if __name__ == "__main__":
    sys.exit(main())
