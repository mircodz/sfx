using System.Globalization;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;

namespace Sfx.Core;

/// <summary>A service to create: partitions are a singleton when 1, else uniform Int64 ranges over all keys.</summary>
/// <param name="Name">Its full name (fabric:/app/svc), or a name relative to the application ("svc").</param>
/// <param name="Count">Target replica set size when stateful, instance count when stateless (-1: one on every node).</param>
/// <param name="Persisted">For stateful services: whether replicas keep their state on disk.</param>
public sealed record NewService(string Name, string TypeName, bool Stateful, int Count, int Partitions = 1, bool Persisted = true)
{
    /// <summary>One replica short of the target can still write, but never fewer than one.</summary>
    public int MinReplicas => Math.Max(1, Count - 1);
}

/// <summary>An application package uploaded and provisioned: the type and version it registered, and where it is in the image store.</summary>
public sealed record ApplicationPackage(string TypeName, string TypeVersion, string StorePath);

/// <summary>
/// An application upgrade or parameter change: to <paramref name="TargetVersion"/> (its current version to change only
/// parameters), with today's parameters kept where the target still defines them.
/// </summary>
/// <param name="Set">Parameters to give these values.</param>
/// <param name="Reset">Parameters to take off the application, so they follow the target version's default again.</param>
/// <param name="Policy">Timeouts and health limits; null keeps the cluster's defaults and the manifest's health policy.</param>
public sealed record ApplicationUpgradeRequest(
    string TargetVersion,
    UpgradePlan Plan,
    IReadOnlyDictionary<string, string>? Set = null,
    IReadOnlyCollection<string>? Reset = null,
    UpgradePolicy? Policy = null);

/// <summary>The parameters an application upgrade sends, and today's ones it drops because the target version doesn't define them.</summary>
public sealed record UpgradeParameters(IReadOnlyDictionary<string, string> Parameters, IReadOnlyList<string> Dropped);

/// <summary>
/// Application types, applications and services: provisioning (from the image store or a local package), creating,
/// upgrading and changing parameters, and steering running upgrades.
/// </summary>
/// <param name="defaultServicesUpgradeEnabled">
/// Whether the cluster lets upgrades change default services (ClusterManager/EnableDefaultServicesUpgrade); read from
/// the cluster manifest when needed if not given, so a caller can cache it.
/// </param>
public sealed class ApplicationLifecycle(IServiceFabricClient client, Func<CancellationToken, Task<bool>>? defaultServicesUpgradeEnabled = null)
{
    private readonly ClusterInspector _inspector = new(client);
    private readonly ClusterQueries _queries = new(client);
    private readonly ClusterOperations _operations = new(client);

    /// <summary>Registers an application package already in the image store, at its path relative to the store's root.</summary>
    /// <remarks>Provisioning continues in the background; the type shows as Provisioning until it's done.</remarks>
    public Task ProvisionAsync(string imageStorePath, CancellationToken cancellationToken)
    {
        var path = ImageStore.Normalize(imageStorePath);
        return path.Length == 0
            ? throw new RefusedException("Give the image store path of the application package to provision.")
            : client.ApplicationTypes.ProvisionApplicationTypeAsync(
                new ProvisionApplicationTypeDescription(path, async: true),
                cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Uploads a package from this machine, a folder or an .sfpkg/.zip file with ApplicationManifest.xml at its root, to
    /// the image store, and provisions it. Refuses a type version that's already provisioned.
    /// </summary>
    /// <param name="storePath">Where to put it in the image store; "{type}_{version}" when null.</param>
    /// <param name="progress">Told how far the upload is, after each file and each chunk of a large one.</param>
    public async Task<ApplicationPackage> UploadAndProvisionAsync(
        string localPath,
        string? storePath,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var package = LocalPackage.Open(localPath);
        var existing = await _inspector.GetApplicationTypeVersionsAsync(package.TypeName, cancellationToken);
        if (existing.FirstOrDefault(t => t.Version == package.TypeVersion) is { } type)
        {
            throw new RefusedException(
                $"{package.TypeName} {package.TypeVersion} is already {(type.Status == ApplicationTypeStatus.Provisioning ? "being provisioned" : "provisioned")}. " +
                "Unprovision it first, or give the package a new version.",
                conflict: true);
        }

        var target = string.IsNullOrWhiteSpace(storePath) ? package.StorePath : ImageStore.Normalize(storePath);
        await new ImageStore(client).UploadFolderAsync(package.Folder, target, progress, cancellationToken);
        await ProvisionAsync(target, cancellationToken);
        return new ApplicationPackage(package.TypeName, package.TypeVersion, target);
    }

    /// <summary>Removes a provisioned type version. Refuses while applications still use it, naming them.</summary>
    public async Task UnprovisionAsync(string typeName, string typeVersion, CancellationToken cancellationToken)
    {
        var users = (await _queries.GetApplicationsAsync(cancellationToken))
            .Where(a => a.TypeName == typeName && a.TypeVersion == typeVersion)
            .Select(a => a.Name)
            .ToList();
        if (users.Count > 0)
        {
            throw new RefusedException(
                $"{typeName} {typeVersion} is still used by {string.Join(", ", users)}. Upgrade or delete {(users.Count == 1 ? "it" : "them")} first.",
                conflict: true);
        }
        await client.ApplicationTypes.UnprovisionApplicationTypeAsync(
            typeName,
            new UnprovisionApplicationTypeDescriptionInfo(typeVersion, async: true),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates an application of a provisioned type version. Parameters left out take the type's defaults; one the type
    /// doesn't define is refused before asking the cluster.
    /// </summary>
    /// <param name="name">Its name, with or without "fabric:/".</param>
    public async Task CreateApplicationAsync(
        string name,
        string typeName,
        string typeVersion,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var applicationName = FullName(name);
        if (applicationName.Length <= "fabric:/".Length)
        {
            throw new RefusedException("Give the application a name.");
        }
        var defaults = await DefaultsAsync(typeName, typeVersion, cancellationToken);
        if (parameters.Keys.FirstOrDefault(k => !defaults.ContainsKey(k)) is { } unknown)
        {
            throw new RefusedException($"{typeName} {typeVersion} has no parameter '{unknown}'.");
        }
        await client.Applications.CreateApplicationAsync(
            new ApplicationDescription(new ApplicationName(applicationName), typeName, typeVersion, parameters),
            cancellationToken: cancellationToken);
    }

    /// <summary>The parameters the application sets; the others take its version's defaults.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetParametersAsync(string applicationId, CancellationToken cancellationToken) =>
        (await _inspector.GetApplicationAsync(applicationId, cancellationToken)).Parameters ?? new Dictionary<string, string>();

    /// <summary>What an upgrade does, for a confirmation or a log: "Upgrade fabric:/app to 2.0.0 (monitored, roll back on failure), setting a".</summary>
    public static string Describe(string applicationName, ApplicationUpgradeRequest request) =>
        $"Upgrade {applicationName} to {request.TargetVersion} ({request.Plan.Title.ToLowerInvariant()})"
        + (request.Set is { Count: > 0 } set ? $", setting {string.Join(", ", set.Keys)}" : "")
        + (request.Reset is { Count: > 0 } reset ? $", resetting {string.Join(", ", reset)}" : "")
        + (request.Policy is null ? "" : ", custom policy");

    /// <summary>
    /// Upgrades an application, or changes its parameters (the same version). Today's parameters are kept where the
    /// target still defines them. Refuses, before the cluster would, a target that isn't provisioned, an unknown
    /// parameter, and a change to a default service that the cluster can't make: one that repartitions an existing
    /// service, or any while EnableDefaultServicesUpgrade is off.
    /// </summary>
    public async Task UpgradeAsync(string applicationId, ApplicationUpgradeRequest request, CancellationToken cancellationToken)
    {
        request.Policy?.Require(request.Plan.Monitored, cluster: false);
        var version = request.TargetVersion;
        var current = await _inspector.GetApplicationAsync(applicationId, cancellationToken);
        var name = current.Name?.ToString() ?? ClusterInspector.NameOf(applicationId);
        var versions = await _inspector.GetApplicationTypeVersionsAsync(current.TypeName, cancellationToken);
        var target = versions.FirstOrDefault(v => v.Version == version)
            ?? throw new RefusedException($"Version {version} of {current.TypeName} isn't provisioned.");

        var today = current.Parameters ?? new Dictionary<string, string>();
        var targetDefaults = target.DefaultParameterList ?? new Dictionary<string, string>();
        var parameters = MergeParameters(today, targetDefaults, request.Set ?? new Dictionary<string, string>(), request.Reset ?? [], version).Parameters;

        // What a parameter is worth before and after, defaults included: one left unset follows the target
        // version's default, so a default that changed between versions changes the services that use it too.
        var currentDefaults = versions.FirstOrDefault(v => v.Version == current.TypeVersion)?.DefaultParameterList ?? new Dictionary<string, string>();
        var changed = DefaultServices.ChangedParameters(today, currentDefaults, parameters, targetDefaults);

        // Catch what the cluster would refuse with only "aka.ms/upgrade-defaultservices", and say why.
        // Only services that exist can be refused; default services new in the target version are simply created.
        var existing = (await _queries.GetServicesAsync(applicationId, cancellationToken))
            .Where(s => s.Name.Length > name.Length)
            .Select(s => s.Name[(name.Length + 1)..])
            .ToHashSet();
        var targetManifest = await _inspector.GetApplicationManifestAsync(current.TypeName, version, cancellationToken);
        bool? enabled = null;
        foreach (var use in DefaultServices.ParameterUses(targetManifest).Where(u => changed.Contains(u.Parameter) && existing.Contains(u.Service)))
        {
            if (use.Partitioning)
            {
                throw new RefusedException(
                    $"{use.Parameter} sets how {use.Service} is partitioned ({use.Setting}), which can't change once the service exists. " +
                    "Repartitioning means creating the service again.",
                    conflict: true);
            }
            if (!(enabled ??= await DefaultServicesUpgradeEnabledAsync(cancellationToken)))
            {
                throw new RefusedException(
                    $"{use.Parameter} sets the {use.Setting} of the default service {use.Service}, and this cluster doesn't let " +
                    "upgrades change default services (ClusterManager/EnableDefaultServicesUpgrade is off). " +
                    (use.Setting is "InstanceCount" or "TargetReplicaSetSize" or "MinReplicaSetSize"
                        ? $"Scale {use.Service} instead."
                        : "Turn that setting on, or change the service directly."),
                    conflict: true);
            }
        }

        try
        {
            await _operations.UpgradeApplicationAsync(
                applicationId,
                name,
                version,
                parameters,
                request.Plan.Mode,
                request.Plan.OnFailure,
                request.Policy,
                targetManifest,
                cancellationToken);
        }
        catch (Exception ex) when (DefaultServices.IsRefusal(ex))
        {
            throw new RefusedException(
                $"The cluster refused: version {version} changes a default service, and this cluster doesn't let upgrades " +
                "change default services (ClusterManager/EnableDefaultServicesUpgrade is off, see aka.ms/upgrade-defaultservices).",
                conflict: true,
                ex);
        }
    }

    /// <summary>
    /// The parameters to upgrade with: today's ones the target version still defines, then <paramref name="set"/>, less
    /// <paramref name="reset"/>. A name the target doesn't define, or one both set and reset, is refused.
    /// </summary>
    /// <param name="version">The target version, for messages.</param>
    public static UpgradeParameters MergeParameters(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> targetDefaults,
        IReadOnlyDictionary<string, string> set,
        IReadOnlyCollection<string> reset,
        string version)
    {
        // A parameter the target dropped would make the cluster refuse, so it goes.
        var parameters = current.Where(kv => targetDefaults.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var dropped = current.Keys.Where(k => !targetDefaults.ContainsKey(k)).ToList();
        foreach (var (name, value) in set)
        {
            parameters[targetDefaults.ContainsKey(name) ? name : throw new RefusedException($"Version {version} has no parameter '{name}'.")] = value;
        }
        foreach (var name in reset)
        {
            if (set.ContainsKey(name))
            {
                throw new RefusedException($"'{name}' can't be both set and reset to its default.");
            }
            parameters.Remove(name);
        }
        return new UpgradeParameters(parameters, dropped);
    }

    /// <summary>Parses "a=1; b=2" (also one per line); a value may contain '='.</summary>
    public static IReadOnlyDictionary<string, string> ParseParameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0)
            {
                throw new RefusedException($"'{entry}' isn't name=value.");
            }
            parameters[entry[..equals].Trim()] = entry[(equals + 1)..].Trim();
        }
        return parameters;
    }

    /// <summary>Switches an application's running upgrade to another plan, e.g. from manual to automatic.</summary>
    public Task ChangeUpgradePlanAsync(string applicationId, UpgradePlan plan, CancellationToken cancellationToken) =>
        _operations.ChangeApplicationUpgradePlanAsync(applicationId, plan, cancellationToken);

    /// <summary>The upgrade domain the application's upgrade waits to start next; null when it isn't waiting for one.</summary>
    public async Task<string?> GetNextUpgradeDomainAsync(string applicationId, CancellationToken cancellationToken) =>
        ClusterUpgrades.NextDomain(UpgradeReport.From(await _inspector.GetApplicationUpgradeAsync(applicationId, cancellationToken)));

    /// <summary>Lets a manual upgrade move on into <paramref name="upgradeDomain"/>.</summary>
    public Task ResumeUpgradeAsync(string applicationId, string upgradeDomain, CancellationToken cancellationToken) =>
        _operations.ResumeApplicationUpgradeAsync(applicationId, upgradeDomain, cancellationToken);

    /// <summary>Lets a manual upgrade move on into the domain it waits for; returns that domain.</summary>
    public async Task<string> ResumeNextUpgradeDomainAsync(string applicationId, CancellationToken cancellationToken)
    {
        var domain = await GetNextUpgradeDomainAsync(applicationId, cancellationToken)
            ?? throw new RefusedException($"The upgrade of {ClusterInspector.NameOf(applicationId)} isn't waiting to start an upgrade domain.", conflict: true);
        await ResumeUpgradeAsync(applicationId, domain, cancellationToken);
        return domain;
    }

    /// <summary>Rolls the running upgrade back: domains already upgraded go back to the previous version.</summary>
    public Task RollbackUpgradeAsync(string applicationId, CancellationToken cancellationToken) =>
        _operations.RollbackApplicationUpgradeAsync(applicationId, cancellationToken);

    /// <summary>
    /// Creates a service in an application. Refuses a type the application's version doesn't provide, or a stateful
    /// service of a stateless type and the other way round, and counts that can't work.
    /// </summary>
    public async Task CreateServiceAsync(string applicationId, NewService service, CancellationToken cancellationToken)
    {
        Check(service);
        var application = await _inspector.GetApplicationAsync(applicationId, cancellationToken);
        var applicationName = application.Name?.ToString() ?? ClusterInspector.NameOf(applicationId);
        var types = await _queries.GetServiceTypesAsync(application.TypeName, application.TypeVersion, cancellationToken);
        var type = types.FirstOrDefault(t => t.Name == service.TypeName)
            ?? throw new RefusedException($"{application.TypeName} {application.TypeVersion} has no service type {service.TypeName}.");
        if (type.Stateful != service.Stateful)
        {
            throw new RefusedException($"{type.Name} is a {(type.Stateful ? "stateful" : "stateless")} service type.");
        }
        await client.Services.CreateServiceAsync(applicationId, Description(applicationName, service), cancellationToken: cancellationToken);
    }

    /// <summary>The description the cluster gets for a new service; relative names are put under the application.</summary>
    public static ServiceDescription Description(string applicationName, NewService service)
    {
        var name = service.Name.Trim().StartsWith("fabric:/", StringComparison.Ordinal)
            ? service.Name.Trim()
            : $"{applicationName}/{service.Name.Trim().Trim('/')}";
        PartitionSchemeDescription partitions = service.Partitions <= 1
            ? new SingletonPartitionSchemeDescription()
            : new UniformInt64RangePartitionSchemeDescription(
                service.Partitions,
                long.MinValue.ToString(CultureInfo.InvariantCulture),
                long.MaxValue.ToString(CultureInfo.InvariantCulture));
        return service.Stateful
            ? new StatefulServiceDescription(
                new ServiceName(name),
                service.TypeName,
                partitions,
                targetReplicaSetSize: service.Count,
                minReplicaSetSize: service.MinReplicas,
                hasPersistedState: service.Persisted,
                applicationName: new ApplicationName(applicationName))
            : new StatelessServiceDescription(
                new ServiceName(name),
                service.TypeName,
                partitions,
                instanceCount: service.Count,
                applicationName: new ApplicationName(applicationName));
    }

    private static void Check(NewService service)
    {
        if (string.IsNullOrWhiteSpace(service.Name) || service.Name.Trim() == "fabric:/")
        {
            throw new RefusedException("Give the service a name.");
        }
        if (string.IsNullOrWhiteSpace(service.TypeName))
        {
            throw new RefusedException("Choose a service type.");
        }
        if (service.Stateful ? service.Count < 1 : service.Count is 0 or < -1)
        {
            throw new RefusedException(service.Stateful
                ? "A stateful service needs at least 1 replica."
                : "A stateless service needs at least 1 instance, or -1 for one on every node.");
        }
        if (service.Partitions < 1)
        {
            throw new RefusedException("A service needs at least 1 partition.");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> DefaultsAsync(string typeName, string typeVersion, CancellationToken cancellationToken)
    {
        var versions = await _inspector.GetApplicationTypeVersionsAsync(typeName, cancellationToken);
        var type = versions.FirstOrDefault(v => v.Version == typeVersion)
            ?? throw new RefusedException($"Version {typeVersion} of {typeName} isn't provisioned.");
        return type.DefaultParameterList ?? new Dictionary<string, string>();
    }

    private async Task<bool> DefaultServicesUpgradeEnabledAsync(CancellationToken cancellationToken) =>
        defaultServicesUpgradeEnabled is { } read
            ? await read(cancellationToken)
            : DefaultServices.UpgradeEnabled(await _inspector.GetClusterManifestAsync(cancellationToken));

    private static string FullName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("fabric:/", StringComparison.Ordinal) ? trimmed.TrimEnd('/') : "fabric:/" + trimmed.Trim('/');
    }
}
