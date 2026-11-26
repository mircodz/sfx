using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

public sealed record DeployedApplicationRow(string Id, string Name, string TypeName, string Status, HealthState Health);

public sealed record DeployedServicePackageRow(string Name, string Version, string Status, string ActivationId);

public sealed record DeployedCodePackageRow(
    string Name,
    string Version,
    string ServiceManifestName,
    string ActivationId,
    string Status,
    string HostType,
    string ProcessId,
    string InstanceId,
    string LastExitCode,
    long Activations,
    long ActivationFailures,
    DeployedCodePackageInfo Info);

public sealed record DeployedReplicaRow(
    string ServiceName,
    PartitionId PartitionId,
    string Id,
    string Role,
    string Status,
    string CodePackage,
    string ProcessId,
    string Address);

/// <summary>What's deployed on a node: applications, their service packages, and those packages' code and replicas.</summary>
public sealed class DeployedQueries(IServiceFabricClient client)
{
    public async Task<IReadOnlyList<DeployedApplicationRow>> GetApplicationsAsync(string node, CancellationToken cancellationToken)
    {
        var apps = await ClusterQueries.PageAsync(token => client.Applications.GetDeployedApplicationInfoListAsync(
            new NodeName(node),
            includeHealthState: true,
            continuationToken: token,
            cancellationToken: cancellationToken));

        return apps.Select(a => new DeployedApplicationRow(
            a.Id,
            a.Name.ToString(),
            a.TypeName ?? "",
            a.Status?.ToString() ?? "",
            a.HealthState ?? HealthState.Unknown)).ToList();
    }

    public async Task<IReadOnlyList<DeployedServicePackageRow>> GetServicePackagesAsync(string node, string applicationId, CancellationToken cancellationToken)
    {
        var packages = await client.ServicePackages.GetDeployedServicePackageInfoListAsync(
            new NodeName(node),
            applicationId,
            cancellationToken: cancellationToken);

        return (packages ?? []).Select(p => new DeployedServicePackageRow(
            p.Name ?? "",
            p.Version ?? "",
            p.Status?.ToString() ?? "",
            p.ServicePackageActivationId ?? "")).ToList();
    }

    public async Task<IReadOnlyList<DeployedCodePackageRow>> GetCodePackagesAsync(
        string node,
        string applicationId,
        string serviceManifestName,
        CancellationToken cancellationToken)
    {
        var packages = await client.CodePackages.GetDeployedCodePackageInfoListAsync(
            new NodeName(node),
            applicationId,
            serviceManifestName,
            cancellationToken: cancellationToken);

        return (packages ?? []).Select(c =>
        {
            var main = c.MainEntryPoint;
            var stats = main?.CodePackageEntryPointStatistics;
            return new DeployedCodePackageRow(
                c.Name ?? "",
                c.Version ?? "",
                c.ServiceManifestName ?? serviceManifestName,
                c.ServicePackageActivationId ?? "",
                c.Status?.ToString() ?? "",
                c.HostType?.ToString() ?? "",
                main?.ProcessId ?? "",
                main?.InstanceId ?? "",
                stats?.LastExitCode ?? "",
                long.TryParse(stats?.ActivationCount, out var activations) ? activations : 0,
                long.TryParse(stats?.ActivationFailureCount, out var failures) ? failures : 0,
                c);
        }).ToList();
    }

    public async Task<IReadOnlyList<DeployedReplicaRow>> GetReplicasAsync(
        string node,
        string applicationId,
        string? serviceManifestName,
        CancellationToken cancellationToken)
    {
        var replicas = await client.Replicas.GetDeployedServiceReplicaInfoListAsync(
            new NodeName(node),
            applicationId,
            serviceManifestName: serviceManifestName,
            cancellationToken: cancellationToken);

        return (replicas ?? []).Select(r => new DeployedReplicaRow(
            r.ServiceName?.ToString() ?? "",
            r.PartitionId,
            r switch
            {
                DeployedStatefulServiceReplicaInfo stateful => stateful.ReplicaId?.ToString() ?? "",
                DeployedStatelessServiceInstanceInfo stateless => stateless.InstanceId?.ToString() ?? "",
                _ => "",
            },
            r is DeployedStatefulServiceReplicaInfo s ? s.ReplicaRole?.ToString() ?? "" : "Instance",
            r.ReplicaStatus?.ToString() ?? "",
            r.CodePackageName ?? "",
            r.HostProcessId ?? "",
            r.Address ?? "")).ToList();
    }

    public async Task<HealthReport> GetServicePackageHealthAsync(
        string node,
        string applicationId,
        string servicePackageName,
        CancellationToken cancellationToken) =>
        HealthReport.From(await client.ServicePackages.GetDeployedServicePackageHealthAsync(
            new NodeName(node),
            applicationId,
            servicePackageName,
            cancellationToken: cancellationToken));
}
