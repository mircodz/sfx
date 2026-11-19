using Cellar.Layout;
using Cellar.Text;
using Cellar.Widgets;

namespace Sfx.Tui;

/// <summary>One page of resources: a table that loads its rows and can open a row as another page.</summary>
public interface IResourceView
{
    /// <summary>This page's step in the breadcrumb, e.g. the application it lists services of.</summary>
    string Crumb { get; }

    Widget Widget { get; }

    string Filter { get; set; }

    /// <summary>Called with the page to show when a row is opened (Enter, or a click on the selected row).</summary>
    Action<IResourceView>? Navigate { get; set; }

    /// <summary>Called when the page needs fresh data outside the refresh cycle, e.g. a tab was switched to.</summary>
    Action? Reload { get; set; }

    /// <summary>What <c>y</c> copies: the selected row's name, or the text being read.</summary>
    string? CopyText { get; }

    /// <summary>What can be done on this page, on top of the global actions.</summary>
    IReadOnlyList<ShellAction> Actions { get; }

    /// <summary>Fetches rows off the UI thread; the returned action shows them and must run on the UI thread.</summary>
    Task<Action> LoadAsync(CancellationToken cancellationToken);
}

public sealed class ResourceView<T> : IResourceView
    where T : class
{
    private readonly string _kind;
    private readonly Func<CancellationToken, Task<IReadOnlyList<T>>> _load;
    private readonly Func<T, string> _key;
    private readonly Panel _panel;
    private Func<T, IResourceView>? _describe;
    private IReadOnlyList<T> _all = [];
    private string _filter = "";

    public ResourceView(string kind, string crumb, Func<CancellationToken, Task<IReadOnlyList<T>>> load, Func<T, string> key)
    {
        _kind = kind;
        _load = load;
        _key = key;
        Crumb = crumb;
        Table = new Table<T>
        {
            ShowHeader = true,
            Striped = true,
            ShowScrollbar = true,
            Sortable = true,
            OnActivate = row =>
            {
                if ((OnOpen?.Invoke(row) ?? _describe?.Invoke(row)) is { } next)
                {
                    Navigate?.Invoke(next);
                }
            },
        };
        _panel = new Panel(Table, "") { BorderStyle = BorderStyle.Rounded };
        UpdateTitle();
    }

    public Table<T> Table { get; }

    /// <summary>The page for a row, or null when opening the row doesn't navigate.</summary>
    public Func<T, IResourceView?>? OnOpen { get; init; }

    public string Crumb { get; }

    public Widget Widget => _panel;

    public Action<IResourceView>? Navigate { get; set; }

    public Action? Reload { get; set; }

    public string? CopyText => Selected is { } row && Table.Columns.Count > 0 ? Table.Columns[0].Render(row).PlainText : null;

    public List<ShellAction> Actions { get; } = [];

    /// <summary>Actions on the selected row; those that don't apply to it are left out.</summary>
    public List<EntityAction<T>> RowActions { get; } = [];

    IReadOnlyList<ShellAction> IResourceView.Actions =>
        Selected is { } row
            ? RowActions.Where(a => a.Applies(row)).Select(a => a.Bind(row, "Selected row")).Concat(Actions).ToList()
            : Actions;

    /// <summary>The row under the cursor, if any.</summary>
    public T? Selected =>
        Table.SelectedIndex >= 0 && Table.SelectedIndex < Table.Rows.Count ? Table.Rows[Table.SelectedIndex] : null;

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            ShowRows();
        }
    }

    public ResourceView<T> Column(
        string header,
        Constraint width,
        Func<T, StyledText> render,
        Func<T, IComparable?>? sortKey = null,
        Justify justify = Justify.Left)
    {
        Table.Columns.Add(new TableColumn<T>(header, width, render, justify) { SortKey = sortKey! });
        if (Table.Columns.Count == 1)
        {
            Table.SortBy(0, SortState.Ascending);
        }
        return this;
    }

    public ResourceView<T> Acting(IEnumerable<EntityAction<T>> actions)
    {
        RowActions.AddRange(actions);
        return this;
    }

    /// <summary>Lets <c>d</c> describe the selected row; Enter does too when the row has nothing to drill into.</summary>
    public ResourceView<T> Describing(Func<T, IResourceView> describe)
    {
        _describe = describe;
        Actions.Add(new ShellAction("Describe", "Selected row", _ =>
        {
            if (Selected is { } row)
            {
                Navigate?.Invoke(describe(row));
            }
        }, [KeyChord.Of('d')], ["describe"]));
        return this;
    }

    public async Task<Action> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await _load(cancellationToken);
        return () =>
        {
            _all = rows;
            ShowRows();
        };
    }

    private void ShowRows()
    {
        var selectedKey = Selected is { } selected ? _key(selected) : null;
        var scroll = Table.ScrollOffset;

        Table.SetRows(_filter.Length == 0 ? _all : _all.Where(Matches));

        // SetRows jumps to the first row; keep the cursor on the same resource across refreshes.
        var index = selectedKey is null ? -1 : Table.Rows.FindIndex(row => _key(row) == selectedKey);
        if (index >= 0)
        {
            Table.ScrollOffset = scroll;
            Table.Select(index);
        }
        UpdateTitle();
    }

    private void UpdateTitle() =>
        _panel.Title = $" {_kind} [{Table.Rows.Count}]{(_filter.Length > 0 ? $" /{_filter}" : "")} ";

    private bool Matches(T row) =>
        Table.Columns.Any(column => column.Render(row).PlainText.Contains(_filter, StringComparison.OrdinalIgnoreCase));
}
