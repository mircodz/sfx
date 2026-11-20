using System.Xml.Linq;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>The health policy an application upgrade uses unless told otherwise.</summary>
/// <param name="FromManifest">Whether the application manifest declares it; otherwise these are Service Fabric's own defaults.</param>
public sealed record HealthPolicyDefaults(
    bool ConsiderWarningAsError,
    int MaxPercentUnhealthyDeployedApplications,
    int MaxPercentUnhealthyServices,
    int MaxPercentUnhealthyPartitionsPerService,
    int MaxPercentUnhealthyReplicasPerPartition,
    bool FromManifest);

/// <summary>The cluster health policy a cluster upgrade uses unless told otherwise: the cluster manifest's HealthManager/ClusterHealthPolicy.</summary>
/// <param name="ApplicationTypes">Max percent unhealthy applications per application type, from "ApplicationTypeMaxPercentUnhealthyApplications-{type}".</param>
public sealed record ClusterHealthPolicyDefaults(
    bool ConsiderWarningAsError,
    int MaxPercentUnhealthyNodes,
    int MaxPercentUnhealthyApplications,
    IReadOnlyDictionary<string, int> ApplicationTypes);

/// <summary>
/// What an application upgrade does when its policy leaves something out: Service Fabric's documented defaults for
/// the monitoring policy and timeouts, and the application manifest's HealthPolicy (or the built-in one) for health.
/// </summary>
public static class UpgradeDefaults
{
    public static readonly TimeSpan HealthCheckWait = TimeSpan.Zero;
    public static readonly TimeSpan HealthCheckStable = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan HealthCheckRetry = TimeSpan.FromMinutes(10);

    // The upgrade, upgrade domain and replica set check timeouts default to "no limit".

    public static HealthPolicyDefaults HealthPolicy(string applicationManifest)
    {
        var policies = XDocument.Parse(applicationManifest).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Policies");
        var health = policies?.Elements().FirstOrDefault(e => e.Name.LocalName == "HealthPolicy");
        if (health is null)
        {
            return new HealthPolicyDefaults(false, 0, 0, 0, 0, FromManifest: false);
        }

        var services = health.Elements().FirstOrDefault(e => e.Name.LocalName == "DefaultServiceTypeHealthPolicy");
        return new HealthPolicyDefaults(
            string.Equals((string?)health.Attribute("ConsiderWarningAsError"), "true", StringComparison.OrdinalIgnoreCase),
            Percent(health, "MaxPercentUnhealthyDeployedApplications"),
            Percent(services, "MaxPercentUnhealthyServices"),
            Percent(services, "MaxPercentUnhealthyPartitionsPerService"),
            Percent(services, "MaxPercentUnhealthyReplicasPerPartition"),
            FromManifest: true);
    }

    private static int Percent(XElement? element, string attribute) =>
        int.TryParse((string?)element?.Attribute(attribute), out var value) ? value : 0;

    /// <summary>The health policies the application manifest sets for particular service types, by service type name.</summary>
    public static IReadOnlyDictionary<string, ServiceTypeHealthPolicy> ServiceTypeHealthPolicies(string applicationManifest)
    {
        var policies = XDocument.Parse(applicationManifest).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Policies");
        var health = policies?.Elements().FirstOrDefault(e => e.Name.LocalName == "HealthPolicy");
        return (health?.Elements() ?? [])
            .Where(e => e.Name.LocalName == "ServiceTypeHealthPolicy" && !string.IsNullOrEmpty((string?)e.Attribute("ServiceTypeName")))
            .GroupBy(e => (string)e.Attribute("ServiceTypeName")!)
            .ToDictionary(g => g.Key, g => new ServiceTypeHealthPolicy(
                maxPercentUnhealthyPartitionsPerService: Percent(g.First(), "MaxPercentUnhealthyPartitionsPerService"),
                maxPercentUnhealthyReplicasPerPartition: Percent(g.First(), "MaxPercentUnhealthyReplicasPerPartition"),
                maxPercentUnhealthyServices: Percent(g.First(), "MaxPercentUnhealthyServices")));
    }

    /// <summary>The cluster health policy in a cluster manifest; what it leaves out is Service Fabric's default (false, 0%).</summary>
    public static ClusterHealthPolicyDefaults ClusterHealthPolicy(string clusterManifest)
    {
        const string perType = "ApplicationTypeMaxPercentUnhealthyApplications-";
        var parameters = ClusterSettings.Parse(clusterManifest)
            .Where(s => s.Name == "HealthManager/ClusterHealthPolicy")
            .SelectMany(s => s.Parameters)
            .ToList();
        string? Value(string name) => parameters.LastOrDefault(p => p.Name == name)?.Value;
        int Int(string name) => int.TryParse(Value(name), out var value) ? value : 0;

        return new ClusterHealthPolicyDefaults(
            string.Equals(Value("ConsiderWarningAsError"), "true", StringComparison.OrdinalIgnoreCase),
            Int("MaxPercentUnhealthyNodes"),
            Int("MaxPercentUnhealthyApplications"),
            parameters
                .Where(p => p.Name.StartsWith(perType, StringComparison.Ordinal) && p.Name.Length > perType.Length && int.TryParse(p.Value, out _))
                .GroupBy(p => p.Name[perType.Length..])
                .ToDictionary(g => g.Key, g => int.Parse(g.Last().Value)));
    }
}
