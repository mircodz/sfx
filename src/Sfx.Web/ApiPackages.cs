using Microsoft.ServiceFabric.Common;
using Sfx.Core;

namespace Sfx.Web;

/// <param name="ActivationId">Empty for a service package shared by the application's services on the node.</param>
public sealed record DeployedPackageDto(
    string ApplicationId,
    string Application,
    string ServicePackage,
    string Version,
    string Status,
    string ActivationId,
    IReadOnlyList<CodePackageDto> CodePackages);

public sealed record CodePackageDto(
    string Name,
    string Version,
    string Status,
    string HostType,
    string ProcessId,
    string LastExitCode,
    long Activations,
    long ActivationFailures,
    IReadOnlyList<ActionDto> Actions);

internal static class ApiPackages
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/nodes/{name}/packages", async (UiSession s, string name, CancellationToken ct) =>
        {
            var status = (await s.Inspector.GetNodeAsync(name, ct)).NodeStatus?.ToString() ?? "";
            if (!ClusterQueries.MayHostReplicas(status))
            {
                return [];
            }
            try
            {
                return await LoadAsync(s, name, ct);
            }
            // A disabled or disabling node may not answer for its packages; show none rather than fail.
            catch (Exception) when (status != nameof(NodeStatus.Up) && !ct.IsCancellationRequested)
            {
                return [];
            }
        });

    private static async Task<IReadOnlyList<DeployedPackageDto>> LoadAsync(UiSession s, string node, CancellationToken ct)
    {
        var apps = await s.Deployed.GetApplicationsAsync(node, ct);
        var packages = (await Bounded.SelectAsync(
            apps,
            async a => (await s.Deployed.GetServicePackagesAsync(node, a.Id, ct)).Select(p => (App: a, Package: p))))
            .SelectMany(p => p)
            .ToList();

        // One query per manifest returns code packages for all its activations; they're split by activation below.
        var codePackages = (await Bounded.SelectAsync(
            packages.Select(p => (p.App, p.Package.Name)).Distinct(),
            async p => (Key: (p.App.Id, p.Name), Code: await s.Deployed.GetCodePackagesAsync(node, p.App.Id, p.Name, ct))))
            .ToDictionary(p => p.Key, p => p.Code);
        var activations = packages.CountBy(p => (p.App.Id, p.Package.Name)).ToDictionary();

        return packages
            .Select(p => new DeployedPackageDto(
                p.App.Id,
                p.App.Name,
                p.Package.Name,
                p.Package.Version,
                p.Package.Status,
                p.Package.ActivationId,
                codePackages[(p.App.Id, p.Package.Name)]
                    .Where(c => activations[(p.App.Id, p.Package.Name)] == 1 || c.ActivationId == p.Package.ActivationId)
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => new CodePackageDto(
                        c.Name,
                        c.Version,
                        c.Status,
                        c.HostType,
                        c.ProcessId,
                        c.LastExitCode,
                        c.Activations,
                        c.ActivationFailures,
                        WebActions.For(s, "codepackage", new Dictionary<string, string>
                        {
                            ["node"] = node,
                            ["applicationId"] = p.App.Id,
                            ["application"] = p.App.Name,
                            ["servicePackage"] = p.Package.Name,
                            ["codePackage"] = c.Name,
                            ["instanceId"] = c.InstanceId,
                            ["activationId"] = c.ActivationId,
                        })))
                    .ToList()))
            .OrderBy(p => p.Application, StringComparer.Ordinal)
            .ThenBy(p => p.ServicePackage, StringComparer.Ordinal)
            .ThenBy(p => p.ActivationId, StringComparer.Ordinal)
            .ToList();
    }
}
