using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sfx.Core;
using Prompt = Cellar.Widgets.CommandLine;

namespace Sfx.Tui;

/// <summary>
/// The screen: the cluster's pages (a stack under a clickable breadcrumb) or the contexts page, and a footer that is
/// the status line or the <c>:</c> and <c>/</c> prompt, with the activity strip on its right. Keys, <c>:</c>
/// commands, the palette, help and the footer's shortcuts all come from one <see cref="Keymap"/>.
/// </summary>
internal sealed class Shell : IActionHost
{
    private const string General = "General";
    private const string Navigation = "Navigation";
    private const string Movement = "Movement";
    private const string PagesGroup = "Pages";
    private const string Rows = "Rows";
    private const string ClusterPage = "cluster";
    private const string ContextsPage = "contexts";

    private static readonly TimeSpan TrackEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SummaryEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SearchIndexFor = TimeSpan.FromSeconds(30);

    private readonly App _app;
    private readonly ClusterConnector _connector;
    private readonly ContextStore _store;
    private readonly Navigator _navigator = new() { BackKey = Key.Backspace };
    private readonly Switcher _main = new();
    private readonly ResourceView<ResolvedContext> _contexts;
    private readonly Dictionary<Widget, IResourceView> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly Keymap _keymap = new();
    private readonly Prompt _line = new();
    private readonly CommandMode _commands;
    private readonly CommandMode _filter;
    private readonly StatusItem _contextItem;
    private readonly SpinnerStatusItem _loadingItem;
    private readonly StatusItem _updatedItem;
    private readonly ActionLog _log;
    private readonly SessionActivity _activity;
    private readonly Label _summary = new(StyledText.Empty());
    private readonly bool _readOnly;

    private ClusterSession _session;
    private Views _views;
    private bool _power;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _connecting;
    private DateTime? _loadedAt;
    private TimeSpan _refreshInterval;
    private CancellationTokenSource? _autoRefresh;
    private StyledText? _loadError;
    private List<Operation>? _batch;
    private bool _batchRefused;
    private DateTime _summaryAt;
    private bool _summaryLoading;
    private (DateTime At, string Text)? _lastError;
    private (ClusterSession Session, DateTime At, Task<IReadOnlyList<SearchHit>> Index)? _searchIndex;

    /// <param name="readOnly"><c>--readonly</c>: every context connected to in this run is read-only.</param>
    public Shell(App app, ClusterSession session, ClusterConnector connector, ContextStore store, TimeSpan refreshInterval, bool readOnly = false)
    {
        _app = app;
        _readOnly = readOnly;
        _session = session;
        _power = session.Context.Power;
        _views = new Views(session.Queries, this);
        _connector = connector;
        _store = store;
        _log = new ActionLog(SfxHost.ActionLogPath(store));
        _activity = new SessionActivity(_log);
        _contexts = Views.Contexts(store, () => _session.Context.Name, Connect, Resolve);
        _contexts.Reload = Refresh;

        _keymap.Global.Add(GlobalActions());
        _keymap.AddScope("page", () => Current?.Actions ?? [], KeymapScope.PagePrecedence);

        var history = new CommandHistory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, "history"));
        _commands = new CommandMode(":")
        {
            Placeholder = "command, e.g. nodes, ctx prod, events 6h (tab completes)",
            History = history.For(':'),
            Completion = new KeymapCompletionSource(_keymap) { Arguments = Arguments },
            OnSubmit = RunCommand,
        };
        _filter = new CommandMode("/")
        {
            History = history.For('/'),
            OnChange = text =>
            {
                if (Current is { } page)
                {
                    page.Filter = text;
                }
            },
            OnSubmit = text =>
            {
                if (Current is { } page)
                {
                    page.Filter = text;
                }
            },
            OnCancel = () =>
            {
                if (Current is { } page)
                {
                    page.Filter = "";
                }
            },
        };

        var status = _line.Status;
        status.Keymap = _keymap;
        status.MessageDuration = TimeSpan.FromSeconds(8);
        status.ErrorHint = Styles.Muted("esc dismisses");
        _contextItem = status.Add(StyledText.Empty(), StatusAlignment.Left, priority: 100);
        _loadingItem = (SpinnerStatusItem)status.Add(new SpinnerStatusItem("loading…", StatusAlignment.Left, priority: 50));
        _updatedItem = status.Add(StyledText.Empty(), StatusAlignment.Left, priority: 40);

        _main.Add(ClusterPage, _navigator);
        _main.Add(ContextsPage, _contexts.Widget);
        _main.Show(ClusterPage);
        // The switcher only passes focus to its page once it has focus itself; a table ignores keys without it.
        _main.HasFocus = true;

        app.Root = new Stack(Direction.Vertical)
            .Add(_summary, Constraint.Length(1))
            .Add(_main, Constraint.Fill())
            .Add(new Footer(_line, new ActivityStrip(_activity.Tasks), _activity.Tasks), Constraint.Length(1));

        app.OnEvent = _line.Intercept;
        // An open overlay (help, palette, a dialog) keeps its keys to itself.
        app.OnUnhandledEvent = e => _app.TopOverlay is null && _keymap.Handle(e);
        app.OnMessage = message =>
        {
            if (message is Action action)
            {
                action();
                app.Invalidate();
            }
        };
        _navigator.OnNavigate = _ => OnNavigated();

        ShowRoot(_views.Nodes());
        SetRefreshInterval(refreshInterval);
    }

    private bool OnContexts => _main.CurrentName == ContextsPage;

    /// <summary>The contexts page's table, for tests.</summary>
    internal Table<ResolvedContext> ContextsTable => _contexts.Table;

    private IResourceView? Current =>
        OnContexts ? _contexts
        : _navigator.Current is { } page && _pages.TryGetValue(page.Content, out var view) ? view
        : null;

    private StatusLine Status => _line.Status;

    /// <summary>Every key and command, the page's included.</summary>
    internal Keymap Keymap => _keymap;

    public Task NotifyAsync(string message)
    {
        Post(() => Warn(message));
        return Task.CompletedTask;
    }

    /// <summary>Every key and command that works on any page. Pages add their own, which win on the same key.</summary>
    private IReadOnlyList<AppAction> GlobalActions() =>
    [
        new("command", "Command", () => _line.Open(_commands)) { Keys = [":"], Footer = "cmd" },
        new("filter", "Filter rows, or search the text being read", OpenFilter) { Keys = ["/"], Footer = "filter" },
        new("palette", "Command palette", OpenPalette) { Keys = ["ctrl+p"], Commands = ["palette"], Footer = "palette" },
        new("help", "Keys", () => _keymap.ShowHelp(_app)) { Keys = ["?"], Commands = ["help"], Footer = "help" },
        new("refresh", "Refresh now; :refresh 2s, 1m or off sets how often it refreshes on its own", ChangeRefresh)
        {
            Keys = ["r"],
            Commands = ["refresh"],
            ArgumentHint = "[interval|off]",
        },
        new("copy", "Copy the selected name, or the text being read", Copy) { Keys = ["y"], Commands = ["copy"] },
        new("find", "Find a node, application or service by name, type or address; a partition by its id", Find)
        {
            Keys = ["ctrl+f"],
            Commands = ["find", "f"],
            ArgumentHint = "[text|partition id]",
            Footer = "find",
        },
        new("last-error", "Show the last error in full", ShowLastError) { Keys = ["!"], Commands = ["error", "errors"] },
        new("quit", "Quit", _app.Quit) { Keys = ["q", "ctrl+c"], Commands = ["quit", "q"], Footer = "quit" },

        new("back", "Back", Back) { Group = Navigation, Keys = ["backspace", "h"], Commands = ["back"], Footer = "back" },
        new("escape", "Dismiss the error or warning, clear the marks or the filter, or go back", Escape) { Group = Navigation, Keys = ["esc"] },

        new("go.nodes", "Nodes", () => ShowRoot(_views.Nodes())) { Group = PagesGroup, Commands = ["nodes", "node", "no"] },
        new("go.applications", "Applications", () => ShowRoot(_views.Applications())) { Group = PagesGroup, Commands = ["apps", "app", "applications"] },
        new("go.cluster", "Cluster", () => ShowRoot(_views.DescribeCluster(_session.Context.Name))) { Group = PagesGroup, Commands = ["cluster"] },
        new("go.health", "Everything unhealthy, and why", () => ShowRoot(_views.Unhealthy())) { Group = PagesGroup, Commands = ["health", "unhealthy"] },
        new(
            "go.cluster-health",
            "Cluster health: evaluations and events",
            () => ShowRoot(_views.DescribeCluster(_session.Context.Name, Views.ClusterHealthTab)))
        {
            Group = PagesGroup,
            Commands = ["cluster-health"],
        },
        new("go.types", "Application types: provisioned versions and their service types", () => ShowRoot(_views.ApplicationTypes()))
        {
            Group = PagesGroup,
            Commands = ["types", "apptypes"],
        },
        new("go.store", "Image store: packages, folders and files", () => ShowRoot(_views.ImageStoreRoot()))
        {
            Group = PagesGroup,
            Commands = ["store", "imagestore"],
        },
        new("provision", "Upload a local application package (folder, .sfpkg or .zip) and provision it", Provision)
        {
            Commands = ["provision"],
            ArgumentHint = "<path>",
            When = () => Allows(ActionRisk.Confirm, power: false),
        },
        new("go.upgrades", "Upgrades of the cluster and applications", () => ShowRoot(_views.Upgrades(_session.Context.Name)))
        {
            Group = PagesGroup,
            Commands = ["upgrades", "updates", "up"],
        },
        new("go.activity", "Activity: what sfx changed, this session and before", () => ShowRoot(Views.Activity(_activity)))
        {
            Group = PagesGroup,
            Commands = ["activity", "act"],
        },
        new("go.events", "Events of the last hour; :events 6h for longer", ShowEvents)
        {
            Group = PagesGroup,
            Commands = ["events", "ev"],
            ArgumentHint = "[window]",
        },
        new("go.repairs", "Repair tasks: reboots, patching and other infrastructure work", () => ShowRoot(_views.Repairs()))
        {
            Group = PagesGroup,
            Commands = ["repairs", "repair", "rt"],
        },
        new("go.contexts", "Contexts; :ctx <name> switches", SwitchContext)
        {
            Group = PagesGroup,
            Keys = ["c"],
            Commands = ["ctx", "context", "contexts"],
            ArgumentHint = "[name]",
        },
        new("power", "Unlock or lock power actions (recover, delete, remove node state…)", TogglePower) { Commands = ["power"] },

        new("group", "Group rows by a column, e.g. :group type; :group alone ungroups", Group)
        {
            Group = Rows,
            Commands = ["group"],
            ArgumentHint = "[column]",
            When = () => Current?.Columns.Count > 0,
        },
        new("mark", "Mark the row, so row actions run on every marked row", () => Forward(Key.Char, ' '))
        {
            Group = Rows,
            Keys = ["space"],
            ShowInPalette = false,
            When = () => Current is { Actions.Count: > 0, Columns.Count: > 0 },
        },
        new("mark-all", "Mark every row shown, or unmark them all", () => Forward(Key.Char, '*'))
        {
            Group = Rows,
            Keys = ["*"],
            ShowInPalette = false,
            When = () => Current is { Actions.Count: > 0, Columns.Count: > 0 },
        },

        new("open", "Open row (or click it twice); on a group, fold or unfold it", () => Forward(Key.Enter))
        {
            Group = Movement,
            Keys = ["enter", "l"],
            ShowInPalette = false,
        },
        new("down", "Down", () => Forward(Key.Down)) { Group = Movement, Keys = ["j"], ShowInPalette = false },
        new("up", "Up", () => Forward(Key.Up)) { Group = Movement, Keys = ["k"], ShowInPalette = false },
        new("first", "First row", () => Forward(Key.Home)) { Group = Movement, Keys = ["g"], ShowInPalette = false },
        new("last", "Last row", () => Forward(Key.End)) { Group = Movement, Keys = ["G"], ShowInPalette = false },
    ];

    internal IEnumerable<string> Arguments(AppAction action, string typed) => action.Id switch
    {
        "go.contexts" => _store.Contexts.Select(context => context.Name),
        "go.events" => ["30m", "1h", "6h", "24h", "48h"],
        "refresh" => ["2s", "5s", "10s", "30s", "1m", "off"],
        "group" => Current is { } page ? page.Columns : [],
        "provision" or Views.UploadActionId => PathCompletion.Complete(typed),
        _ => [],
    };

    private void RunCommand(string text)
    {
        text = text.Trim();
        if (text.Length > 0 && !_keymap.RunCommand(text))
        {
            Warn($"unknown command '{text.TrimStart(':')}' (ctrl+p lists everything)");
        }
    }

    private void OpenFilter()
    {
        _filter.Placeholder = Current is DescribeView ? "text to find in this tab" : "text in any column";
        _line.Open(_filter, Current?.Filter ?? "");
    }

    private bool Forward(Key key, char rune = '\0') =>
        Current?.Widget.OnEvent(new KeyEvent(key, new System.Text.Rune(rune), KeyModifiers.None)) ?? false;

    private void Back()
    {
        if (OnContexts)
        {
            ShowCluster();
        }
        else
        {
            _navigator.Pop();
        }
    }

    private void Escape()
    {
        if (Status.Message is { Level: StatusLevel.Error or StatusLevel.Warning })
        {
            Status.Dismiss();
        }
        else if (Current is { MarkCount: > 0 } marked)
        {
            marked.ClearMarks();
        }
        else if (Current is { Filter.Length: > 0 } filtered)
        {
            filtered.Filter = "";
        }
        else
        {
            Back();
        }
    }

    private void Group(string? column)
    {
        if (Current is not { Columns.Count: > 0 } page)
        {
            return;
        }
        if (!page.GroupBy(column))
        {
            Warn($"no column '{column}'; columns: {string.Join(", ", page.Columns)}");
        }
    }

    private void OpenPalette() =>
        _keymap.ShowPalette(
            _app,
            _store.Contexts.Select(context => new Command($"Switch to context {context.Name}", () => Connect(context), context.Endpoint)));

    private void SwitchContext(string? name)
    {
        if (name is null)
        {
            ShowContexts();
        }
        else if (_store.Find(name) is { } context)
        {
            Connect(context);
        }
        else
        {
            Warn($"no context named '{name}'");
        }
    }

    private void TogglePower()
    {
        if (_session.Context.ReadOnly)
        {
            Warn($"{_session.Context.Name} is read-only");
            return;
        }

        _power = !_power;
        Info(_power ? "power actions unlocked; :power locks them again" : "power actions locked");
        UpdateStatus();
    }

    public bool Allows(ActionRisk risk, bool power) =>
        (risk == ActionRisk.None || !_session.Context.ReadOnly) && (!power || _power);

    public void Run(Operation operation)
    {
        if (_batch is not null)
        {
            _batch.Add(operation);
            return;
        }

        if (_session.Context.ReadOnly)
        {
            Warn($"{_session.Context.Name} is read-only");
            return;
        }

        Confirm(operation, back: null);
    }

    /// <summary>Asks before anything that changes the cluster; <paramref name="back"/> runs when the answer is no.</summary>
    private void Confirm(Operation operation, Action? back)
    {
        if (operation.Risk == ActionRisk.None)
        {
            Execute(operation, new HashSet<char>());
            return;
        }

        ConfirmDialog.Show(_app, Confirmations.For(operation), result =>
        {
            if (result.Confirmed)
            {
                Execute(operation, Confirmations.Chosen(result));
            }
            else
            {
                back?.Invoke();
            }
        }, DialogWidth(60));
    }

    private int DialogWidth(int percent) => Math.Max(44, _app.Size.Width * percent / 100);

    public void Batch(string title, Action queue, string? note = null, Action? started = null)
    {
        if (_session.Context.ReadOnly)
        {
            Warn($"{_session.Context.Name} is read-only");
            return;
        }

        List<Operation> operations = [];
        _batch = operations;
        _batchRefused = false;
        try
        {
            queue();
        }
        finally
        {
            _batch = null;
        }

        if (_batchRefused || operations.Count == 0)
        {
            Warn("that action works on one row at a time; esc clears the marks");
            return;
        }

        ConfirmDialog.Show(_app, Confirmations.ForBatch(title, operations, note), result =>
        {
            if (result.Confirmed)
            {
                started?.Invoke();
                ExecuteAll(title, operations, Confirmations.Chosen(result));
            }
        }, DialogWidth(60));
    }

    /// <summary>
    /// Runs the operation off the UI thread, as a task in the activity strip; the outcome goes to the action log.
    /// Once the cluster has accepted it, a change it carries out over time is followed until it's done.
    /// </summary>
    private void Execute(Operation operation, IReadOnlySet<char> options)
    {
        var session = _session;
        var title = Titled(operation, options);
        var task = _activity.Start(session.Context.Name, title);
        operation.Progress?.Attach(state => task.Report(state.Fraction, state.Detail));
        _ = Task.Run(async () =>
        {
            var requested = DateTimeOffset.UtcNow;
            try
            {
                await operation.Run(CancellationToken.None, options);
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                Log(session, title, "failed: " + message, task);
                task.Fail(message);
                Post(() => Error($"{title}: {message}"));
                return;
            }

            Log(session, title, "ok", task);
            Post(() => RefreshIf(session));
            if (operation.Track is null)
            {
                task.Succeed();
                return;
            }

            task.Report(null, "requested");
            await TrackAsync(operation, task, requested, session);
        });
    }

    /// <summary>Asks the cluster where the change has got to every few seconds, until it's done or the time is up.</summary>
    private async Task TrackAsync(Operation operation, ActivityTask task, DateTimeOffset requested, ClusterSession session)
    {
        var detail = "requested";
        while (DateTimeOffset.UtcNow - requested < operation.TrackFor)
        {
            await Task.Delay(TrackEvery);
            OperationState state;
            try
            {
                state = await operation.Track!(DateTimeOffset.UtcNow - requested, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The cluster may not answer while a node restarts; keep asking.
                task.Report(null, detail = "can't tell yet: " + ex.GetBaseException().Message);
                continue;
            }

            detail = state.Detail;
            task.Report(state.Fraction, detail);
            if (state.Done)
            {
                if (state.Failed)
                {
                    task.Fail(detail);
                }
                else
                {
                    task.Succeed(detail);
                }
                Post(() => RefreshIf(session));
                return;
            }
        }

        task.Cancel($"stopped following it after {RefreshInterval.Format(operation.TrackFor)}: {detail}");
    }

    /// <summary>Runs operations one after another as one task, and stops at the first that fails.</summary>
    private void ExecuteAll(string title, IReadOnlyList<Operation> operations, IReadOnlySet<char> options)
    {
        var session = _session;
        var task = _activity.Start(session.Context.Name, title);
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var each = Titled(operation, options);
                task.Report((double)i / operations.Count, $"{i} of {operations.Count} done; {each}");
                try
                {
                    await operation.Run(CancellationToken.None, options);
                    Log(session, each, "ok", null);
                }
                catch (Exception ex)
                {
                    var message = ex.GetBaseException().Message;
                    Log(session, each, "failed: " + message, null);
                    task.Fail($"{each}: {message} ({i} of {operations.Count} done)");
                    Post(() =>
                    {
                        Error($"{each}: {message}");
                        RefreshIf(session);
                    });
                    return;
                }
            }

            task.Succeed($"{operations.Count} of {operations.Count} done");
            Post(() => RefreshIf(session));
        });
    }

    private static string Titled(Operation operation, IReadOnlySet<char> options) =>
        operation.Title + string.Concat(operation.Options?.Where(o => options.Contains(o.Key)).Select(o => $" ({o.Label})") ?? []);

    private void Log(ClusterSession session, string title, string outcome, ActivityTask? task)
    {
        try
        {
            var entry = _log.Append(session.Context.Name, session.Endpoint.ToString(), title, outcome);
            if (task is not null)
            {
                _activity.Logged(task, entry);
            }
        }
        catch (IOException)
        {
        }
    }

    public void Pick(string title, Func<CancellationToken, Task<IReadOnlyList<Choice>>> load)
    {
        if (_batch is not null)
        {
            _batchRefused = true;
            return;
        }

        // The question stays in the footer while the list loads and while it's open.
        var question = StyledText.Of(title);
        Status.Show(question, StatusLevel.Info, Timeout.InfiniteTimeSpan);
        void Done()
        {
            if (ReferenceEquals(Status.Message?.Text, question))
            {
                Status.Dismiss();
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var choices = await load(CancellationToken.None);
                Post(() =>
                {
                    if (_app.TopOverlay is not null)
                    {
                        Done();
                        return;
                    }
                    ShowPick(choices, Done);
                });
            }
            catch (Exception ex)
            {
                Post(() => Error(ex.GetBaseException().Message));
            }
        });
    }

    private void ShowPick(IReadOnlyList<Choice> choices, Action done)
    {
        var palette = new CommandPalette(choices.Select(choice => new Command(choice.Title, choice.Pick, choice.Subtitle ?? "")));
        var overlay = new Overlay(palette)
        {
            Placement = OverlayPlacement.Top,
            WidthPercent = 60,
            Height = Math.Min(choices.Count + 4, Math.Max(8, _app.Size.Height - 4)),
            Margin = 3,
            Modal = true,
            ScrimOpacity = 0.4,
        };
        // Runs after the choice, which may have opened a confirmation; only this list is closed.
        void Close()
        {
            _app.RemoveOverlay(overlay);
            done();
        }
        palette.OnAfterRun = Close;
        palette.OnCancel = Close;
        _app.PushOverlay(overlay);
    }

    public void Ask(string label, string initial, Func<string, string?>? validate, Action<string> submit)
    {
        if (_batch is not null)
        {
            _batchRefused = true;
            return;
        }

        _line.Ask(label, initial, validate, text => submit(text.Trim()));
    }

    public void Edit(
        string title,
        Func<CancellationToken, Task<IReadOnlyList<FormField>>> load,
        Func<IReadOnlyList<FormField>, Operation> review,
        bool unchanged = false)
    {
        if (_batch is not null)
        {
            _batchRefused = true;
            return;
        }

        if (_session.Context.ReadOnly)
        {
            Warn($"{_session.Context.Name} is read-only");
            return;
        }

        var loading = StyledText.Of(title + "…");
        Status.Show(loading, StatusLevel.Info, Timeout.InfiniteTimeSpan);
        _ = Task.Run(async () =>
        {
            try
            {
                var fields = await load(CancellationToken.None);
                Post(() =>
                {
                    if (ReferenceEquals(Status.Message?.Text, loading))
                    {
                        Status.Dismiss();
                    }
                    if (_app.TopOverlay is null)
                    {
                        ShowForm(title, fields, review, unchanged);
                    }
                });
            }
            catch (Exception ex)
            {
                Post(() => Error($"{title}: {ex.GetBaseException().Message}"));
            }
        });
    }

    /// <summary>
    /// The form, then its review: the confirmation lists what changed, and no goes back to the form. A review that
    /// refuses what was typed says why and opens the form again, as it was.
    /// </summary>
    private void ShowForm(string title, IReadOnlyList<FormField> fields, Func<IReadOnlyList<FormField>, Operation> review, bool unchanged)
    {
        var dialog = FormDialog.Show(_app, $" {title} ", fields, _ =>
        {
            Operation operation;
            try
            {
                operation = review(fields);
            }
            catch (Exception ex) when (ex is RefusedException or FormatException or InvalidOperationException or ArgumentException)
            {
                Error($"{title}: {ex.Message}");
                ShowForm(title, fields, review, unchanged);
                return;
            }
            Confirm(operation, back: () => ShowForm(title, fields, review, unchanged));
        }, width: Math.Min(_app.Size.Width - 4, Math.Max(60, _app.Size.Width * 60 / 100)));
        dialog.Form.AllowEmptySubmit = unchanged;
    }

    public void Load<T>(string title, Func<CancellationToken, Task<T>> load, Action<T> then)
    {
        if (_batch is not null)
        {
            _batchRefused = true;
            return;
        }

        var loading = StyledText.Of(title + "…");
        Status.Show(loading, StatusLevel.Info, Timeout.InfiniteTimeSpan);
        var session = _session;
        _ = Task.Run(async () =>
        {
            try
            {
                var value = await load(CancellationToken.None);
                Post(() =>
                {
                    if (ReferenceEquals(Status.Message?.Text, loading))
                    {
                        Status.Dismiss();
                    }
                    if (session == _session)
                    {
                        then(value);
                    }
                });
            }
            catch (Exception ex)
            {
                Post(() => Error($"{title}: {ex.GetBaseException().Message}"));
            }
        });
    }

    public void Open(string title, Func<CancellationToken, Task<IReadOnlyList<IResourceView>>> load, bool replace = false) =>
        Load(title, load, pages =>
        {
            if (pages.Count == 0)
            {
                return;
            }
            if (replace)
            {
                ShowRoot(pages[0]);
            }
            foreach (var page in pages.Skip(replace ? 1 : 0))
            {
                Push(page);
            }
        });

    public void Command(string text) => _line.Open(_commands, text);

    /// <summary>
    /// <c>ctrl+f</c>: a search over every node, application and service, by name, type or address, as it's typed;
    /// a partition id finds the partition. The chosen one opens where it sits: applications › services › partitions.
    /// </summary>
    private void Find(string? text)
    {
        var views = _views;
        var index = SearchIndex();
        SearchPicker? picker = null;
        picker = new SearchPicker(query => views.SearchChoices(index.IsCompletedSuccessfully ? index.Result : null, query), text ?? "")
        {
            Loading = () => !index.IsCompleted,
        };
        if (!index.IsCompleted)
        {
            _ = index.ContinueWith(_ => Post(() => picker.Refresh()), TaskScheduler.Default);
        }

        var overlay = new Overlay(picker)
        {
            Placement = OverlayPlacement.Top,
            WidthPercent = 70,
            Height = Math.Max(8, Math.Min(24, _app.Size.Height - 4)),
            Margin = 3,
            Modal = true,
            ScrimOpacity = 0.4,
        };
        picker.OnClose = () => _app.RemoveOverlay(overlay);
        _app.PushOverlay(overlay);
    }

    /// <summary>Every node, application and service, kept for a little while so searching again is instant.</summary>
    private Task<IReadOnlyList<SearchHit>> SearchIndex()
    {
        if (_searchIndex is { } cached && cached.Session == _session && DateTime.UtcNow - cached.At < SearchIndexFor && !cached.Index.IsFaulted)
        {
            return cached.Index;
        }

        var session = _session;
        var index = _views.Overview.GetSearchIndexAsync(CancellationToken.None);
        _searchIndex = (session, DateTime.UtcNow, index);
        _ = index.ContinueWith(
            t => Post(() => Error($"find: {t.Exception!.GetBaseException().Message}")),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return index;
    }

    /// <summary><c>!</c>: the last error, whole, however long; the status line only has room for its start.</summary>
    private void ShowLastError()
    {
        if (_lastError is not { } error)
        {
            Info("no errors so far");
            return;
        }

        var viewer = new TextViewer($"{error.At:HH:mm:ss}\n\n{error.Text}") { Wrap = true, SearchPrompt = false };
        var panel = new Panel(viewer, " last error · esc closes ") { BorderStyle = BorderStyle.Rounded };
        var width = Math.Max(40, _app.Size.Width * 70 / 100);
        var lines = error.Text.Split('\n').Sum(line => Math.Max(1, (line.Length + width - 5) / Math.Max(1, width - 4))) + 2;
        _app.PushOverlay(new Overlay(panel)
        {
            Width = width,
            Height = Math.Min(lines + 2, Math.Max(5, _app.Size.Height - 4)),
            Modal = true,
            ScrimOpacity = 0.4,
        });
    }

    /// <summary><c>:provision path</c>: uploads and provisions a package from this machine; without a path, asks for one.</summary>
    private void Provision(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Command("provision ");
            return;
        }
        _views.ProvisionLocal(path);
    }

    private void Copy()
    {
        if (Current?.CopyText is not { Length: > 0 } text)
        {
            return;
        }

        // OSC 52: the terminal puts it on the clipboard, which also works over SSH.
        _app.CopyToClipboard(text);
        Info(text.Contains('\n') ? $"copied {text.Count(c => c == '\n') + 1} lines" : $"copied {text}");
    }

    private void ShowContexts()
    {
        if (!OnContexts)
        {
            _main.Show(ContextsPage);
            // Once the rows are loaded (the first time, they aren't yet): start on the context in use.
            _contexts.Select(_session.Context.Name);
            Refresh();
        }
    }

    private void ShowCluster()
    {
        if (OnContexts)
        {
            _main.Show(ClusterPage);
            _loadedAt = null;
            Refresh();
        }
    }

    private void ShowRoot(IResourceView view)
    {
        _main.Show(ClusterPage);
        _pages.Clear();
        Register(view);
        _navigator.Reset(new Page($"{_session.Context.Name} · {view.Crumb}", view.Widget));
    }

    private void Push(IResourceView view)
    {
        Register(view);
        _navigator.Push(new Page(view.Crumb, view.Widget));
    }

    private void Register(IResourceView view)
    {
        view.Navigate = Push;
        view.Reload = Refresh;
        _pages[view.Widget] = view;
    }

    private void OnNavigated()
    {
        var onStack = _navigator.Stack.Select(page => page.Content).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var widget in _pages.Keys.Where(widget => !onStack.Contains(widget)).ToList())
        {
            _pages.Remove(widget);
        }

        _loadedAt = null;
        Refresh();
    }

    /// <summary>A saved context with its folders' rules applied, and <c>--readonly</c> over them all.</summary>
    internal ResolvedContext Resolve(ClusterContext saved)
    {
        var resolved = _store.Resolve(saved);
        return _readOnly ? resolved with { Context = resolved.Context with { ReadOnly = true, Power = false } } : resolved;
    }

    /// <summary>Connects to a saved context, read-only if its folder or <c>--readonly</c> says so.</summary>
    private void Connect(ClusterContext saved)
    {
        var context = Resolve(saved).Context;
        _connecting?.Cancel();
        var connecting = _connecting = new CancellationTokenSource();
        var message = StyledText.Of($"connecting to {context.Name}…");
        Status.Show(message, StatusLevel.Info, Timeout.InfiniteTimeSpan);

        _ = Task.Run(async () =>
        {
            try
            {
                var session = await _connector.ConnectAsync(context, NotifyAsync, connecting.Token);
                Post(() =>
                {
                    if (_connecting != connecting)
                    {
                        return;
                    }
                    _connecting = null;
                    if (ReferenceEquals(Status.Message?.Text, message))
                    {
                        Status.Dismiss();
                    }
                    _session = session;
                    _power = session.Context.Power;
                    _views = new Views(session.Queries, this);
                    _summary.Content = StyledText.Empty();
                    _summaryAt = default;
                    _store.Active = context.Name;
                    _store.Save();
                    ShowRoot(_views.Nodes());
                });
            }
            catch (Exception) when (connecting.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    if (_connecting == connecting)
                    {
                        _connecting = null;
                        Error($"{context.Name}: {ex.GetBaseException().Message}");
                    }
                });
            }
        });
    }

    private void RefreshIf(ClusterSession session)
    {
        if (session == _session)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        _loading?.Cancel();
        if (Current is not { } view)
        {
            return;
        }

        var loading = _loading = new CancellationTokenSource();
        UpdateStatus();
        RefreshSummary();

        _ = Task.Run(async () =>
        {
            try
            {
                var show = await view.LoadAsync(loading.Token);
                Post(() =>
                {
                    if (_loading != loading)
                    {
                        return;
                    }
                    _loading = null;
                    show();
                    _loadedAt = DateTime.Now;
                    // A failed load's error goes once a load works again; other errors stay until dismissed.
                    if (_loadError is not null && ReferenceEquals(Status.Message?.Text, _loadError))
                    {
                        Status.Dismiss();
                    }
                    _loadError = null;
                    UpdateStatus();
                });
            }
            catch (Exception) when (loading.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    if (_loading == loading)
                    {
                        _loading = null;
                        _loadError = Error(ex.GetBaseException().Message);
                        UpdateStatus();
                    }
                });
            }
        });
    }

    /// <summary>The summary line: asked again with the page, but not more often than every few seconds.</summary>
    private void RefreshSummary()
    {
        if (_summaryLoading || DateTime.UtcNow - _summaryAt < SummaryEvery)
        {
            return;
        }

        _summaryLoading = true;
        _summaryAt = DateTime.UtcNow;
        var session = _session;
        var views = _views;
        _ = Task.Run(async () =>
        {
            StyledText text;
            try
            {
                text = Views.SummaryLine(await views.Overview.GetSummaryAsync(CancellationToken.None));
            }
            catch (Exception ex)
            {
                text = Styles.Muted($" summary unavailable: {ex.GetBaseException().Message}");
            }
            Post(() =>
            {
                _summaryLoading = false;
                if (session == _session)
                {
                    _summary.Content = text;
                }
            });
        });
    }

    private void ShowEvents(string? window)
    {
        if (window is null)
        {
            ShowRoot(_views.Events(Views.DefaultEventWindow));
        }
        else if (RefreshInterval.TryParse(window, out var span) && span > TimeSpan.Zero)
        {
            ShowRoot(_views.Events(span));
        }
        else
        {
            Warn($"events takes how far back to look, like 30m, 6h or 48h, not '{window}'");
        }
    }

    /// <summary><c>:refresh</c> refreshes now; <c>:refresh 2s</c> (or <c>off</c>) changes the auto-refresh interval.</summary>
    private void ChangeRefresh(string? argument)
    {
        if (argument is null)
        {
            Refresh();
            return;
        }

        if (!RefreshInterval.TryParse(argument, out var interval))
        {
            Warn($"refresh takes an interval like 5s, 1m or off (at least {RefreshInterval.Format(RefreshInterval.Minimum)}), not '{argument}'");
            return;
        }

        SetRefreshInterval(interval);
        Info(interval > TimeSpan.Zero ? $"refreshing every {RefreshInterval.Format(interval)}" : "auto-refresh off; r refreshes");
        UpdateStatus();
    }

    private void SetRefreshInterval(TimeSpan interval)
    {
        _autoRefresh?.Cancel();
        _autoRefresh = null;
        _refreshInterval = interval;
        if (interval > TimeSpan.Zero)
        {
            _autoRefresh = new CancellationTokenSource();
            _ = AutoRefreshAsync(interval, _autoRefresh.Token);
        }
    }

    private async Task AutoRefreshAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Post(() =>
                {
                    if (_loading is null && !cancellationToken.IsCancellationRequested)
                    {
                        Refresh();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Post(Action action) => _app.Post(action);

    private void Info(string text) => Status.Show(StyledText.Of(text), StatusLevel.Info);

    public void Warn(string text) => Status.Show(StyledText.Of(text), StatusLevel.Warning);

    private StyledText Error(string text)
    {
        _lastError = (DateTime.Now, text);
        var message = StyledText.Of(text);
        Status.ShowError(message);
        return message;
    }

    /// <summary>The context, its folder's label and the mode; then loading, or when it was updated.</summary>
    private void UpdateStatus()
    {
        var name = StyledText.Of($" {_session.Context.Name} ").Bold().Fg(Theme.Current.Accent);
        var context = StyledText.Of($" {_session.Context.Name} ").Bold().Fg(Theme.Current.Accent);
        if (_store.Resolve(_session.Context) is { Label: { } label } folder)
        {
            context.Append(label + " ").Bold().Fg(Styles.FolderColor(folder.Color));
        }
        if (_session.Context.ReadOnly)
        {
            context.Append("read-only").Fg(Theme.Current.Info);
        }
        else if (_power)
        {
            context.Append("POWER").Bold().Fg(Theme.Current.Error);
        }
        _contextItem.Text = context;
        _contextItem.ShortText = name;

        _loadingItem.Active = _loading is not null;
        _updatedItem.Visible = _loading is null && _loadedAt is not null;
        if (_loadedAt is { } at)
        {
            var every = _refreshInterval > TimeSpan.Zero ? $" · every {RefreshInterval.Format(_refreshInterval)}" : " · auto-refresh off";
            _updatedItem.Text = Styles.Muted($"updated {at:HH:mm:ss}{every}");
            _updatedItem.ShortText = Styles.Muted($"{at:HH:mm:ss}");
        }
    }

    /// <summary>
    /// The command line (the status line while no prompt is open), and on its right the activity strip, as wide as
    /// what it has to show: running changes, and ones that just finished.
    /// </summary>
    private sealed class Footer(Prompt line, ActivityStrip strip, Activity activity) : Widget
    {
        private readonly List<ActivitySnapshot> _tasks = [];

        public override bool OnEvent(InputEvent e) => strip.OnEvent(e) || line.OnEvent(e);

        public override void Render(Surface surface, Rect area)
        {
            var width = Math.Min(StripWidth(), area.Width / 2);
            line.Render(surface, new Rect(area.X, area.Y, area.Width - width, area.Height));
            strip.Render(surface, width == 0 ? Rect.Empty : new Rect(area.Right - width + 1, area.Y, width - 1, area.Height));
        }

        protected override void VisitChildren(Action<Widget> visit)
        {
            visit(line);
            visit(strip);
        }

        // What the strip draws (glyph, title, percent, why it failed, " · " between), plus a column before it.
        private int StripWidth()
        {
            activity.CopyTo(_tasks);
            var now = activity.Clock.GetUtcNow();
            var width = 0;
            foreach (var task in _tasks)
            {
                var shown = task.IsRunning
                    || task.Ended is { } ended && ended + (task.State == ActivityState.Failed ? strip.ShowFailedFor : strip.ShowFinishedFor) > now;
                if (!shown)
                {
                    continue;
                }
                width += (width > 0 ? 3 : 1) + 2 + Unicode.StringWidth(task.Title)
                    + (task.IsRunning && task.Progress is not null ? 5 : 0)
                    + (task.State == ActivityState.Failed && task.Message is { } message ? 2 + Unicode.StringWidth(message) : 0);
            }
            return width;
        }
    }
}
