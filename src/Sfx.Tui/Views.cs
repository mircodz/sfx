using Cellar.Layout;
using Cellar.Text;
using Cellar.Theming;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>
/// The pages of sfx. Every table follows the same rules: Enter (or a second click) drills into a row, <c>d</c>
/// describes it, and the row's actions are those of its entity, also offered on its describe page. Lists of
/// applications, and of what belongs to them across the cluster, show only what the current view includes.
/// </summary>
/// <param name="view">The view in use, asked on every load so switching it takes effect on the next refresh.</param>
public sealed partial class Views(ClusterQueries queries, IActionHost host, Func<ViewFilter>? view = null)
{
    private ViewFilter View() => view?.Invoke() ?? ViewFilter.None;

    private readonly ClusterInspector _inspect = new(queries.Client);
    private readonly ClusterOperations _ops = new(queries.Client);
    private readonly DeployedQueries _deployed = new(queries.Client);

    public IResourceView Nodes() =>
        new ResourceView<NodeRow>("nodes", "nodes", queries.GetNodesAsync, n => n.Name) { OnOpen = DeployedApplications }
            .Describing(DescribeNode)
            .Acting(NodeActions())
            .Column("NAME", Constraint.Fill(2), n => Plain(n.Name), n => n.Name)
            .Column("STATUS", Constraint.Length(9), n => Status(n.Status), n => n.Status)
            .Column("HEALTH", Constraint.Length(7), n => Health(n.Health), n => n.Health)
            .Column("TYPE", Constraint.Fill(1), n => Plain(n.NodeType), n => n.NodeType)
            .Column("ADDRESS", Constraint.Fill(1), n => Plain(n.Address), n => n.Address)
            .Column("UD", Constraint.Length(3), n => Plain(n.UpgradeDomain), n => n.UpgradeDomain)
            .Column("FD", Constraint.Fill(1), n => Plain(n.FaultDomain), n => n.FaultDomain)
            .Column("SEED", Constraint.Length(4), n => n.IsSeed ? Plain("yes") : Muted("no"), n => n.IsSeed)
            .Column("VERSION", Constraint.Fill(1), n => Plain(n.CodeVersion), n => n.CodeVersion)
            .Column("UPTIME", Constraint.Length(7), n => Plain(Duration(n.Uptime)), n => n.Uptime, Justify.Right);

    public IResourceView Applications() =>
        new ResourceView<ApplicationRow>("applications", "apps", queries.GetApplicationsAsync, a => a.Id) { OnOpen = Services }
            .InView(View, a => a.Name)
            .Describing(DescribeApplication)
            .Acting(ApplicationActions())
            .Column("NAME", Constraint.Fill(2), a => Plain(a.Name), a => a.Name)
            .Column("TYPE", Constraint.Fill(1), a => Plain(a.TypeName), a => a.TypeName)
            .Column("VERSION", Constraint.Length(12), a => Plain(a.TypeVersion), a => a.TypeVersion)
            .Column("STATUS", Constraint.Length(10), a => Status(a.Status), a => a.Status)
            .Column("HEALTH", Constraint.Length(8), a => Health(a.Health), a => a.Health);

    public IResourceView Services(ApplicationRow app) =>
        new ResourceView<ServiceRow>("services", app.Name, ct => queries.GetServicesAsync(app.Id, ct), s => s.Id) { OnOpen = Partitions }
            .Describing(service => DescribeService(app, service))
            .Acting(ServiceActions(app))
            .Column("NAME", Constraint.Fill(2), s => Plain(s.Name), s => s.Name)
            .Column("TYPE", Constraint.Fill(1), s => Plain(s.TypeName), s => s.TypeName)
            .Column("KIND", Constraint.Length(10), s => Plain(s.Kind), s => s.Kind)
            .Column("STATUS", Constraint.Length(10), s => Status(s.Status), s => s.Status)
            .Column("HEALTH", Constraint.Length(8), s => Health(s.Health), s => s.Health);

    public IResourceView Partitions(ServiceRow service) =>
        new ResourceView<PartitionRow>("partitions", service.Name, ct => queries.GetPartitionsAsync(service.Id, ct), p => p.Id.ToString())
            {
                OnOpen = partition => Replicas(service, partition),
            }
            .Describing(partition => DescribePartition(service, partition))
            .Acting(PartitionActions(service))
            .Column("ID", Constraint.Length(36), p => Plain(p.Id.ToString()), p => p.Id.ToString())
            .Column("KIND", Constraint.Length(10), p => Plain(p.Kind), p => p.Kind)
            .Column("PARTITIONING", Constraint.Length(13), p => Plain(p.PartitionKind), p => p.PartitionKind)
            .Column("REPLICAS", Constraint.Length(10), p => Plain(p.ReplicaSet), p => p.ReplicaSet)
            .Column("STATUS", Constraint.Length(14), p => Status(p.Status), p => p.Status)
            .Column("HEALTH", Constraint.Length(8), p => Health(p.Health), p => p.Health);

    public IResourceView Replicas(ServiceRow service, PartitionRow partition) =>
        new ResourceView<ReplicaRow>("replicas", partition.Id.ToString(), ct => queries.GetReplicasAsync(partition.Id, ct), r => r.Id)
            .Describing(replica => DescribeReplica(service, partition, replica))
            .Acting(ReplicaActions(service, partition))
            .Column("ID", Constraint.Length(19), r => Plain(r.Id), r => r.Id)
            .Column("NODE", Constraint.Fill(1), r => Plain(r.NodeName), r => r.NodeName)
            .Column("ROLE", Constraint.Length(16), r => Role(r.Role), r => r.Role)
            .Column("STATUS", Constraint.Length(10), r => Status(r.Status), r => r.Status)
            .Column("HEALTH", Constraint.Length(8), r => Health(r.Health), r => r.Health)
            .Column("ADDRESS", Constraint.Fill(2), r => Muted(r.Address), r => r.Address);

    // What's deployed on a node: applications → service packages → code packages.

    public IResourceView DeployedApplications(NodeRow node) =>
        new ResourceView<DeployedApplicationRow>("deployed applications", node.Name, ct => _deployed.GetApplicationsAsync(node.Name, ct), a => a.Id)
            {
                OnOpen = app => ServicePackages(node.Name, app),
            }
            .InView(View, a => a.Name)
            .Column("NAME", Constraint.Fill(2), a => Plain(a.Name), a => a.Name)
            .Column("TYPE", Constraint.Fill(1), a => Plain(a.TypeName), a => a.TypeName)
            .Column("STATUS", Constraint.Length(10), a => Status(a.Status), a => a.Status)
            .Column("HEALTH", Constraint.Length(8), a => Health(a.Health), a => a.Health);

    public IResourceView ServicePackages(string node, DeployedApplicationRow app) =>
        new ResourceView<DeployedServicePackageRow>("service packages", app.Name, ct => _deployed.GetServicePackagesAsync(node, app.Id, ct), p => p.Name + p.ActivationId)
            {
                OnOpen = package => CodePackages(node, app, package),
            }
            .Describing(package => DescribeServicePackage(node, app, package))
            .Column("NAME", Constraint.Fill(2), p => Plain(p.Name), p => p.Name)
            .Column("VERSION", Constraint.Length(12), p => Plain(p.Version), p => p.Version)
            .Column("STATUS", Constraint.Length(12), p => Status(p.Status), p => p.Status)
            .Column("ACTIVATION", Constraint.Fill(1), p => Plain(p.ActivationId), p => p.ActivationId);

    public IResourceView CodePackages(string node, DeployedApplicationRow app, DeployedServicePackageRow package) =>
        new ResourceView<DeployedCodePackageRow>("code packages", package.Name,
                async ct => (await _deployed.GetCodePackagesAsync(node, app.Id, package.Name, ct))
                    .Where(c => c.ActivationId == package.ActivationId)
                    .ToList(),
                c => c.Name)
            .Describing(codePackage => DescribeCodePackage(node, app, codePackage))
            .Acting(CodePackageActions(node, app))
            .Column("NAME", Constraint.Fill(2), c => Plain(c.Name), c => c.Name)
            .Column("STATUS", Constraint.Length(10), c => Status(c.Status), c => c.Status)
            .Column("VERSION", Constraint.Length(10), c => Plain(c.Version), c => c.Version)
            .Column("HOST", Constraint.Length(12), c => Plain(c.HostType), c => c.HostType)
            .Column("PID", Constraint.Length(8), c => Plain(c.ProcessId), c => long.TryParse(c.ProcessId, out var pid) ? pid : null, Justify.Right)
            .Column("LAST EXIT", Constraint.Length(9), c => Plain(c.LastExitCode), c => c.LastExitCode, Justify.Right)
            .Column("STARTS", Constraint.Length(7), c => Plain(c.Activations.ToString()), c => c.Activations, Justify.Right)
            .Column("FAILED", Constraint.Length(7), c => c.ActivationFailures > 0 ? Colored(c.ActivationFailures.ToString(), Theme.Current.Error) : Muted("0"), c => c.ActivationFailures, Justify.Right);

    /// <summary>
    /// The saved contexts, grouped by folder, with the mode and label their folders give them; opening one calls
    /// <paramref name="use"/> instead of navigating.
    /// </summary>
    public static IResourceView Contexts(ContextStore store, string current, Action<ClusterContext> use) =>
        new ResourceView<ResolvedContext>("contexts", "contexts", _ => Task.FromResult<IReadOnlyList<ResolvedContext>>(store.Contexts.Select(store.Resolve).ToList()), c => c.Context.Name)
            {
                OnOpen = context =>
                {
                    use(context.Context);
                    return null;
                },
            }
            .Column("NAME", Constraint.Fill(1), c => c.Context.Name == current ? Colored(c.Context.Name + " *", Theme.Current.Accent) : Plain(c.Context.Name), c => c.Context.Name)
            .Column("FOLDER", Constraint.Fill(1), Folder, c => new FolderOrder(c.Context.Folder, c.Context.Name))
            .Column("AUTH", Constraint.Length(14), c => Plain(c.Context.Auth.Count > 0 ? string.Join(",", c.Context.Auth) : "none"))
            .Column("MODE", Constraint.Length(9), c => Plain(ContextCommand.Mode(c.Context)))
            .Column("ENDPOINT", Constraint.Fill(2), c => Plain(c.Context.Endpoint), c => c.Context.Endpoint)
            .Sorting("FOLDER");

    /// <summary>"prod/europe PROD": the path, so filtering matches it, then the label in the folder's color.</summary>
    private static StyledText Folder(ResolvedContext context) =>
        context.Context.Folder is not { } path ? Muted("-")
        : context.Label is { } label ? StyledText.Of(path + " ").Append(label).Bold().Fg(FolderColor(context.Color))
        : StyledText.Of(path);

    private static StyledText Role(string role) => role == "Primary" ? Colored(role, Theme.Current.Accent) : Plain(role);
}
