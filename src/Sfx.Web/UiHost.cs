using Sfx.Core;

namespace Sfx.Web;

/// <param name="Messages">What sign-in has said so far, e.g. a device code to enter; shown while connecting.</param>
public sealed record ConnectingDto(string Context, IReadOnlyList<string> Messages, string? Error);

/// <param name="ReadOnly">After folder rules: a read-only folder makes every context in it read-only.</param>
/// <param name="Label">From the nearest folder that sets one.</param>
public sealed record ContextDto(
    string Name,
    string Endpoint,
    IReadOnlyList<string> Auth,
    bool ReadOnly,
    bool Power,
    bool Saved,
    string? Folder,
    string? Label,
    string? Color);

public sealed record FolderDto(string Path, string? Label, string? Color, bool ReadOnly);

/// <param name="Session">The current connection's <see cref="UiSession.Id"/>.</param>
/// <param name="ConfigPath">Where contexts are saved: ~/.sfx/config.toml, or $SFX_CONFIG.</param>
public sealed record ContextsDto(
    string Current,
    string Session,
    IReadOnlyList<ContextDto> Contexts,
    IReadOnlyList<FolderDto> Folders,
    ConnectingDto? Connecting,
    string ConfigPath);

/// <summary>
/// Connecting runs in the background so sign-in (e.g. a device code) can show in the browser; the current cluster stays
/// in use until the new one is ready, and stays if it fails.
/// </summary>
/// <param name="readOnly">--readonly: every context connected in this run is read-only, not just the first.</param>
public sealed class UiHost(ClusterSession initial, ContextStore store, ClusterConnector connector, ActionLog log, string version, bool readOnly = false)
{
    private readonly Lock _lock = new();
    private UiSession _current = new(initial, log, version, Labelled(store, initial.Context));
    private Connecting? _connecting;

    // Every connection of this run by id, to tell a tab which context it's still showing.
    private readonly Dictionary<string, string> _shown = [];

    private sealed class Connecting(string context)
    {
        public string Context => context;
        public List<string> Messages { get; } = [];
        public string? Error { get; set; }
        public bool Done { get; set; }
    }

    public UiSession Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Saved contexts, plus the current one if it was given on the command line rather than saved.</summary>
    public ContextsDto Contexts()
    {
        lock (_lock)
        {
            var saved = store.Contexts.Select(c => Dto(Resolve(c), saved: true)).ToList();
            if (saved.All(c => !c.Name.Equals(_current.Context.Name, StringComparison.OrdinalIgnoreCase)))
            {
                saved.Insert(0, Dto(new ResolvedContext(_current.Context, null, null), saved: false));
            }
            var connecting = _connecting is { Done: false } or { Error: not null } ? _connecting : null;
            return new ContextsDto(
                _current.Context.Name,
                _current.Id,
                saved,
                store.Folders.Select(f => new FolderDto(f.Path, f.Label, f.Color, f.ReadOnly)).ToList(),
                connecting is null ? null : new ConnectingDto(connecting.Context, [.. connecting.Messages], connecting.Error),
                Home(store.Path));
        }
    }

    /// <summary>Starts connecting to a saved context; switches once connected.</summary>
    public void Switch(string name)
    {
        var saved = store.Find(name) ?? throw new ActionException($"No context named '{name}'. Add it with 'sfx context add'.", StatusCodes.Status404NotFound);
        var resolved = Resolve(saved);
        var context = resolved.Context;
        Connecting connecting;
        lock (_lock)
        {
            if (_connecting is { Done: false, Error: null } busy)
            {
                throw new ActionException($"Still connecting to {busy.Context}. Wait for it, then switch.", StatusCodes.Status409Conflict);
            }
            connecting = new Connecting(name);
            _connecting = connecting;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var session = await connector.ConnectAsync(context, message =>
                {
                    lock (_lock)
                    {
                        connecting.Messages.Add(message);
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None);

                lock (_lock)
                {
                    Use(new UiSession(session, log, version, resolved));
                    connecting.Done = true;
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    connecting.Error = $"Couldn't connect to {name}: {ClusterConnector.Describe(ex).TrimEnd('.')}. Still on {_current.Context.Name}.";
                }
            }
        });
    }

    internal void Use(UiSession session)
    {
        lock (_lock)
        {
            _shown.TryAdd(_current.Id, _current.Context.Name);
            _current = session;
        }
    }

    /// <summary>
    /// Refuses a change from a tab showing an earlier connection than <paramref name="session"/> (it sends the one it
    /// shows as <see cref="WebServer.SessionHeader"/>), which would otherwise land on a cluster the tab isn't showing.
    /// </summary>
    public void Expect(UiSession session, HttpContext context)
    {
        var tab = context.Request.Headers[WebServer.SessionHeader].ToString();
        if (tab == session.Id)
        {
            return;
        }
        string? shown;
        lock (_lock)
        {
            shown = _shown.GetValueOrDefault(tab);
        }
        var current = session.Context.Name;
        var message = shown is null || shown.Equals(current, StringComparison.OrdinalIgnoreCase)
            ? $"sfx ui reconnected to {current} since this tab loaded it. Reload to continue."
            : $"This tab is on {shown} but sfx ui switched to {current}. Reload to continue.";
        throw new ActionException(message, StatusCodes.Status409Conflict);
    }

    /// <summary>Forgets a failed attempt once the browser has shown it.</summary>
    public void Dismiss()
    {
        lock (_lock)
        {
            if (_connecting is { Error: not null })
            {
                _connecting = null;
            }
        }
    }

    /// <summary>The config path with the home directory as ~.</summary>
    private static string Home(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var full = Path.GetFullPath(path);
        return home.Length > 0 && full.StartsWith(home, StringComparison.Ordinal) ? "~" + full[home.Length..] : full;
    }

    /// <summary>A saved context as it connects in this run: folder rules apply, and so does --readonly.</summary>
    internal ResolvedContext Resolve(ClusterContext saved)
    {
        var resolved = store.Resolve(saved);
        return readOnly ? resolved with { Context = resolved.Context with { ReadOnly = true, Power = false } } : resolved;
    }

    private static ContextDto Dto(ResolvedContext r, bool saved)
    {
        var c = r.Context;
        return new(c.Name, c.Endpoint, c.Auth.Count > 0 ? c.Auth : ["none"], c.ReadOnly, c.Power, saved, c.Folder, r.Label, r.Color);
    }

    /// <summary>Folder label and color for a saved context; one given on the command line has none.</summary>
    private static ResolvedContext Labelled(ContextStore store, ClusterContext context) =>
        store.Find(context.Name) is { } saved ? store.Resolve(saved) with { Context = context } : new ResolvedContext(context, null, null);
}
