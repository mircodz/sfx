using Cellar.Layout;
using Cellar.Text;
using Cellar.Widgets;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>One page of resources: a table that loads its rows and can open a row as another page.</summary>
public interface IResourceView
{
    /// <summary>This page's step in the breadcrumb, e.g. the application it lists services of.</summary>
    string Crumb { get; }

    Widget Widget { get; }

    /// <summary>What <c>/</c> typed: rows that contain it, or the text to find in what's being read.</summary>
    string Filter { get; set; }

    /// <summary>Called with the page to show when a row is opened (Enter, or a click on the selected row).</summary>
    Action<IResourceView>? Navigate { get; set; }

    /// <summary>Called when the page needs fresh data outside the refresh cycle, e.g. a tab was switched to.</summary>
    Action? Reload { get; set; }

    /// <summary>What <c>y</c> copies: the selected row's name, or the text being read.</summary>
    string? CopyText { get; }

    /// <summary>What can be done on this page, on top of the global actions; each says when it's available.</summary>
    IReadOnlyList<AppAction> Actions { get; }

    /// <summary>The column headers rows can be grouped by; none on pages that aren't tables.</summary>
    IReadOnlyList<string> Columns => [];

    /// <summary>Groups the rows by a column (null: ungroups); false if there's no such column.</summary>
    bool GroupBy(string? column) => false;

    /// <summary>How many rows are marked for an action on several at once.</summary>
    int MarkCount => 0;

    void ClearMarks()
    {
    }

    /// <summary>Fetches rows off the UI thread; the returned action shows them and must run on the UI thread.</summary>
    Task<Action> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A table of resources. A refresh keeps the cursor and marks on the same rows (by key); <c>/</c> filters rows by
/// any column's text; <c>:group</c> groups them by a column; <c>space</c> marks rows so the row actions run on all
/// of them after one confirmation.
/// </summary>
public sealed class ResourceView<T> : IResourceView
    where T : class
{
    public const string RowGroup = "Selected row";

    private readonly string _kind;
    private readonly Func<CancellationToken, Task<IReadOnlyList<T>>> _load;
    private readonly Panel _panel;
    private readonly List<AppAction> _actions = [];
    private readonly List<EntityAction<T>> _rowActions = [];
    private IReadOnlyList<AppAction>? _all;
    private IActionHost? _host;
    private Func<T, IResourceView>? _describe;
    private string _filter = "";

    public ResourceView(string kind, string crumb, Func<CancellationToken, Task<IReadOnlyList<T>>> load, Func<T, string> key)
    {
        _kind = kind;
        _load = load;
        Crumb = crumb;
        Table = new Table<T>
        {
            ShowHeader = true,
            Striped = true,
            ShowScrollbar = true,
            Sortable = true,
            RowKey = row => key(row),
            EmptyText = Styles.Muted("loading…"),
            OnActivate = row =>
            {
                if ((OnOpen?.Invoke(row) ?? _describe?.Invoke(row)) is { } next)
                {
                    Navigate?.Invoke(next);
                }
            },
            OnMarksChanged = UpdateTitle,
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

    /// <summary>The page's actions: <c>d</c> describe, then the row actions, which act on the marked rows when
    /// some are marked and on the selected one otherwise.</summary>
    public IReadOnlyList<AppAction> Actions => _all ??= [.. _actions, .. _rowActions.Select(BindRow)];

    /// <summary>The row under the cursor, if any (not a group header).</summary>
    public T? Selected => Table.Selected;

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Table.Filter = value.Length == 0 ? null : row => Table.ContainsText(row, value);
            UpdateTitle();
        }
    }

    public IReadOnlyList<string> Columns => Table.Columns.Select(column => column.Header.ToLowerInvariant()).ToList();

    public bool GroupBy(string? column)
    {
        if (column is null)
        {
            Table.Ungroup();
        }
        else if (!Table.GroupBy(column))
        {
            return false;
        }
        UpdateTitle();
        return true;
    }

    public int MarkCount => Table.Marked.Count;

    public void ClearMarks() => Table.ClearMarks();

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

    /// <summary>Actions on the selected row (or the marked ones); those that don't apply to it are left out.</summary>
    public ResourceView<T> Acting(IEnumerable<EntityAction<T>> actions, IActionHost host)
    {
        _rowActions.AddRange(actions);
        _host = host;
        _all = null;
        Table.Markable = _rowActions.Count > 0;
        return this;
    }

    /// <summary>Sorts by another column than the first, e.g. to put what needs attention on top.</summary>
    public ResourceView<T> Sorting(string header, SortState state = SortState.Ascending)
    {
        var index = Table.Columns.FindIndex(column => column.Header == header);
        Table.SortBy(index >= 0 ? index : throw new ArgumentException($"No column '{header}'.", nameof(header)), state);
        return this;
    }

    /// <summary>Groups rows by a column from the start, with <paramref name="summary"/> after each group's count.</summary>
    public ResourceView<T> Grouping(string header, Func<TableGroup<T>, StyledText>? summary = null)
    {
        if (!Table.GroupBy(header))
        {
            throw new ArgumentException($"No column '{header}'.", nameof(header));
        }
        Table.GroupSummary = summary;
        return this;
    }

    /// <summary>Draws new rows and changed cells in bold until the next refresh; for tables without a ticking column.</summary>
    public ResourceView<T> HighlightingChanges()
    {
        Table.HighlightChanges = true;
        return this;
    }

    /// <summary>What the table says when it has no rows, once loaded.</summary>
    public string Empty { get; init; } = "nothing here";

    /// <summary>Lets <c>d</c> describe the selected row; Enter does too when the row has nothing to drill into.</summary>
    public ResourceView<T> Describing(Func<T, IResourceView> describe)
    {
        _describe = describe;
        _actions.Add(new AppAction("row.describe", "Describe", () =>
        {
            if (Selected is { } row)
            {
                Navigate?.Invoke(describe(row));
            }
        })
        {
            Group = RowGroup,
            Keys = ["d"],
            Commands = ["describe"],
            When = () => Selected is not null,
        });
        _all = null;
        return this;
    }

    public async Task<Action> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await _load(cancellationToken);
        return () =>
        {
            Table.EmptyText = Styles.Muted(Empty);
            Table.SetRows(rows);
            UpdateTitle();
        };
    }

    /// <summary>A row action on the selected row, or on every marked row it applies to.</summary>
    private AppAction BindRow(EntityAction<T> action) =>
        new(action.Id, action.Title, () => RunRow(action))
        {
            Group = RowGroup,
            Keys = action.Keys ?? [],
            Commands = action.Commands ?? [],
            When = () => (_host?.Allows(action.Risk, action.Power) ?? true)
                && (Table.Marked.Count > 0 ? Table.Marked.Any(action.Applies) : Selected is { } row && action.Applies(row)),
        };

    private void RunRow(EntityAction<T> action)
    {
        var marked = Table.Marked;
        if (marked.Count == 0)
        {
            if (Selected is { } row && action.Applies(row))
            {
                action.Run(row);
            }
            return;
        }

        var rows = marked.Where(action.Applies).ToList();
        var skipped = marked.Count - rows.Count;
        _host?.Batch(
            $"{action.Title}: {rows.Count} marked {(rows.Count == 1 ? "row" : "rows")}",
            () => rows.ForEach(action.Run),
            skipped > 0 ? $"{skipped} marked {(skipped == 1 ? "row is" : "rows are")} left out: it doesn't apply to {(skipped == 1 ? "it" : "them")}." : null,
            Table.ClearMarks);
    }

    private void UpdateTitle()
    {
        var shown = Table.Filter is null ? Table.Rows.Count : Table.FilteredRows.Count;
        var marked = Table.Marked.Count;
        _panel.Title = $" {_kind} [{shown}]"
            + (Table.GroupColumn >= 0 && Table.GroupColumn < Table.Columns.Count ? $" · by {Table.Columns[Table.GroupColumn].Header.ToLowerInvariant()}" : "")
            + (marked > 0 ? $" · {marked} marked" : "")
            + (_filter.Length > 0 ? $" /{_filter}" : "")
            + " ";
    }
}
