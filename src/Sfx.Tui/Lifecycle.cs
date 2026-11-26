using System.Globalization;
using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

public sealed record ApplicationTypeUse(ApplicationTypeRow Type, int Applications);

/// <summary>
/// Application lifecycle: provisioning types (from this machine or the image store), creating and upgrading
/// applications, and creating and editing services. Each is a form, a review of the changes, then the change,
/// followed in the activity strip until done.
/// </summary>
public sealed partial class Views
{
    private const string Available = "Available";
    private const string ParameterPrefix = "param:";
    private const string PolicyPrefix = "policy:";
    private const string DefaultChoice = "default";

    private ApplicationLifecycle? _lifecycle;
    private ServiceSettings? _settings;

    private ApplicationLifecycle Lifecycle => _lifecycle ??= new ApplicationLifecycle(queries.Client);

    private ServiceSettings Settings => _settings ??= new ServiceSettings(queries.Client);

    private static bool IsUserApp(ApplicationRow app) => app.Id != ClusterQueries.SystemApplicationId;

    private static bool IsUpgrading(ApplicationRow app) => app.Status == "Upgrading";

    public IResourceView ApplicationTypes() =>
        new ResourceView<ApplicationTypeUse>("application types", "types", ApplicationTypeUsesAsync, t => t.Type.Name + ":" + t.Type.Version)
            {
                Empty = "no application types; :provision <path> uploads one",
            }
            .HighlightingChanges()
            .Describing(t => DescribeApplicationType(t.Type))
            .Acting(ApplicationTypeActions().Select(Unwrap), host)
            .Offering(
                [
                    new AppAction("page.provision", "Provision a package from this machine", () => host.Command("provision "))
                    {
                        Group = "Application types",
                        Keys = [Provision],
                        When = () => host.Allows(ActionRisk.Confirm, power: false),
                    },
                ])
            .Column("NAME", Constraint.Fill(2), t => Plain(t.Type.Name), t => t.Type.Name)
            .Column("VERSION", Constraint.Length(14), t => Plain(t.Type.Version), t => t.Type.Version)
            .Column("STATUS", Constraint.Length(14), t => TypeStatus(t.Type.Status), t => t.Type.Status)
            .Column(
                "APPS",
                Constraint.Length(5),
                t => t.Applications == 0 ? Muted("0") : Plain(t.Applications.ToString(CultureInfo.InvariantCulture)),
                t => t.Applications,
                Justify.Right)
            .Column(
                "PARAMS",
                Constraint.Length(7),
                t => Plain(t.Type.DefaultParameters.Count.ToString(CultureInfo.InvariantCulture)),
                t => t.Type.DefaultParameters.Count,
                Justify.Right)
            .Column("DETAILS", Constraint.Fill(2), t => Muted(t.Type.StatusDetails), t => t.Type.StatusDetails);

    private static EntityAction<ApplicationTypeUse> Unwrap(EntityAction<ApplicationTypeRow> action) =>
        new(
            action.Title,
            action.Risk,
            use => action.Run(use.Type),
            action.Keys,
            action.Commands,
            action.Power,
            action.AppliesTo is { } applies ? use => applies(use.Type) : null);

    private static StyledText TypeStatus(string status) => status switch
    {
        Available => Colored(status, Theme.Current.Success),
        "Failed" => Colored(status, Theme.Current.Error),
        _ => Colored(status, Theme.Current.Warning),
    };

    private async Task<IReadOnlyList<ApplicationTypeUse>> ApplicationTypeUsesAsync(CancellationToken cancellationToken)
    {
        var apps = queries.GetApplicationsAsync(cancellationToken);
        var types = await queries.GetApplicationTypesAsync(cancellationToken);
        var used = (await apps).GroupBy(a => (a.TypeName, a.TypeVersion)).ToDictionary(g => g.Key, g => g.Count());
        return types.Select(t => new ApplicationTypeUse(t, used.GetValueOrDefault((t.Name, t.Version)))).ToList();
    }

    public IResourceView DescribeApplicationType(ApplicationTypeRow type) =>
        new DescribeView(
            "application type",
            $"{type.Name} {type.Version}",
            [
                Tab("Service types", async ct => Lines.Table(
                    ["NAME", "KIND", "MANIFEST", "VERSION"],
                    (await queries.GetServiceTypesAsync(type.Name, type.Version, ct))
                        .Select(s => (IReadOnlyList<string>)[s.Name, s.Stateful ? "Stateful" : "Stateless", s.Manifest, s.ManifestVersion])
                        .ToList())),
                Tab("Parameters", _ => Task.FromResult(Lines.Fields(
                    type.DefaultParameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new Field(p.Key, p.Value))))),
                Tab("Applications", async ct => Lines.Table(
                    ["NAME", "VERSION", "STATUS"],
                    (await queries.GetApplicationsAsync(ct))
                        .Where(a => a.TypeName == type.Name)
                        .Select(a => (IReadOnlyList<string>)[a.Name, a.TypeVersion, a.Status])
                        .ToList())),
                Tab("Manifest", async ct => Lines.Text(await _inspect.GetApplicationManifestAsync(type.Name, type.Version, ct))),
            ],
            actions: Bind(ApplicationTypeActions(), type, "Application type"));

    public IReadOnlyList<EntityAction<ApplicationTypeRow>> ApplicationTypeActions() =>
    [
        new("Create an application of this type", ActionRisk.Confirm, CreateApplication, [Create], ["create", "new"], AppliesTo: t => t.Status == Available),

        new(
            "Unprovision this type version",
            ActionRisk.Destructive,
            t => host.Run(new Operation(
                $"Unprovision {t.Name} {t.Version}",
                t.Version,
                ActionRisk.Destructive,
                (ct, _) => Lifecycle.UnprovisionAsync(t.Name, t.Version, ct),
                "Its packages leave the cluster's store; applications on this version must be upgraded or deleted first.")
            {
                Track = async (
                    _,
                    ct) => Tracking.Gone((await queries.GetApplicationTypesAsync(ct)).Any(other => other.Name == t.Name && other.Version == t.Version)),
            }),
            [Delete],
            ["unprovision"],
            AppliesTo: t => t.Status is Available or "Failed"),
    ];

    /// <summary>The create form: the name, the version, and every parameter of the type with its default.</summary>
    private void CreateApplication(ApplicationTypeRow type) =>
        host.Edit(
            $"Create an application of {type.Name} {type.Version}",
            _ => Task.FromResult(CreateApplicationFields(type)),
            fields => CreateApplicationOperation(type, fields),
            unchanged: true);

    internal static IReadOnlyList<FormField> CreateApplicationFields(ApplicationTypeRow type) =>
    [
        new FormField("name", "Name", "fabric:/" + (type.Name.EndsWith("Type", StringComparison.Ordinal) && type.Name.Length > 4 ? type.Name[..^4] : type.Name))
        {
            Required = true,
            Hint = "fabric:/name; unique in the cluster",
            Validate = name => FullName(name).Length <= "fabric:/".Length ? "give it a name" : null,
        },
        new FormField("version", "Version", type.Version) { ReadOnly = true, Note = type.Name },
        .. type.DefaultParameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p =>
            new FormField(ParameterPrefix + p.Key, p.Key, p.Value) { Section = "Parameters", Note = "default", Hint = "the type's default unless changed" }),
    ];

    internal Operation CreateApplicationOperation(ApplicationTypeRow type, IReadOnlyList<FormField> fields)
    {
        var name = FullName(fields.First(f => f.Key == "name").Value);
        var parameters = fields.Where(f => f.Key.StartsWith(ParameterPrefix, StringComparison.Ordinal) && f.IsChanged)
            .ToDictionary(f => f.Key[ParameterPrefix.Length..], f => f.Value);
        return new Operation(
            $"Create {name} ({type.Name} {type.Version})",
            name,
            ActionRisk.Confirm,
            (ct, _) => Lifecycle.CreateApplicationAsync(name, type.Name, type.Version, parameters, ct),
            parameters.Count == 0 ? "Every parameter takes the type's default." : null)
        {
            Details = Changes(fields.Where(f => f.Key.StartsWith(ParameterPrefix, StringComparison.Ordinal))),
            Track = async (_, ct) => Tracking.Created((await queries.GetApplicationsAsync(ct)).Any(a => a.Name == name)),
        };
    }

    private static string FullName(string name)
    {
        name = name.Trim();
        return name.StartsWith("fabric:/", StringComparison.Ordinal) ? name.TrimEnd('/') : "fabric:/" + name.Trim('/');
    }

    /// <summary><c>ctrl+n</c> on the applications: picks an available type version, then the create form.</summary>
    private void CreateApplicationOfAnyType() =>
        host.Load("reading the application types", queries.GetApplicationTypesAsync, types =>
        {
            var available = types.Where(t => t.Status == Available).ToList();
            if (available.Count == 0)
            {
                host.Warn("no application type is provisioned; :provision <path> uploads one");
                return;
            }
            host.Pick("Create an application of", _ => Task.FromResult<IReadOnlyList<Choice>>(
                available.Select(t => new Choice($"{t.Name} {t.Version}", $"{t.DefaultParameters.Count} parameters", () => CreateApplication(t))).ToList()));
        });

    /// <summary>
    /// <c>:provision path</c>: reads the package's type and version, then (once confirmed) uploads it to the image store,
    /// provisions it, and follows it until the type is available.
    /// </summary>
    public void ProvisionLocal(string path)
    {
        if (!host.Allows(ActionRisk.Confirm, power: false))
        {
            host.Warn("this context is read-only");
            return;
        }

        var full = PathCompletion.Expand(path);
        host.Load($"reading {path}", _ => Task.Run(() =>
        {
            using var package = LocalPackage.Open(full);
            return (package.TypeName, package.TypeVersion, package.StorePath);
        }), package => host.Run(ProvisionLocalOperation(full, package.TypeName, package.TypeVersion, package.StorePath)));
    }

    internal Operation ProvisionLocalOperation(string path, string typeName, string typeVersion, string storePath)
    {
        var progress = new OperationProgress();
        return new Operation(
            $"Provision {typeName} {typeVersion}",
            typeName,
            ActionRisk.Confirm,
            async (ct, _) =>
            {
                await Lifecycle.UploadAndProvisionAsync(path, null, Reporting(progress, "uploading"), ct);
                progress.Report(null, "provisioning");
            },
            "Uploads the package to the image store, then registers its type; :types shows it once it's available.")
        {
            Details = Facts(("From", path), ("Type", typeName), ("Version", typeVersion), ("To", $"image store › {storePath}")),
            Progress = progress,
            Track = async (_, ct) => Tracking.TypeProvisioned(typeName, typeVersion, new HashSet<string>(), await queries.GetApplicationTypesAsync(ct)),
        };
    }

    /// <summary>Provisions a package already in the image store, and follows it until its type is available.</summary>
    private void ProvisionStored(string storePath) =>
        host.Load("reading the application types", queries.GetApplicationTypesAsync, before =>
        {
            var known = before.Select(Tracking.Key).ToHashSet();
            host.Run(new Operation(
                $"Provision the package at {storePath}",
                ImageStore.NameOf(storePath),
                ActionRisk.Confirm,
                (ct, _) => Lifecycle.ProvisionAsync(storePath, ct),
                "Registers the application type in the package; :types shows it once it's available.")
            {
                Track = async (_, ct) => Tracking.TypeProvisioned(null, null, known, await queries.GetApplicationTypesAsync(ct)),
            });
        });

    /// <summary>Upload progress as the activity strip shows it: how much of how much.</summary>
    private static IProgress<UploadProgress> Reporting(OperationProgress progress, string verb) =>
        new Reporter<UploadProgress>(u => progress.Report(
            u.Fraction,
            u.BytesTotal > 0 ? $"{verb} {Bytes(u.BytesDone)} of {Bytes(u.BytesTotal)}" : $"{verb} {u.FilesDone} of {u.FilesTotal} files"));

    /// <summary>Reports right away, on the reporting thread, so reports keep their order.</summary>
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>"Type  VotingType" lines for a confirmation.</summary>
    private static IReadOnlyList<StyledText> Facts(params (string Label, string Value)[] facts)
    {
        var width = facts.Max(f => f.Label.Length) + 2;
        return facts.Select(f => StyledText.Of(f.Label.PadRight(width)).Fg(Theme.Current.Muted).Append(f.Value).Fg(Theme.Current.Foreground)).ToList();
    }

    public IReadOnlyList<EntityAction<ApplicationRow>> ApplicationLifecycleActions() =>
    [
        new(
            "Edit parameters, or upgrade",
            ActionRisk.Confirm,
            a => UpgradeApplication(a, otherVersion: false),
            [Edit],
            ["edit", "params"],
            AppliesTo: a => IsUserApp(a) && !IsUpgrading(a)),

        new(
            "Upgrade to another version",
            ActionRisk.Confirm,
            a => UpgradeApplication(a, otherVersion: true),
            [Upgrade],
            ["upgrade"],
            AppliesTo: a => IsUserApp(a) && !IsUpgrading(a)),

        new("Steer the running upgrade", ActionRisk.Confirm, a => ApplicationUpgradeControls(a.Id, a.Name), [Upgrade], AppliesTo: IsUpgrading),

        new(
            "Change how the running upgrade proceeds",
            ActionRisk.Confirm,
            a => ChangeApplicationUpgradePlan(a.Id, a.Name),
            Commands: ["upgrade-mode"],
            AppliesTo: IsUpgrading),

        new(
            "Resume the upgrade into the next domain",
            ActionRisk.Confirm,
            a => ResumeApplicationUpgrade(a.Id, a.Name),
            Commands: ["resume"],
            AppliesTo: IsUpgrading),

        new("Roll back the running upgrade", ActionRisk.Confirm, a => RollbackApplicationUpgrade(a.Id, a.Name), Commands: ["rollback"], AppliesTo: IsUpgrading),
    ];

    /// <summary>What the upgrade form was opened with; the review needs it.</summary>
    internal sealed class UpgradeFormState
    {
        public required ApplicationRow App { get; init; }

        public required IReadOnlyList<ApplicationTypeRow> Versions { get; init; }

        public required IReadOnlyDictionary<string, string> Current { get; init; }

        public string? StartedBefore { get; init; }
    }

    /// <summary>
    /// <c>e</c> and <c>u</c>: one form for both. The version (today's for <c>e</c>, the newest other for <c>u</c>), the
    /// plan, every parameter of any provisioned version (empty follows the version's default), and the policy.
    /// </summary>
    private void UpgradeApplication(ApplicationRow app, bool otherVersion)
    {
        UpgradeFormState? state = null;
        host.Edit(
            otherVersion ? $"Upgrade {app.Name}" : $"Edit {app.Name}",
            async ct =>
            {
                var versions = (await queries.GetApplicationTypesAsync(ct)).Where(t => t.Name == app.TypeName && t.Status == Available).ToList();
                var current = await Lifecycle.GetParametersAsync(app.Id, ct);
                string? started = null;
                try
                {
                    started = UpgradeReport.From(await _inspect.GetApplicationUpgradeAsync(app.Id, ct)).Started;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
                state = new UpgradeFormState { App = app, Versions = versions, Current = current, StartedBefore = started };
                return UpgradeFields(state, otherVersion);
            },
            fields => UpgradeOperation(state!, fields),
            unchanged: otherVersion);
    }

    /// <summary>The upgrade form's fields; for <paramref name="otherVersion"/>, refuses when no other version is provisioned.</summary>
    internal static IReadOnlyList<FormField> UpgradeFields(UpgradeFormState state, bool otherVersion)
    {
        var app = state.App;
        var versions = state.Versions.Select(v => v.Version).OrderBy(v => v, VersionOrder.Instance).ToList();
        if (!versions.Contains(app.TypeVersion))
        {
            versions.Insert(0, app.TypeVersion);
        }
        var target = app.TypeVersion;
        if (otherVersion)
        {
            target = versions.LastOrDefault(v => v != app.TypeVersion)
                ?? throw new RefusedException($"no other version of {app.TypeName} is provisioned; :provision <path> uploads one");
        }

        var defaults = state.Versions.FirstOrDefault(v => v.Version == app.TypeVersion)?.DefaultParameters ?? new Dictionary<string, string>();
        var names = state.Versions.SelectMany(v => v.DefaultParameters.Keys).Concat(state.Current.Keys)
            .Distinct().Order(StringComparer.OrdinalIgnoreCase);
        return
        [
            new FormField("version", "Version", target)
            {
                Kind = FormFieldKind.Choice,
                Choices = versions,
                Note = $"now {app.TypeVersion}",
                Hint = "the same version changes only parameters: services restart domain by domain",
            },
            new FormField("plan", "Plan", UpgradePlan.All[0].Title)
            {
                Kind = FormFieldKind.Choice,
                Choices = UpgradePlan.All.Select(p => p.Title).ToList(),
                Hint = string.Join("; ", UpgradePlan.All.Select(p => $"{p.Title.ToLowerInvariant()}: {p.Hint}")),
            },
            .. names.Select(name => new FormField(ParameterPrefix + name, name, state.Current.GetValueOrDefault(name) ?? "")
            {
                Section = "Parameters",
                Note = defaults.TryGetValue(name, out var value) ? $"default {(value.Length == 0 ? "empty" : value)}"
                    : $"in {string.Join(", ", state.Versions.Where(v => v.DefaultParameters.ContainsKey(name)).Select(v => v.Version))}",
                Hint = "empty follows the version's default",
            }),
            .. PolicyFields(cluster: false),
        ];
    }

    internal Operation UpgradeOperation(UpgradeFormState state, IReadOnlyList<FormField> fields)
    {
        var app = state.App;
        var version = fields.First(f => f.Key == "version").Value;
        var plan = PlanOf(fields);
        var set = new Dictionary<string, string>();
        var reset = new List<string>();
        foreach (var field in fields.Where(f => f.Key.StartsWith(ParameterPrefix, StringComparison.Ordinal) && f.IsChanged))
        {
            var name = field.Key[ParameterPrefix.Length..];
            if (field.Value.Length == 0)
            {
                reset.Add(name);
            }
            else
            {
                set[name] = field.Value;
            }
        }

        if (version == app.TypeVersion && set.Count == 0 && reset.Count == 0)
        {
            throw new RefusedException("nothing to change: pick another version, or change a parameter");
        }
        var target = state.Versions.FirstOrDefault(v => v.Version == version)
            ?? throw new RefusedException($"version {version} of {app.TypeName} isn't provisioned");
        var merged = ApplicationLifecycle.MergeParameters(state.Current, target.DefaultParameters, set, reset, version);
        var request = new ApplicationUpgradeRequest(version, plan, set, reset, ParsePolicy(fields, plan, cluster: false));

        var details = Changes(fields).ToList();
        if (merged.Dropped.Count > 0)
        {
            details.Add(StyledText.Of($"dropped, {version} doesn't define them: {string.Join(", ", merged.Dropped)}").Fg(Theme.Current.Warning));
        }
        return new Operation(
            ApplicationLifecycle.Describe(app.Name, request),
            app.Name,
            ActionRisk.Confirm,
            (ct, _) => Lifecycle.UpgradeAsync(app.Id, request, ct),
            version == app.TypeVersion
                ? "Runs as an upgrade to the same version: services restart domain by domain."
                : "Rolls through the upgrade domains one at a time; :upgrades shows how far it got.")
        {
            Details = details,
            Track = async (_, ct) => Tracking.Upgrade(UpgradeReport.From(await _inspect.GetApplicationUpgradeAsync(app.Id, ct)), version, state.StartedBefore),
            TrackFor = TimeSpan.FromHours(2),
        };
    }

    private static UpgradePlan PlanOf(IReadOnlyList<FormField> fields)
    {
        var title = fields.First(f => f.Key == "plan").Value;
        return UpgradePlan.All.FirstOrDefault(p => p.Title.Equals(title, StringComparison.OrdinalIgnoreCase)) ?? UpgradePlan.All[0];
    }

    private static readonly (string Key, string Label, string Hint)[] PolicyDurations =
    [
        ("healthCheckWait", "Health check wait", "after a domain, before checking health"),
        ("healthCheckStable", "Health check stable", "how long health must stay good"),
        ("healthCheckRetry", "Health check retry", "how long to keep retrying a failed check"),
        ("upgradeDomainTimeout", "Domain timeout", "longest one domain may take"),
        ("upgradeTimeout", "Upgrade timeout", "longest the whole upgrade may take"),
        ("replicaSetCheckTimeout", "Replica set check timeout", "how long to wait for replicas to be safe before going on anyway"),
    ];

    private static readonly (string Key, string Label)[] PolicyFlags =
    [
        ("forceRestart", "Force restart"),
        ("considerWarningAsError", "Warnings count as errors"),
    ];

    private static readonly (string Key, string Label)[] ApplicationLimits =
    [
        ("maxPercentUnhealthyDeployedApplications", "Max unhealthy deployed apps %"),
        ("maxPercentUnhealthyServices", "Max unhealthy services %"),
        ("maxPercentUnhealthyPartitionsPerService", "Max unhealthy partitions %"),
        ("maxPercentUnhealthyReplicasPerPartition", "Max unhealthy replicas %"),
    ];

    private static readonly (string Key, string Label)[] ClusterLimits =
    [
        ("maxPercentUnhealthyNodes", "Max unhealthy nodes %"),
        ("maxPercentUnhealthyApplications", "Max unhealthy applications %"),
    ];

    /// <summary>The optional policy fields; empty ones keep the cluster's defaults.</summary>
    internal static IEnumerable<FormField> PolicyFields(bool cluster)
    {
        const string section = "Policy (empty keeps the cluster's defaults; monitored plans only)";
        foreach (var (key, label, hint) in PolicyDurations)
        {
            yield return new FormField(PolicyPrefix + key, label) { Kind = FormFieldKind.Duration, Section = section, Hint = hint + "; e.g. 30s, 5m, 1h" };
        }
        foreach (var (key, label) in PolicyFlags)
        {
            yield return new FormField(PolicyPrefix + key, label, DefaultChoice)
            {
                Kind = FormFieldKind.Choice,
                Choices = [DefaultChoice, "true", "false"],
                Section = section,
            };
        }
        foreach (var (key, label) in cluster ? ClusterLimits : ApplicationLimits)
        {
            yield return new FormField(PolicyPrefix + key, label)
            {
                Kind = FormFieldKind.Number,
                Integer = true,
                Min = 0,
                Max = 100,
                Section = section,
                Hint = "0 to 100",
            };
        }
    }

    /// <summary>The policy typed into <see cref="PolicyFields"/>, or null when it's all left to the defaults.</summary>
    internal static UpgradePolicy? ParsePolicy(IReadOnlyList<FormField> fields, UpgradePlan plan, bool cluster)
    {
        var values = new Dictionary<string, string>();
        foreach (var field in fields.Where(f => f.Key.StartsWith(PolicyPrefix, StringComparison.Ordinal)))
        {
            var value = field.Value.Trim();
            if (value.Length == 0 || value == DefaultChoice)
            {
                continue;
            }
            if (field.Kind == FormFieldKind.Duration)
            {
                value = FormField.TryParseDuration(value, out var duration)
                    ? ((long)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                    : throw new RefusedException($"{field.Label} must be a duration like 30s, 5m or 1h.");
            }
            values[field.Key[PolicyPrefix.Length..]] = value;
        }
        var policy = UpgradePolicy.Parse(values, plan.Monitored, cluster);
        policy?.Require(plan.Monitored, cluster);
        return policy;
    }

    private void ApplicationUpgradeControls(string id, string name) =>
        host.Pick($"{name} is upgrading", _ => Task.FromResult<IReadOnlyList<Choice>>(
            [
                new("Resume into the next domain", "manual upgrades wait for this", () => ResumeApplicationUpgrade(id, name)),
                new("Change how it proceeds", "monitored, manual, automatic…", () => ChangeApplicationUpgradePlan(id, name)),
                new("Roll back", "domains already upgraded go back", () => RollbackApplicationUpgrade(id, name)),
            ]));

    private void ChangeApplicationUpgradePlan(string id, string name) =>
        PickPlan($"Continue the upgrade of {name} as", plan =>
            host.Run(Op(
                $"Continue the upgrade of {name}: {plan.Title.ToLowerInvariant()}",
                name,
                ActionRisk.Confirm,
                ct => Lifecycle.ChangeUpgradePlanAsync(id, plan, ct))));

    private void ResumeApplicationUpgrade(string id, string name) =>
        host.Load($"reading the upgrade of {name}", ct => Lifecycle.GetNextUpgradeDomainAsync(id, ct), domain =>
        {
            if (domain is null)
            {
                host.Warn($"the upgrade of {name} isn't waiting to start a domain");
                return;
            }
            host.Run(Op($"Resume the upgrade of {name} into domain {domain}", name, ActionRisk.Confirm, ct => Lifecycle.ResumeUpgradeAsync(id, domain, ct)));
        });

    private void RollbackApplicationUpgrade(string id, string name) =>
        host.Run(Op(
            $"Roll back the upgrade of {name}",
            name,
            ActionRisk.Confirm,
            ct => Lifecycle.RollbackUpgradeAsync(id, ct),
            "Domains already upgraded go back to the previous version.") with
        {
            Track = async (_, ct) => RolledBack(UpgradeReport.From(await _inspect.GetApplicationUpgradeAsync(id, ct))),
            TrackFor = TimeSpan.FromHours(1),
        });

    private static OperationState RolledBack(UpgradeReport report) => report.State switch
    {
        nameof(Microsoft.ServiceFabric.Common.UpgradeState.RollingBackCompleted) => OperationState.Finished("rolled back"),
        nameof(Microsoft.ServiceFabric.Common.UpgradeState.Failed) => OperationState.Failure("failed" + (report.FailureReason is { Length: > 0 } and not "None" ? $": {report.FailureReason}" : "")),
        _ => OperationState.Running("rolling back"),
    };

    private void PickPlan(string title, Action<UpgradePlan> pick) =>
        host.Pick(title, _ => Task.FromResult<IReadOnlyList<Choice>>(
            UpgradePlan.All.Select(plan => new Choice(plan.Title, plan.Hint, () => pick(plan))).ToList()));

    /// <summary>The services page's own actions: creating one, in an application that isn't the system's.</summary>
    private IEnumerable<AppAction> ServicePageActions(ApplicationRow app) =>
    [
        new AppAction("page.create-service", "Create a service", () => CreateService(app))
        {
            Group = "Services",
            Keys = [Create],
            Commands = ["create", "new"],
            When = () => IsUserApp(app) && host.Allows(ActionRisk.Confirm, power: false),
        },
    ];

    private IEnumerable<AppAction> ApplicationPageActions() =>
    [
        new AppAction("page.create-application", "Create an application", CreateApplicationOfAnyType)
        {
            Group = "Applications",
            Keys = [Create],
            Commands = ["create", "new"],
            When = () => host.Allows(ActionRisk.Confirm, power: false),
        },
    ];

    public IReadOnlyList<EntityAction<ServiceRow>> ServiceLifecycleActions(ApplicationRow app) =>
    [
        new(
            "Edit settings",
            ActionRisk.Confirm,
            EditServiceSettings,
            [Edit],
            ["edit", "settings"],
            AppliesTo: s => IsUserApp(app) && s.Kind is nameof(ServiceKind.Stateful) or nameof(ServiceKind.Stateless)),
    ];

    private void CreateService(ApplicationRow app)
    {
        IReadOnlyList<ServiceTypeRow> types = [];
        host.Edit(
            $"Create a service in {app.Name}",
            async ct =>
            {
                types = await queries.GetServiceTypesAsync(app.TypeName, app.TypeVersion, ct);
                return types.Count == 0
                    ? throw new RefusedException($"{app.TypeName} {app.TypeVersion} has no service types")
                    : CreateServiceFields(types);
            },
            fields => CreateServiceOperation(app, types, fields),
            unchanged: true);
    }

    internal static IReadOnlyList<FormField> CreateServiceFields(IReadOnlyList<ServiceTypeRow> types)
    {
        var first = types[0];
        var type = FormField.Choice("type", "Service type", first.Name, types.Select(t => t.Name).ToList());
        return
        [
            new FormField("name", "Name", first.Name.EndsWith("Type", StringComparison.Ordinal) && first.Name.Length > 4 ? first.Name[..^4] : first.Name)
            {
                Required = true,
                Hint = "under the application's name, or a full fabric:/ name",
            },
            type,
            new FormField("kind", "Kind", first.Stateful ? "Stateful" : "Stateless")
            {
                Kind = FormFieldKind.Choice, Choices = ["Stateful", "Stateless"],
                Validate = kind => types.FirstOrDefault(t => t.Name == type.Value) is { } chosen && chosen.Stateful != (kind == "Stateful")
                    ? $"{chosen.Name} is a {(chosen.Stateful ? "stateful" : "stateless")} service type" : null,
            },
            new FormField("count", "Replicas or instances", first.Stateful ? "3" : "1")
            {
                Kind = FormFieldKind.Number,
                Integer = true,
                Min = -1,
                Required = true,
                Hint = "target replicas when stateful; instances when stateless (-1: one on every node)",
            },
            new FormField("partitions", "Partitions", "1")
            {
                Kind = FormFieldKind.Number,
                Integer = true,
                Min = 1,
                Required = true,
                Note = "1: singleton, more: Int64 ranges",
            },
            new FormField("persisted", "Persisted state", first.Stateful ? "true" : "false") { Kind = FormFieldKind.Flag, Note = "stateful services only" },
        ];
    }

    internal Operation CreateServiceOperation(ApplicationRow app, IReadOnlyList<ServiceTypeRow> types, IReadOnlyList<FormField> fields)
    {
        string Value(string key) => fields.First(f => f.Key == key).Value.Trim();
        var typeName = Value("type");
        var type = types.FirstOrDefault(t => t.Name == typeName) ?? throw new RefusedException($"no service type {typeName}");
        var stateful = Value("kind") == "Stateful";
        if (type.Stateful != stateful)
        {
            throw new RefusedException($"{type.Name} is a {(type.Stateful ? "stateful" : "stateless")} service type");
        }
        var count = int.Parse(Value("count"), CultureInfo.InvariantCulture);
        if (stateful ? count < 1 : count is 0 or < -1)
        {
            throw new RefusedException(stateful ? "a stateful service needs at least 1 replica" : "instances: at least 1, or -1 for every node");
        }
        var partitions = int.Parse(Value("partitions"), CultureInfo.InvariantCulture);
        var persisted = fields.First(f => f.Key == "persisted").IsOn;
        var service = new NewService(Value("name"), typeName, stateful, count, partitions, persisted);
        var name = ServiceName(app.Name, service.Name);
        var shape = stateful ? $"{count} replica{(count == 1 ? "" : "s")} (min {service.MinReplicas}){(persisted ? ", persisted" : "")}"
            : count == -1 ? "an instance on every node" : $"{count} instance{(count == 1 ? "" : "s")}";
        return new Operation($"Create {name}", name, ActionRisk.Confirm, (ct, _) => Lifecycle.CreateServiceAsync(app.Id, service, ct))
        {
            Details = Facts(
                ("Type", $"{type.Name} ({(stateful ? "stateful" : "stateless")})"),
                ("Size", shape),
                ("Partitions", partitions == 1 ? "1 (singleton)" : $"{partitions} (Int64 ranges)")),
            Track = async (_, ct) => Tracking.Created((await queries.GetServicesAsync(app.Id, ct)).Any(s => s.Name == name)),
        };
    }

    /// <summary>A service's full name, as the cluster will have it: a relative name goes under the application.</summary>
    internal static string ServiceName(string applicationName, string name)
    {
        name = name.Trim();
        return name.StartsWith("fabric:/", StringComparison.Ordinal) ? name : $"{applicationName}/{name.Trim('/')}";
    }

    /// <summary><c>e</c> on a service: every setting with its value; the review lists the changes, made in one update.</summary>
    private void EditServiceSettings(ServiceRow service)
    {
        ServiceSettingSet? current = null;
        host.Edit(
            $"Edit {service.Name}",
            async ct =>
            {
                current = await Settings.GetAsync(service.Id, ct);
                return SettingFields(current);
            },
            fields => SettingsOperation(service, current!, fields));
    }

    internal static IReadOnlyList<FormField> SettingFields(ServiceSettingSet set) =>
        set.Settings.Select(setting => new FormField(setting.Key, setting.Title, setting.Value)
        {
            Kind = setting.Kind switch
            {
                SettingKind.Choice or SettingKind.Flag => FormFieldKind.Choice,
                SettingKind.Count => FormFieldKind.Number,
                _ => FormFieldKind.Text,
            },
            Choices = setting.Kind is SettingKind.Choice or SettingKind.Flag ? setting.Choices ?? ["true", "false"] : [],
            Integer = setting.Kind == SettingKind.Count,
            Min = setting.Min,
            Max = setting.Max,
            Note = setting.Value.Length == 0 ? "cluster default" : setting.Kind == SettingKind.Seconds ? "seconds" : null,
            Hint = setting.Kind switch
            {
                SettingKind.Seconds => "seconds, or a number with s, m, h or d (30s, 5m, 1h)",
                SettingKind.Choice or SettingKind.Flag => "enter or ←/→ to change",
                SettingKind.Text => "empty clears it",
                _ => null,
            },
            Validate = text =>
            {
                try
                {
                    ServiceSettings.Parse(setting, text);
                    return null;
                }
                catch (RefusedException ex)
                {
                    return ex.Message;
                }
            },
        }).ToList();

    internal Operation SettingsOperation(ServiceRow service, ServiceSettingSet current, IReadOnlyList<FormField> fields)
    {
        var changes = ServiceSettings.Changes(current, fields.Where(f => f.IsChanged).ToDictionary(f => f.Key, f => f.Value));
        if (changes.Count == 0)
        {
            throw new RefusedException("those are the values it has already");
        }
        var scales = changes.Keys.Any(key => key is "count" or "min-replicas" or "min-instances");
        return new Operation(
            ServiceSettings.Describe(service.Name, current.Settings, changes),
            service.Name,
            ActionRisk.Confirm,
            (ct, _) => Settings.UpdateAsync(service.Id, changes, ct),
            scales ? "Replicas or instances are added or dropped to match." : null)
        {
            Details = Changes(fields),
        };
    }

    /// <summary>Orders versions like 1.2.10 after 1.2.9; anything else as text.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(string? x, string? y) =>
            Version.TryParse(x, out var a) && Version.TryParse(y, out var b) ? a.CompareTo(b) : string.CompareOrdinal(x, y);
    }
}
