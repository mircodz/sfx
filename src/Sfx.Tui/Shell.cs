using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sfx.Core;

namespace Sfx.Tui;

/// <summary>
/// The screen: a page stack under a clickable breadcrumb, and a footer with the status (or the : and / prompt)
/// and clickable shortcuts. Keys, <c>:</c> commands, the palette, help and the footer all come from <see cref="Actions"/>.
/// </summary>
internal sealed class Shell : IActionHost
{
    private enum PromptMode { None, Command, Filter, Ask }

    private const string General = "General";
    private const string Navigation = "Navigation";
    private const string Movement = "Movement";
    private const string ViewsGroup = "Views";

    private readonly App _app;
    private readonly ITerminal _terminal;
    private readonly ClusterConnector _connector;
    private readonly ContextStore _store;
    private readonly Navigator _navigator = new() { BackKey = Key.Backspace };
    private readonly Dictionary<Widget, IResourceView> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlyList<ShellAction> _global;
    private readonly Label _status = new(StyledText.Empty());
    private readonly Label _promptLabel = new(StyledText.Empty());
    private readonly Input _prompt = new();
    private readonly Slot _footer;
    private readonly ActionLog _log;

    private Widget _promptRow;
    private Widget _statusRow;
    private ClusterSession _session;
    private Views _views;
    private PromptMode _mode;
    private Action<string>? _askSubmit;
    private bool _power;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _connecting;
    private DateTime? _loadedAt;
    private string? _error;
    private string? _notice;

    public Shell(App app, ITerminal terminal, ClusterSession session, ClusterConnector connector, ContextStore store, TimeSpan refreshInterval)
    {
        _app = app;
        _terminal = terminal;
        _session = session;
        _power = session.Context.Power;
        _views = new Views(session.Queries, this);
        _connector = connector;
        _store = store;
        _log = new ActionLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, "actions.log"));
        _global = GlobalActions();

        _promptRow = PromptRow(":");
        _statusRow = BuildStatusRow();
        _footer = new Slot(_statusRow);

        app.Root = new Stack(Direction.Vertical)
            .Add(_navigator, Constraint.Fill())
            .Add(_footer, Constraint.Length(1));

        app.OnEvent = OnPromptEvent;
        app.OnUnhandledEvent = OnUnhandledEvent;
        app.OnMessage = message =>
        {
            if (message is Action action)
            {
                action();
                app.Invalidate();
            }
        };
        _navigator.OnNavigate = _ => OnNavigated();
        _prompt.OnChange = text =>
        {
            if (_mode == PromptMode.Filter && Current is { } view)
            {
                view.Filter = text;
            }
        };

        ShowRoot(_views.Nodes());
        if (refreshInterval > TimeSpan.Zero)
        {
            _ = AutoRefreshAsync(refreshInterval);
        }
    }

    private IResourceView? Current =>
        _navigator.Current is { } page && _pages.TryGetValue(page.Content, out var view) ? view : null;

    /// <summary>
    /// The current page's actions, then the global ones, so a page can rebind a key. Read-only contexts drop every
    /// action that changes the cluster, and power actions stay hidden until unlocked, so keys, help and palette agree.
    /// </summary>
    private IEnumerable<ShellAction> Actions => (Current?.Actions ?? [])
        .Concat(_global)
        .Where(action => action.Risk == ActionRisk.None || !_session.Context.ReadOnly)
        .Where(action => !action.Power || _power);

    public Task NotifyAsync(string message)
    {
        Post(() =>
        {
            _notice = message;
            UpdateStatus();
        });
        return Task.CompletedTask;
    }

    private IReadOnlyList<ShellAction> GlobalActions() =>
    [
        new("Command", General, _ => OpenPrompt(PromptMode.Command), [KeyChord.Of(':')], Footer: "cmd"),
        new("Filter rows", General, _ => OpenPrompt(PromptMode.Filter), [KeyChord.Of('/')], Footer: "filter"),
        new("Command palette", General, _ => OpenPalette(), [KeyChord.Ctrl('p')], ["palette"], "palette"),
        new("Keys", General, _ => ShowHelp(), [KeyChord.Of('?')], ["help"], "help"),
        new("Refresh", General, _ => Refresh(), [KeyChord.Of('r')], ["refresh"]),
        new("Copy the selected name, or the text being read", General, _ => Copy(), [KeyChord.Of('y')], ["copy"]),
        new("Quit", General, _ => _app.Quit(), [KeyChord.Of('q'), KeyChord.Ctrl('c')], ["quit", "q"], "quit"),

        new("Back", Navigation, _ => _navigator.Pop(), [KeyChord.Named(Key.Backspace), KeyChord.Of('h')], ["back"], "back"),
        new("Clear filter, or back", Navigation, _ => ClearFilterOrBack(), [KeyChord.Named(Key.Escape)]),

        new("Nodes", ViewsGroup, _ => ShowRoot(_views.Nodes()), Commands: ["nodes", "node", "no"]),
        new("Applications", ViewsGroup, _ => ShowRoot(_views.Applications()), Commands: ["apps", "app", "applications"]),
        new("Cluster", ViewsGroup, _ => ShowRoot(_views.DescribeCluster(_session.Context.Name)), Commands: ["cluster"]),
        new("Cluster health", ViewsGroup, _ => ShowRoot(_views.DescribeCluster(_session.Context.Name, Views.ClusterHealthTab)), Commands: ["health"]),
        new("Contexts; :ctx <name> switches", ViewsGroup, SwitchContext, [KeyChord.Of('c')], ["ctx", "context", "contexts"]),
        new("Unlock or lock power actions (recover, delete, remove node state…)", General, _ => TogglePower(), Commands: ["power"]),

        new("Open row (or click it twice)", Movement, _ => Forward(Key.Enter), [KeyChord.Named(Key.Enter), KeyChord.Of('l')]),
        new("Down", Movement, _ => Forward(Key.Down), [KeyChord.Of('j')]),
        new("Up", Movement, _ => Forward(Key.Up), [KeyChord.Of('k')]),
        new("First row", Movement, _ => Forward(Key.Home), [KeyChord.Of('g')]),
        new("Last row", Movement, _ => Forward(Key.End), [KeyChord.Of('G')]),
    ];

    // While the prompt is open it takes every key, before the page sees them.
    private bool OnPromptEvent(InputEvent e)
    {
        if (_mode == PromptMode.None || e is not KeyEvent key)
        {
            return false;
        }

        switch (key.Key)
        {
            case Key.Escape:
                if (_mode == PromptMode.Filter && Current is { } view)
                {
                    view.Filter = "";
                }
                ClosePrompt();
                return true;
            case Key.Enter:
                var mode = _mode;
                var text = _prompt.Text.Trim();
                var submit = _askSubmit;
                ClosePrompt();
                if (mode == PromptMode.Command)
                {
                    RunCommand(text);
                }
                else if (mode == PromptMode.Ask)
                {
                    submit?.Invoke(text);
                }
                return true;
            default:
                _prompt.OnEvent(key);
                return true;
        }
    }

    // Keys the page didn't use. An open overlay (help, palette) keeps its keys to itself.
    private bool OnUnhandledEvent(InputEvent e)
    {
        if (_app.TopOverlay is not null || e is not KeyEvent key || ActionLookup.ForKey(Actions, key) is not { } action)
        {
            return false;
        }

        action.Run(null);
        return true;
    }

    private void RunCommand(string text)
    {
        _notice = null;
        if (text.Length == 0)
        {
            return;
        }

        if (ActionLookup.ForCommand(Actions, text) is { } match)
        {
            match.Action.Run(match.Argument);
        }
        else
        {
            _notice = $"unknown command '{text}' (ctrl+p lists everything)";
        }
        UpdateStatus();
    }

    private bool Forward(Key key) => Current?.Widget.OnEvent(new KeyEvent(key, default, KeyModifiers.None)) ?? false;

    private void ClearFilterOrBack()
    {
        if (Current is { Filter.Length: > 0 } view)
        {
            view.Filter = "";
        }
        else
        {
            _navigator.Pop();
        }
    }

    private void OpenPrompt(PromptMode mode, string? label = null, string initial = "")
    {
        _mode = mode;
        _prompt.Text = mode == PromptMode.Filter ? Current?.Filter ?? "" : initial;
        _promptRow = PromptRow(label ?? (mode == PromptMode.Command ? ":" : "/"));
        _footer.Content = _promptRow;
        _prompt.HasFocus = true;
    }

    private Widget PromptRow(string label)
    {
        _promptLabel.Content = StyledText.Of(label).Bold().Fg(Theme.Current.Accent);
        return new Stack(Direction.Horizontal)
            .Add(_promptLabel, Constraint.Length(_promptLabel.Content.Width))
            .Add(_prompt, Constraint.Fill());
    }

    private void ClosePrompt()
    {
        _mode = PromptMode.None;
        _askSubmit = null;
        _prompt.HasFocus = false;
        UpdateStatus();
    }

    private void OpenPalette()
    {
        if (_app.TopOverlay is not null)
        {
            return;
        }

        var commands = Actions
            .Where(action => action.Group != Movement)
            .DistinctBy(action => action.Title)
            .Select(action => new Command(action.Title, () => action.Run(null), action.Hint))
            .Concat(_store.Contexts.Select(context =>
                new Command($"Switch to context {context.Name}", () => Connect(context), context.Endpoint)));

        var palette = new CommandPalette(commands);
        var overlay = new Overlay(palette)
        {
            Placement = OverlayPlacement.Top,
            WidthPercent = 60,
            Height = 14,
            Margin = 3,
            Modal = true,
            ScrimOpacity = 0.4,
        };
        palette.OnAfterRun = () => _app.RemoveOverlay(overlay);
        palette.OnCancel = () => _app.RemoveOverlay(overlay);
        _app.PushOverlay(overlay);
    }

    private void ShowHelp()
    {
        if (_app.TopOverlay is not null)
        {
            return;
        }

        var actions = Actions.DistinctBy(action => action.Title).ToList();
        var hintWidth = actions.Max(action => action.Hint.Length) + 2;
        var text = StyledText.Empty();
        var lines = 0;
        foreach (var group in actions.GroupBy(action => action.Group))
        {
            text.Append((lines > 0 ? "\n\n" : "") + group.Key).Bold().Fg(Theme.Current.Accent);
            lines += lines > 0 ? 2 : 1;
            foreach (var action in group)
            {
                text.Append("\n  " + action.Hint.PadRight(hintWidth)).Fg(Theme.Current.Warning)
                    .Append(action.Title).Fg(Theme.Current.Foreground);
                lines++;
            }
        }

        var overlay = new Overlay(new Panel(new Padding(new Label(text) { Wrap = true }, new Thickness(1, 0)), " keys · esc to close ")
        {
            BorderStyle = BorderStyle.Rounded,
            Background = Theme.Current.Background,
        })
        {
            WidthPercent = 70,
            Height = Math.Min(lines + 2, Math.Max(5, _app.Size.Height - 2)),
            Modal = true,
            DismissOnEscape = true,
            ScrimOpacity = 0.4,
        };
        _app.PushOverlay(overlay);
    }

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
            _notice = $"no context named '{name}'";
            UpdateStatus();
        }
    }

    private void TogglePower()
    {
        if (_session.Context.ReadOnly)
        {
            _notice = $"{_session.Context.Name} is read-only";
        }
        else
        {
            _power = !_power;
            _notice = _power ? "power actions unlocked; :power locks them again" : "power actions locked";
        }
        UpdateStatus();
    }

    public void Run(Operation operation)
    {
        if (_session.Context.ReadOnly)
        {
            _notice = $"{_session.Context.Name} is read-only";
            UpdateStatus();
            return;
        }

        if (operation.Risk == ActionRisk.None)
        {
            Execute(operation, new HashSet<char>());
            return;
        }

        Overlay? overlay = null;
        var dialog = new ConfirmDialog(
            operation,
            options =>
            {
                _app.RemoveOverlay(overlay!);
                Execute(operation, options);
            },
            () => _app.RemoveOverlay(overlay!));
        var width = Math.Max(40, _app.Size.Width * 60 / 100);
        overlay = new Overlay(dialog)
        {
            Width = width,
            Height = Math.Min(dialog.Height(width), Math.Max(6, _app.Size.Height - 2)),
            Modal = true,
            DismissOnEscape = false,
            ScrimOpacity = 0.5,
        };
        _app.PushOverlay(overlay);
    }

    /// <summary>Runs the operation off the UI thread; the outcome goes to the status line and the action log.</summary>
    private void Execute(Operation operation, IReadOnlySet<char> options)
    {
        var session = _session;
        var title = operation.Title + string.Concat(operation.Options?.Where(o => options.Contains(o.Key)).Select(o => $" ({o.Label})") ?? []);
        _error = null;
        _notice = title + "…";
        UpdateStatus();

        _ = Task.Run(async () =>
        {
            string outcome;
            try
            {
                await operation.Run(CancellationToken.None, options);
                outcome = "ok";
                Post(() =>
                {
                    _notice = "done: " + title;
                    Refresh();
                });
            }
            catch (Exception ex)
            {
                outcome = "failed: " + ex.GetBaseException().Message;
                Post(() =>
                {
                    _notice = null;
                    _error = $"{title}: {ex.GetBaseException().Message}";
                    UpdateStatus();
                });
            }

            try
            {
                _log.Append(session.Context.Name, session.Endpoint.ToString(), title, outcome);
            }
            catch (IOException)
            {
            }
        });
    }

    public void Pick(string title, Func<CancellationToken, Task<IReadOnlyList<Choice>>> load)
    {
        _notice = title;
        UpdateStatus();
        _ = Task.Run(async () =>
        {
            try
            {
                var choices = await load(CancellationToken.None);
                Post(() => ShowPick(title, choices));
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    _notice = null;
                    _error = ex.GetBaseException().Message;
                    UpdateStatus();
                });
            }
        });
    }

    private void ShowPick(string title, IReadOnlyList<Choice> choices)
    {
        if (_app.TopOverlay is not null)
        {
            _notice = null;
            UpdateStatus();
            return;
        }

        // The question stays in the footer while the list is open.
        _notice = title;
        UpdateStatus();
        void Close(Overlay overlay)
        {
            _app.RemoveOverlay(overlay);
            if (_notice == title)
            {
                _notice = null;
                UpdateStatus();
            }
        }

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
        palette.OnAfterRun = () => Close(overlay);
        palette.OnCancel = () => Close(overlay);
        _app.PushOverlay(overlay);
    }

    public void Ask(string label, string initial, Action<string> submit)
    {
        OpenPrompt(PromptMode.Ask, label + " ", initial);
        _askSubmit = submit;
    }

    // OSC 52: the terminal puts it on the clipboard, which also works over SSH.
    private void Copy()
    {
        if (Current?.CopyText is not { Length: > 0 } text)
        {
            return;
        }

        _terminal.Write(Clipboard.SetClipboardSequence(text));
        _notice = text.Contains('\n') ? $"copied {text.Count(c => c == '\n') + 1} lines" : $"copied {text}";
        UpdateStatus();
    }

    private void ShowContexts()
    {
        if (Current is { Crumb: "contexts" })
        {
            return;
        }
        Push(Views.Contexts(_store, _session.Context.Name, Connect));
    }

    private void ShowRoot(IResourceView view)
    {
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

        _statusRow = BuildStatusRow();
        _error = null;
        _loadedAt = null;
        Refresh();
    }

    private void Connect(ClusterContext context)
    {
        _connecting?.Cancel();
        var connecting = _connecting = new CancellationTokenSource();
        _notice = $"connecting to {context.Name}…";
        UpdateStatus();

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
                    _session = session;
                    _power = session.Context.Power;
                    _views = new Views(session.Queries, this);
                    _store.Active = context.Name;
                    _store.Save();
                    _notice = null;
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
                        _notice = null;
                        _error = $"{context.Name}: {ex.GetBaseException().Message}";
                        UpdateStatus();
                    }
                });
            }
        });
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
                    _error = null;
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
                        _error = ex.GetBaseException().Message;
                        UpdateStatus();
                    }
                });
            }
        });
    }

    private async Task AutoRefreshAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync())
        {
            Post(() =>
            {
                if (_loading is null)
                {
                    Refresh();
                }
            });
        }
    }

    private void Post(Action action) => _app.Post(action);

    private void UpdateStatus()
    {
        var context = StyledText.Of($" {_session.Context.Name} ").Bold().Fg(Theme.Current.Accent);
        if (_session.Context.ReadOnly)
        {
            context.Append("read-only ").Fg(Theme.Current.Info);
        }
        else if (_power)
        {
            context.Append("POWER ").Bold().Fg(Theme.Current.Error);
        }
        _status.Content = (_error, _notice, _loading, _loadedAt) switch
        {
            ({ } error, _, _, _) => StyledText.Of(" " + error).Fg(Theme.Current.Error),
            (_, { } notice, _, _) => StyledText.Of(" " + notice).Fg(Theme.Current.Warning),
            (_, _, not null, _) => context.Append("loading…").Fg(Theme.Current.Muted),
            (_, _, _, { } at) => context.Append($"updated {at:HH:mm:ss}").Fg(Theme.Current.Muted),
            _ => context,
        };

        if (_mode == PromptMode.None)
        {
            // Errors and notices need the full width; the shortcuts come back once they're gone.
            _footer.Content = _error is null && _notice is null ? _statusRow : _status;
        }
    }

    /// <summary>The status on the left, and a clickable shortcut for every action with a footer label.</summary>
    private Widget BuildStatusRow()
    {
        var shortcuts = new Stack(Direction.Horizontal);
        var width = 0;
        foreach (var action in Actions.Where(action => action.Footer is not null && action.Keys is { Count: > 0 }))
        {
            var content = StyledText.Of($" {action.Keys![0].Short}").Bold().Fg(Theme.Current.Accent)
                .Append($" {action.Footer} ").Fg(Theme.Current.Muted);
            var button = new Button(action.Footer!, () => action.Run(null))
            {
                Content = content,
                Style = new Style(Theme.Current.Muted, Color.Default),
                HoverStyle = new Style(Theme.Current.Foreground, Theme.Current.StripeBackground),
            };
            shortcuts.Add(button, Constraint.Length(content.Width));
            width += content.Width;
        }

        return new Stack(Direction.Horizontal)
            .Add(_status, Constraint.Fill())
            .Add(shortcuts, Constraint.Length(width));
    }

    /// <summary>Shows one of several widgets in the same place.</summary>
    private sealed class Slot(Widget content) : Widget
    {
        public Widget Content { get; set; } = content;

        public override bool HasFocus
        {
            get => Content.HasFocus;
            set => Content.HasFocus = value;
        }

        public override bool OnEvent(InputEvent e) => Content.OnEvent(e);

        public override void Render(Surface surface, Rect area) => Content.Render(surface, area);
    }
}
