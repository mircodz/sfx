using System.Xml.Linq;

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
}
