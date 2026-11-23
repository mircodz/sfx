using Cellar.Text;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>The describe page of each kind of entity; its actions are the same as on the entity's table row.</summary>
public sealed partial class Views
{
    public const int ClusterHealthTab = 1;

    private static DescribeTab Tab(string title, Func<CancellationToken, Task<IReadOnlyList<StyledText>>> load) => new(title, load);

    public IResourceView DescribeCluster(string name, int activeTab = 0) =>
        new DescribeView("cluster", name,
        [
            Tab("Overview", async ct => Lines.Fields(Inspect.Fields(await _inspect.GetClusterVersionAsync(ct)))),
            Tab("Health", async ct => Lines.Health(await _inspect.GetClusterHealthAsync(ct))),
            Tab("Settings", async ct => Lines.Settings(ClusterSettings.Parse(await _inspect.GetClusterManifestAsync(ct)))),
            Tab("Manifest", async ct => Lines.Text(await _inspect.GetClusterManifestAsync(ct))),
            Tab("Upgrade", async ct => Lines.Upgrade(UpgradeReport.From(await _inspect.GetClusterUpgradeAsync(ct)))),
            Tab("Config", async ct => Lines.Text(await _inspect.GetClusterConfigurationAsync(ct))),
            Tab("Load", async ct => Lines.ClusterLoad(await _inspect.GetClusterLoadAsync(ct))),
        ], activeTab, crumb: activeTab == ClusterHealthTab ? "health" : "cluster", Bind(ClusterActions(), name, "Cluster"));

    public IResourceView DescribeNode(NodeRow node) =>
        new DescribeView("node", node.Name,
        [
            Tab("Overview", async ct => Lines.Fields(Inspect.Fields(await _inspect.GetNodeAsync(node.Name, ct)))),
            Tab("Health", async ct => Lines.Health(await _inspect.GetNodeHealthAsync(node.Name, ct))),
            Tab("Deployed apps", async ct => Lines.Table(
                ["NAME", "TYPE", "STATUS", "HEALTH"],
                (await _deployed.GetApplicationsAsync(node.Name, ct))
                    .Select(a => (IReadOnlyList<string>)[a.Name, a.TypeName, a.Status, a.Health.ToString()])
                    .ToList())),
            Tab("Load", async ct => Lines.NodeLoad(await _inspect.GetNodeLoadAsync(node.Name, ct))),
            Tab("Raw", async ct => Lines.Json(await _inspect.GetNodeAsync(node.Name, ct))),
        ], actions: Bind(NodeActions(), node, "Node"));

    public IResourceView DescribeApplication(ApplicationRow app) =>
        new DescribeView("application", app.Name,
        [
            Tab("Overview", async ct => Lines.Fields(Inspect.Fields(await _inspect.GetApplicationAsync(app.Id, ct), "Parameters"))),
            Tab("Parameters", async ct => Lines.Fields(
                ((await _inspect.GetApplicationAsync(app.Id, ct)).Parameters ?? new Dictionary<string, string>())
                    .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new Field(p.Key, p.Value)))),
            Tab("Manifest", async ct => Lines.Text(await _inspect.GetApplicationManifestAsync(app.TypeName, app.TypeVersion, ct))),
            Tab("Upgrade", async ct => Lines.Upgrade(UpgradeReport.From(await _inspect.GetApplicationUpgradeAsync(app.Id, ct)))),
            Tab("Health", async ct => Lines.Health(await _inspect.GetApplicationHealthAsync(app.Id, ct))),
            Tab("Raw", async ct => Lines.Json(await _inspect.GetApplicationAsync(app.Id, ct))),
        ], actions: Bind(ApplicationActions(), app, "Application"));

    public IResourceView DescribeService(ApplicationRow app, ServiceRow service) =>
        new DescribeView("service", service.Name,
        [
            Tab("Overview", async ct => Lines.Fields(Inspect.Fields(await _inspect.GetServiceAsync(app.Id, service.Id, ct)))),
            Tab("Description", async ct => Lines.ServiceDescription(await _inspect.GetServiceDescriptionAsync(service.Id, ct))),
            Tab("Manifest", async ct => Lines.Text(await _inspect.GetServiceManifestAsync(app.TypeName, app.TypeVersion, service.TypeName, ct))),
            Tab("Health", async ct => Lines.Health(await _inspect.GetServiceHealthAsync(service.Id, ct))),
            Tab("Raw", async ct => Lines.Json(await _inspect.GetServiceDescriptionAsync(service.Id, ct))),
        ], actions: Bind(ServiceActions(app), service, "Service"));

    public IResourceView DescribePartition(ServiceRow service, PartitionRow partition) =>
        new DescribeView("partition", partition.Id.ToString(),
        [
            Tab("Overview", async ct => Lines.Fields(Inspect.Fields(await _inspect.GetPartitionAsync(partition.Id, ct)))),
            Tab("Health", async ct => Lines.Health(await _inspect.GetPartitionHealthAsync(partition.Id, ct))),
            Tab("Load", async ct => Lines.PartitionLoad(await _inspect.GetPartitionLoadAsync(partition.Id, ct))),
            Tab("Raw", async ct => Lines.Json(await _inspect.GetPartitionAsync(partition.Id, ct))),
        ], actions: Bind(PartitionActions(service), partition, "Partition"));

    public IResourceView DescribeReplica(ServiceRow service, PartitionRow partition, ReplicaRow replica) =>
        new DescribeView(replica.Role == "Instance" ? "instance" : "replica", replica.Id,
        [
            Tab("Overview", async ct => Lines.Fields(Inspect.Fields(await _inspect.GetReplicaAsync(partition.Id, replica.Id, ct)))),
            Tab("Health", async ct => Lines.Health(await _inspect.GetReplicaHealthAsync(partition.Id, replica.Id, ct))),
            Tab("Raw", async ct => Lines.Json(await _inspect.GetReplicaAsync(partition.Id, replica.Id, ct))),
        ], actions: Bind(ReplicaActions(service, partition), replica, "Replica"));

    public IResourceView DescribeServicePackage(string node, DeployedApplicationRow app, DeployedServicePackageRow package) =>
        new DescribeView("service package", package.Name,
        [
            Tab("Replicas", async ct => Lines.Table(
                ["SERVICE", "PARTITION", "ID", "ROLE", "STATUS", "CODE PACKAGE", "PID"],
                (await _deployed.GetReplicasAsync(node, app.Id, package.Name, ct))
                    .Select(r => (IReadOnlyList<string>)[r.ServiceName, r.PartitionId.ToString(), r.Id, r.Role, r.Status, r.CodePackage, r.ProcessId])
                    .ToList())),
            Tab("Manifest", async ct => Lines.Text(await _inspect.GetServiceManifestByNameAsync(app.Id, package.Name, ct))),
            Tab("Health", async ct => Lines.Health(await _deployed.GetServicePackageHealthAsync(node, app.Id, package.Name, ct))),
        ]);

    public IResourceView DescribeCodePackage(string node, DeployedApplicationRow app, DeployedCodePackageRow codePackage) =>
        new DescribeView("code package", codePackage.Name,
        [
            Tab("Overview", _ => Task.FromResult(Lines.Fields(Inspect.Fields(codePackage.Info, "SetupEntryPoint", "MainEntryPoint")))),
            Tab("Main entry point", _ => Task.FromResult(Lines.EntryPoint(codePackage.Info.MainEntryPoint))),
            Tab("Setup entry point", _ => Task.FromResult(Lines.EntryPoint(codePackage.Info.SetupEntryPoint))),
            Tab("Raw", _ => Task.FromResult(Lines.Json(codePackage.Info))),
        ], actions: Bind(CodePackageActions(node, app), codePackage, "Code package"));
}
