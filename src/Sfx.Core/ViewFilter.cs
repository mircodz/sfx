using System.Text;
using System.Text.RegularExpressions;

namespace Sfx.Core;

/// <summary>A <c>[views.&lt;name&gt;]</c> table: which applications a view shows, by glob on their <c>fabric:/</c> name.</summary>
/// <param name="Apps">Patterns of the applications to show; empty shows every application <paramref name="Exclude"/> doesn't remove.</param>
/// <param name="Exclude">Patterns of the applications to leave out, even ones <paramref name="Apps"/> matches.</param>
public sealed record ViewDefinition(string Name, IReadOnlyList<string> Apps, IReadOnlyList<string> Exclude);

/// <summary>A view in the config can't be used as written, e.g. a pattern that isn't a <c>fabric:/</c> name.</summary>
public sealed class ViewConfigurationException(string message) : Exception(message);

/// <summary>
/// A named filter on applications. Services, partitions, replicas, upgrades and events are filtered through their
/// application. It's a lens, not access control: cluster health, nodes and direct links are never filtered.
/// </summary>
public sealed class ViewFilter
{
    private const string Scheme = "fabric:/";

    private readonly Regex? _apps;
    private readonly Regex? _exclude;

    private ViewFilter(ViewDefinition? definition, Regex? apps, Regex? exclude)
    {
        Definition = definition;
        _apps = apps;
        _exclude = exclude;
    }

    /// <summary>No view: every application is in.</summary>
    public static ViewFilter None { get; } = new(null, null, null);

    /// <summary>What the view was built from; null for <see cref="None"/>.</summary>
    public ViewDefinition? Definition { get; }

    public string? Name => Definition?.Name;

    public bool IsNone => Definition is null;

    /// <summary>Checks the patterns and compiles them once.</summary>
    /// <param name="configPath">Where the view is defined, for errors.</param>
    /// <exception cref="ViewConfigurationException">A pattern doesn't start with <c>fabric:/</c>, or the view has no pattern.</exception>
    public static ViewFilter From(ViewDefinition view, string? configPath = null)
    {
        var where = configPath is null ? $"View '{view.Name}'" : $"View '{view.Name}' in {configPath}";
        if (view.Apps.Count == 0 && view.Exclude.Count == 0)
        {
            throw new ViewConfigurationException($"{where} has no patterns; give it apps, exclude or both.");
        }

        foreach (var pattern in view.Apps.Concat(view.Exclude))
        {
            if (!pattern.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                throw new ViewConfigurationException(
                    $"{where} has the pattern '{pattern}', which doesn't start with {Scheme}; patterns match application names like fabric:/payments.");
            }
        }

        return new ViewFilter(view, Compile(view.Apps), Compile(view.Exclude));
    }

    /// <summary>
    /// Whether the application is in the view: it matches a pattern of <c>apps</c> (or there are none) and no pattern
    /// of <c>exclude</c>. Case doesn't matter.
    /// </summary>
    public bool Includes(string applicationName) =>
        (_apps is null || _apps.IsMatch(applicationName)) && (_exclude is null || !_exclude.IsMatch(applicationName));

    /// <summary>
    /// One regular expression for a list of globs: <c>*</c> matches within a path segment (not '/'), <c>**</c> across
    /// segments, and everything else matches itself. Null for no patterns.
    /// </summary>
    internal static Regex? Compile(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return null;
        }

        var regex = new StringBuilder("^(?:");
        for (var p = 0; p < patterns.Count; p++)
        {
            if (p > 0)
            {
                regex.Append('|');
            }

            var pattern = patterns[p];
            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] != '*')
                {
                    regex.Append(Regex.Escape(pattern[i].ToString()));
                }
                else if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    regex.Append(".*");
                    i++;
                }
                else
                {
                    regex.Append("[^/]*");
                }
            }
        }
        regex.Append(")$");
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

/// <summary>The views a config defines, by name (case doesn't matter), compiled once.</summary>
public sealed class ViewCatalog
{
    private readonly Dictionary<string, ViewFilter> _byName;

    /// <param name="configPath">Where the views are defined, as it's shown in messages, e.g. <c>~/.sfx/config.toml</c>.</param>
    /// <exception cref="ViewConfigurationException">A view can't be used as written.</exception>
    public ViewCatalog(IEnumerable<ViewDefinition> views, string configPath)
    {
        ConfigPath = configPath;
        Views = views
            .DistinctBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .Select(view => ViewFilter.From(view, configPath))
            .ToList();
        _byName = Views.ToDictionary(view => view.Name!, StringComparer.OrdinalIgnoreCase);
    }

    public static ViewCatalog Empty { get; } = new([], "~/.sfx/config.toml");

    /// <summary>Reads and checks the views of the store's config.</summary>
    /// <exception cref="ViewConfigurationException">A view can't be used as written.</exception>
    public static ViewCatalog From(ContextStore store) => new(store.Views, store.DisplayPath);

    public string ConfigPath { get; }

    /// <summary>Every view, by name.</summary>
    public IReadOnlyList<ViewFilter> Views { get; }

    public IReadOnlyList<ViewDefinition> Definitions => Views.Select(view => view.Definition!).ToList();

    public ViewFilter? Find(string? name) => name is not null && _byName.TryGetValue(name.Trim(), out var view) ? view : null;

    /// <summary>"There's no view x. Views: a, b.", or that there are none, for a name <see cref="Find"/> doesn't know.</summary>
    public string Unknown(string name) =>
        Views.Count == 0
            ? $"No views are defined in {ConfigPath}."
            : $"There's no view {name}. Views: {string.Join(", ", Views.Select(view => view.Name))}.";

    /// <summary>
    /// The view a context opens with: its <c>view</c> if that's defined, else none. <paramref name="warning"/> says
    /// when the context names a view that isn't defined.
    /// </summary>
    public ViewFilter DefaultFor(ClusterContext context, out string? warning)
    {
        warning = null;
        if (context.View is not { Length: > 0 } name)
        {
            return ViewFilter.None;
        }
        if (Find(name) is { } view)
        {
            return view;
        }
        warning = $"Context '{context.Name}' uses view '{name}', which isn't defined in {ConfigPath}; showing every application.";
        return ViewFilter.None;
    }
}
