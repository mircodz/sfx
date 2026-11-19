using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Widgets;

namespace Sfx.Tui;

/// <summary>A tab of a describe page: a title and how to load its lines.</summary>
public sealed record DescribeTab(string Title, Func<CancellationToken, Task<IReadOnlyList<StyledText>>> Load);

/// <summary>
/// One entity's details as tabs of text (overview, health, manifest, raw JSON…). Only the tab being read is loaded,
/// and refreshed; <c>/</c> searches it, <c>n</c>/<c>N</c> step through the matches, <c>y</c> copies it.
/// </summary>
public sealed class DescribeView : IResourceView
{
    private readonly string _kind;
    private readonly string _name;
    private readonly string? _crumb;
    private readonly IReadOnlyList<DescribeTab> _tabs;
    private readonly TextView[] _views;
    private readonly Tabs _tabsWidget = new() { ArrowKeysSwitch = false };
    private readonly Panel _panel;
    private string _filter = "";

    /// <param name="crumb">Breadcrumb text, when the name isn't the right one (the cluster's is its context).</param>
    /// <param name="actions">What can be done to the entity, the same actions its table row offers.</param>
    public DescribeView(
        string kind,
        string name,
        IReadOnlyList<DescribeTab> tabs,
        int activeTab = 0,
        string? crumb = null,
        IEnumerable<ShellAction>? actions = null)
    {
        _kind = kind;
        _name = name;
        _crumb = crumb;
        _tabs = tabs;
        _views = tabs.Select(_ => new TextView()).ToArray();
        for (var i = 0; i < tabs.Count; i++)
        {
            _tabsWidget.Add(tabs[i].Title, _views[i]);
        }
        _tabsWidget.ActiveIndex = activeTab;
        _panel = new Panel(new TabsWatcher(_tabsWidget, OnTabChanged), "") { BorderStyle = BorderStyle.Rounded };

        Actions =
        [
            new("Next match", "Reading", _ => Step(1), [KeyChord.Of('n')]),
            new("Previous match", "Reading", _ => Step(-1), [KeyChord.Of('N')]),
            new("Next tab (also 1-9 and click)", "Reading", _ => SwitchTab(1), [KeyChord.Of(']')]),
            new("Previous tab", "Reading", _ => SwitchTab(-1), [KeyChord.Of('[')]),
            .. actions ?? [],
        ];
        UpdateTitle();
    }

    public string Crumb => _crumb ?? _name;

    public Widget Widget => _panel;

    public Action<IResourceView>? Navigate { get; set; }

    public Action? Reload { get; set; }

    public IReadOnlyList<ShellAction> Actions { get; }

    public string? CopyText => Active.Text;

    /// <summary>Searches the tab being read; switching tabs carries the search along.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Active.Find(value);
            UpdateTitle();
        }
    }

    private TextView Active => _views[_tabsWidget.ActiveIndex];

    public async Task<Action> LoadAsync(CancellationToken cancellationToken)
    {
        var index = _tabsWidget.ActiveIndex;
        IReadOnlyList<StyledText> lines;
        try
        {
            lines = await _tabs[index].Load(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A tab that can't load (no manifest for system services, no config on Azure clusters) says why in place.
            lines = Lines.Error(ex);
        }

        return () =>
        {
            _views[index].SetLines(lines);
            UpdateTitle();
        };
    }

    private void Step(int direction)
    {
        Active.NextMatch(direction);
        UpdateTitle();
    }

    private void SwitchTab(int direction)
    {
        _tabsWidget.ActiveIndex = (_tabsWidget.ActiveIndex + direction + _tabs.Count) % _tabs.Count;
        OnTabChanged();
    }

    private void OnTabChanged()
    {
        if (_filter.Length > 0)
        {
            Active.Find(_filter);
        }
        UpdateTitle();
        Reload?.Invoke();
    }

    private void UpdateTitle()
    {
        var search = _filter.Length == 0 ? ""
            : Active.MatchCount == 0 ? $" /{_filter} (no match)"
            : $" /{_filter} ({Active.CurrentMatch}/{Active.MatchCount})";
        _panel.Title = $" {_kind} {_name}{search} ";
    }

    /// <summary>Passes everything to the tabs, and reports when the active tab changes (by key or click).</summary>
    private sealed class TabsWatcher(Tabs tabs, Action changed) : Widget
    {
        public override bool IsFocusable => true;

        public override bool HasFocus
        {
            get => tabs.HasFocus;
            set => tabs.HasFocus = value;
        }

        public override bool OnEvent(InputEvent e)
        {
            var before = tabs.ActiveIndex;
            var handled = tabs.OnEvent(e);
            if (tabs.ActiveIndex != before)
            {
                changed();
            }
            return handled;
        }

        public override void Render(Surface surface, Rect area) => tabs.Render(surface, area);

        protected override void VisitChildren(Action<Widget> visit) => visit(tabs);
    }
}
