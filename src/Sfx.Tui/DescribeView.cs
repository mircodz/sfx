using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Widgets;

namespace Sfx.Tui;

public sealed record DescribeTab(string Title, Func<CancellationToken, Task<IReadOnlyList<StyledText>>> Load);

/// <summary>
/// One entity's details as tabs of text (overview, health, manifest, raw JSON…). Only the tab being read is loaded,
/// and refreshed; <c>/</c> searches it, <c>n</c>/<c>N</c> step through the matches, <c>y</c> copies it, <c>#</c>
/// numbers its lines and <c>w</c> wraps them.
/// </summary>
public sealed class DescribeView : IResourceView
{
    private const string Reading = "Reading";

    private readonly string _kind;
    private readonly string _name;
    private readonly string? _crumb;
    private readonly IReadOnlyList<DescribeTab> _tabs;
    private readonly TextViewer[] _viewers;
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
        IEnumerable<AppAction>? actions = null)
    {
        _kind = kind;
        _name = name;
        _crumb = crumb;
        _tabs = tabs;
        // The shell's command line does the searching, so / is the same key on every page.
        _viewers = tabs.Select(_ => new TextViewer { SearchPrompt = false }).ToArray();
        for (var i = 0; i < tabs.Count; i++)
        {
            _tabsWidget.Add(tabs[i].Title, _viewers[i]);
        }
        _tabsWidget.ActiveIndex = activeTab;
        _panel = new Panel(new Reader(this), "") { BorderStyle = BorderStyle.Rounded };

        Actions =
        [
            new AppAction("reading.next-match", "Next match", () => Step(1)) { Group = Reading, Keys = ["n"] },
            new AppAction("reading.previous-match", "Previous match", () => Step(-1)) { Group = Reading, Keys = ["N"] },
            new AppAction("reading.next-tab", "Next tab (also 1-9 and click)", () => SwitchTab(1)) { Group = Reading, Keys = ["]"] },
            new AppAction("reading.previous-tab", "Previous tab", () => SwitchTab(-1)) { Group = Reading, Keys = ["["] },
            new AppAction("reading.line-numbers", "Line numbers on or off", () => Active.ShowLineNumbers = !Active.ShowLineNumbers)
            {
                Group = Reading,
                Keys = ["#"],
            },
            new AppAction("reading.wrap", "Wrap long lines, or scroll them sideways", () => Active.Wrap = !Active.Wrap) { Group = Reading, Keys = ["w"] },
            .. actions ?? [],
        ];
        UpdateTitle();
    }

    public string Crumb => _crumb ?? _name;

    public Widget Widget => _panel;

    public Action<IResourceView>? Navigate { get; set; }

    public Action? Reload { get; set; }

    public IReadOnlyList<AppAction> Actions { get; }

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

    public TextViewer Active => _viewers[_tabsWidget.ActiveIndex];

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
            var viewer = _viewers[index];
            var top = viewer.TopLine;
            viewer.SetLines(lines);
            // A refresh keeps the place being read.
            viewer.ScrollToLine(top);
            if (_filter.Length > 0 && viewer.Search != _filter)
            {
                viewer.Find(_filter);
            }
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
        if (Active.Search != _filter)
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
            : $" /{_filter} ({Math.Max(1, Active.CurrentMatch)}/{Active.MatchCount})";
        _panel.Title = $" {_kind} {_name}{search} ";
    }

    /// <summary>
    /// The tabs, minus the viewer keys sfx gives another meaning on every page (<c>h</c> back, <c>l</c> open,
    /// <c>y</c> copy, <c>esc</c> clear the search, then back); notices when the tab or the match changes.
    /// </summary>
    private sealed class Reader(DescribeView page) : Widget
    {
        private Tabs Tabs => page._tabsWidget;

        public override bool IsFocusable => true;

        public override bool HasFocus
        {
            get => Tabs.HasFocus;
            set => Tabs.HasFocus = value;
        }

        public override bool OnEvent(InputEvent e)
        {
            if (e is KeyEvent { Modifiers: KeyModifiers.None } key
                && (key.Key == Key.Escape || key.IsChar && key.Rune.Value is 'h' or 'l' or 'y'))
            {
                return false;
            }

            var before = Tabs.ActiveIndex;
            var handled = Tabs.OnEvent(e);
            if (Tabs.ActiveIndex != before)
            {
                page.OnTabChanged();
            }
            else if (handled)
            {
                page.UpdateTitle();
            }
            return handled;
        }

        public override void Render(Surface surface, Rect area) => Tabs.Render(surface, area);

        protected override void VisitChildren(Action<Widget> visit) => visit(Tabs);
    }
}
