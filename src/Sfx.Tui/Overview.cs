using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using static Sfx.Tui.Styles;

namespace Sfx.Tui;

/// <summary>Getting around: the summary line, what's unhealthy, search results, and jumps between related entities.</summary>
public sealed partial class Views
{
    private ClusterOverview? _overview;

    public ClusterOverview Overview => _overview ??= new ClusterOverview(queries);

    private static readonly Dictionary<string, string> Plurals = new()
    {
        ["Node"] = "nodes",
        ["Application"] = "apps",
        ["Service"] = "services",
        ["Partition"] = "partitions",
        ["Replica"] = "replicas",
    };

    /// <summary>
    /// The cluster in one line: its health in words, how many of each kind of entity it has (with those in warning
    /// and error as W2 E1, in their colors), and its version. Plain ASCII, so it reads in any terminal and font.
    /// </summary>
    public static StyledText SummaryLine(ClusterSummary summary)
    {
        var theme = Theme.Current;
        var color = summary.Health.State switch
        {
            HealthState.Ok => theme.Success,
            HealthState.Warning => theme.Warning,
            HealthState.Error => theme.Error,
            _ => theme.Muted,
        };
        var line = StyledText.Of(" ")
            .Append(summary.Health.State == HealthState.Invalid ? "Unknown" : summary.Health.State.ToString()).Bold().Fg(color);
        foreach (var count in summary.Counts)
        {
            line.Append("  ·  ").Fg(theme.Muted)
                .Append($"{count.Healthy + count.Warning + count.Error} {Plurals.GetValueOrDefault(count.Kind, count.Kind.ToLowerInvariant())}").Fg(theme.Foreground);
            if (count.Warning > 0)
            {
                line.Append($" W{count.Warning}").Fg(theme.Warning);
            }
            if (count.Error > 0)
            {
                line.Append($" E{count.Error}").Fg(theme.Error);
            }
        }
        if (summary.CodeVersion.Length > 0)
        {
            line.Append("  ·  ").Fg(theme.Muted).Append($"v{summary.CodeVersion}").Fg(theme.Muted);
        }
        return line;
    }

    /// <summary>Every unhealthy node, application and service with the reason the cluster gives; Enter opens it.</summary>
    public IResourceView Unhealthy() =>
        new ResourceView<UnhealthyEntity>("unhealthy", "health", Overview.GetUnhealthyAsync, e => e.Kind + "/" + e.Id)
            {
                Empty = "everything is healthy",
                OnOpen = entity =>
                {
                    host.Open($"opening {entity.Name}", ct => PathToAsync(entity, ct));
                    return null;
                },
            }
            .Column("HEALTH", Constraint.Length(8), e => Health(e.Health), e => e.Health)
            .Column("KIND", Constraint.Length(12), e => Plain(e.Kind), e => e.Kind)
            .Column("NAME", Constraint.Fill(2), e => Plain(e.Name), e => e.Name)
            .Column("REASON", Constraint.Fill(4), e => e.Reason is { Length: > 0 } reason ? Plain(reason) : Muted("see its health"), e => e.Reason);

    /// <summary>The pages below the root that lead to an unhealthy entity: a node's page, an application's services, a service's partitions.</summary>
    internal async Task<IReadOnlyList<IResourceView>> PathToAsync(UnhealthyEntity entity, CancellationToken cancellationToken)
    {
        switch (entity.Kind)
        {
            case "node":
                var node = (await queries.GetNodesAsync(cancellationToken)).FirstOrDefault(n => n.Name == entity.Id)
                    ?? throw new RefusedException($"{entity.Name} isn't in the cluster any more");
                return [DescribeNode(node)];
            case "application":
                var app = await queries.GetApplicationAsync(entity.Id, cancellationToken)
                    ?? throw new RefusedException($"{entity.Name} isn't in the cluster any more");
                return [Services(app)];
            default:
                var appId = entity.ApplicationId ?? ClusterQueries.SystemApplicationId;
                var owner = await queries.GetApplicationAsync(appId, cancellationToken)
                    ?? throw new RefusedException($"{entity.Name}'s application isn't in the cluster any more");
                var service = await queries.GetServiceAsync(appId, entity.Id, cancellationToken)
                    ?? throw new RefusedException($"{entity.Name} isn't in the cluster any more");
                var services = Services(owner);
                services.Select(service.Id);
                return [services, Partitions(service)];
        }
    }

    /// <summary>
    /// The pages that lead to a search hit, from a root page, with each step's row selected: nodes › the node, or
    /// applications › services › partitions › replicas.
    /// </summary>
    public IReadOnlyList<IResourceView> PathTo(SearchHit hit)
    {
        if (hit.Node is { } node)
        {
            var nodes = Nodes();
            nodes.Select(node.Name);
            return [nodes, DescribeNode(node)];
        }

        var path = new List<IResourceView>();
        var apps = Applications();
        path.Add(apps);
        if (hit.Application is { } app)
        {
            apps.Select(app.Id);
            var services = Services(app);
            path.Add(services);
            if (hit.Service is { } service)
            {
                services.Select(service.Id);
                var partitions = Partitions(service);
                path.Add(partitions);
                if (hit.Partition is { } partition)
                {
                    partitions.Select(partition.Id.ToString());
                    path.Add(Replicas(service, partition));
                }
            }
        }
        return path;
    }

    /// <summary>
    /// The search hits for what's typed, as choices that open the hit where it is; a partition id is looked up when
    /// chosen. Nothing while <paramref name="index"/> is still loading (null).
    /// </summary>
    internal IReadOnlyList<Choice> SearchChoices(IReadOnlyList<SearchHit>? index, string query)
    {
        if (Guid.TryParse(query.Trim(), out var partition))
        {
            return [new Choice($"partition {partition}", "find it", () => host.Open($"finding partition {partition}", async ct =>
                await Overview.FindPartitionAsync(partition, ct) is { } hit
                    ? PathTo(hit)
                    : throw new RefusedException($"no partition {partition} in this cluster"), replace: true))];
        }
        return index is null
            ? []
            : ClusterOverview.Search(index, query, 100)
                .Select(hit => SearchChoice(hit, () => host.Open($"opening {hit.Title}", _ => Task.FromResult(PathTo(hit)), replace: true)))
                .ToList();
    }

    /// <summary>A search hit as a choice: its name (a node's with its address), and what it is.</summary>
    public static Choice SearchChoice(SearchHit hit, Action open) =>
        new(
            hit.Node is { } node ? $"{node.Name}  {node.Address}" : hit.Title,
            hit switch
            {
                { Node: { } n } => $"node · {n.NodeType}",
                { Partition: { } p } => $"partition · {p.PartitionKind}",
                { Service: { } s } => $"service · {s.TypeName}",
                { Application: { } a } => $"application · {a.TypeName} {a.TypeVersion}",
                _ => hit.Kind,
            },
            open);

    /// <summary>Goes to the node a replica is on.</summary>
    private void OpenNode(string name) =>
        host.Open($"opening {name}", async ct =>
        {
            var node = (await queries.GetNodesAsync(ct)).FirstOrDefault(n => n.Name == name)
                ?? throw new RefusedException($"no node named {name}");
            return [DescribeNode(node)];
        });
}

/// <summary>
/// A search box over a list of results that follow what's typed: up and down choose, Enter opens, Esc closes. The
/// results come from <see cref="Search"/>, asked again on each change.
/// </summary>
internal sealed class SearchPicker : Widget
{
    private readonly Func<string, IReadOnlyList<Choice>> _search;
    private readonly Input _input = new() { Placeholder = "name, type, address, or a partition id" };
    private readonly List<(int Y, int Index)> _rowHits = [];
    private IReadOnlyList<Choice> _results = [];
    private int _selected;
    private int _scroll;

    public SearchPicker(Func<string, IReadOnlyList<Choice>> search, string initial = "")
    {
        _search = search;
        _input.OnChange = _ => Refresh();
        _input.Text = initial;
        Refresh();
    }

    /// <summary>Whether what's searched is still loading.</summary>
    public Func<bool>? Loading { get; init; }

    /// <summary>Called once a result is opened.</summary>
    public Action? OnClose { get; set; }

    public string Query
    {
        get => _input.Text;
        set
        {
            _input.Text = value;
            Refresh();
        }
    }

    public IReadOnlyList<Choice> Results => _results;

    public Choice? Selected => _selected >= 0 && _selected < _results.Count ? _results[_selected] : null;

    public override bool IsFocusable => true;

    /// <summary>Searches again, e.g. once what's searched has loaded.</summary>
    public void Refresh()
    {
        _results = _input.Text.Trim().Length == 0 ? [] : _search(_input.Text);
        _selected = 0;
        _scroll = 0;
    }

    public override void Render(Surface surface, Rect area)
    {
        if (area.IsEmpty)
        {
            return;
        }

        var theme = Theme.Current;
        var bg = theme.Background;
        new Panel(null, " find · enter opens · esc closes ")
        {
            BorderStyle = BorderStyle.Rounded,
            Background = bg,
            BorderLineStyle = new Style(theme.Border, bg),
        }.Render(surface, area);

        var inner = Border.Inner(area);
        if (inner.IsEmpty)
        {
            return;
        }

        var rows = LayoutSolver.Split(inner, Direction.Vertical, Constraint.Length(1), Constraint.Length(1), Constraint.Fill());
        surface.DrawText(rows[0].X, rows[0].Y, "❯ ", new Style(theme.Accent, bg));
        _input.HasFocus = HasFocus;
        _input.TextStyle = new Style(theme.Foreground, bg);
        _input.PlaceholderStyle = new Style(theme.Muted, bg);
        _input.Render(surface, new Rect(rows[0].X + 2, rows[0].Y, rows[0].Width - 2, 1));
        new Rule { Style = new Style(theme.Border, bg) }.Render(surface, rows[1]);
        RenderList(surface, rows[2], theme, bg);
    }

    private void RenderList(Surface surface, Rect area, Theme theme, Color bg)
    {
        _rowHits.Clear();
        if (area.IsEmpty)
        {
            return;
        }

        if (_results.Count == 0)
        {
            var message = Loading?.Invoke() == true ? "loading nodes, applications and services…"
                : _input.Text.Trim().Length == 0 ? "type to search"
                : "nothing matches";
            TextRenderer.DrawLine(surface, area.X + 1, area.Y, area.Width - 1, new StyledText(message, new Style(theme.Muted, bg)), Justify.Left);
            return;
        }

        if (_selected < _scroll)
        {
            _scroll = _selected;
        }
        else if (_selected >= _scroll + area.Height)
        {
            _scroll = _selected - area.Height + 1;
        }

        var count = Math.Min(area.Height, _results.Count - _scroll);
        for (var i = 0; i < count; i++)
        {
            var index = _scroll + i;
            var choice = _results[index];
            var y = area.Y + i;
            var selected = index == _selected;
            _rowHits.Add((y, index));
            var style = selected ? theme.SelectionStyle : new Style(theme.Foreground, bg);
            surface.FillRect(new Rect(area.X, y, area.Width, 1), style);

            var width = area.Width - 2;
            var titleWidth = width;
            if (choice.Subtitle is { Length: > 0 } subtitle)
            {
                var sub = new StyledText(subtitle, selected ? style : new Style(theme.Muted, bg));
                var subWidth = Math.Min(sub.Width, width / 2);
                TextRenderer.DrawLine(surface, area.X + 1 + width - subWidth, y, subWidth, sub, Justify.Right);
                titleWidth = Math.Max(0, width - subWidth - 2);
            }
            TextRenderer.DrawLine(surface, area.X + 1, y, titleWidth, new StyledText(choice.Title, style), Justify.Left);
        }
    }

    public override bool OnEvent(InputEvent e)
    {
        if (e is MouseEvent mouse)
        {
            foreach (var (y, index) in _rowHits)
            {
                if (y == mouse.Y && mouse.Kind == MouseEventKind.Down)
                {
                    _selected = index;
                    Open();
                    return true;
                }
            }
            return false;
        }

        if (e is not KeyEvent key)
        {
            return false;
        }

        switch (key.Key)
        {
            case Key.Up:
                Move(-1);
                return true;
            case Key.Down:
                Move(1);
                return true;
            case Key.PageUp:
                Move(-10);
                return true;
            case Key.PageDown:
                Move(10);
                return true;
            case Key.Enter:
                Open();
                return true;
        }

        _input.HasFocus = true;
        return _input.OnEvent(key);
    }

    private void Move(int delta)
    {
        if (_results.Count > 0)
        {
            _selected = Math.Clamp(_selected + delta, 0, _results.Count - 1);
        }
    }

    /// <summary>Opens the selected result; nothing happens while nothing is selected.</summary>
    public void Open()
    {
        if (Selected is not { } choice)
        {
            return;
        }
        OnClose?.Invoke();
        choice.Pick();
    }
}
