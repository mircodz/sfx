using System.Globalization;
using Sfx.Core.Auth;
using Tomlyn;
using Tomlyn.Model;

namespace Sfx.Core;

/// <summary>
/// Saved cluster contexts in a TOML file: <c>$SFX_CONFIG</c>, or <c>~/.sfx/config.toml</c>.
/// Besides <c>endpoint</c>, <c>auth</c>, <c>readonly</c> and <c>power</c>, a context's keys are the options of its auth methods.
/// </summary>
public sealed class ContextStore
{
    private const string ActiveKey = "context";
    private const string ContextsKey = "contexts";
    private const string EndpointKey = "endpoint";
    private const string AuthKey = "auth";
    private const string ReadOnlyKey = "readonly";
    private const string PowerKey = "power";

    private readonly TomlTable _root;

    private ContextStore(string path, TomlTable root)
    {
        Path = path;
        _root = root;
    }

    public string Path { get; }

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("SFX_CONFIG") is { Length: > 0 } path
            ? path
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sfx", "config.toml");

    public static ContextStore Load(string? path = null)
    {
        path ??= DefaultPath;
        var root = File.Exists(path) ? Toml.ToModel(File.ReadAllText(path)) : new TomlTable();
        return new ContextStore(path, root);
    }

    public string? Active
    {
        get => _root.TryGetValue(ActiveKey, out var value) ? value as string : null;
        set
        {
            if (value is null)
            {
                _root.Remove(ActiveKey);
            }
            else
            {
                _root[ActiveKey] = value;
            }
        }
    }

    public IReadOnlyList<ClusterContext> Contexts =>
        ContextTables()
            .Select(entry => Read(entry.Key, entry.Value))
            .OfType<ClusterContext>()
            .OrderBy(context => context.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public ClusterContext? Find(string name) =>
        Contexts.FirstOrDefault(context => context.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the context, or replaces the one with the same name.</summary>
    public void Set(ClusterContext context)
    {
        var table = new TomlTable { [EndpointKey] = context.Endpoint };
        if (context.Auth.Count > 0)
        {
            table[AuthKey] = ToArray(context.Auth);
        }
        if (context.ReadOnly)
        {
            table[ReadOnlyKey] = true;
        }
        if (context.Power)
        {
            table[PowerKey] = true;
        }

        foreach (var key in context.Options.Keys)
        {
            var values = context.Options.GetAll(key);
            table[key] = values.Count == 1 ? values[0] : ToArray(values);
        }

        RemoveTable(context.Name);
        ContextsTable()[context.Name] = table;
    }

    /// <summary>Removes the context; if it was active, no context is active afterwards.</summary>
    public bool Remove(string name)
    {
        if (!RemoveTable(name))
        {
            return false;
        }

        if (string.Equals(Active, name, StringComparison.OrdinalIgnoreCase))
        {
            Active = null;
        }
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        File.WriteAllText(Path, Toml.FromModel(_root));
    }

    private TomlTable ContextsTable()
    {
        if (_root.TryGetValue(ContextsKey, out var value) && value is TomlTable table)
        {
            return table;
        }

        table = new TomlTable();
        _root[ContextsKey] = table;
        return table;
    }

    private IEnumerable<KeyValuePair<string, TomlTable>> ContextTables() =>
        _root.TryGetValue(ContextsKey, out var value) && value is TomlTable table
            ? table.Where(entry => entry.Value is TomlTable).Select(entry => KeyValuePair.Create(entry.Key, (TomlTable)entry.Value))
            : [];

    private bool RemoveTable(string name)
    {
        var key = ContextTables().Select(entry => entry.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        return key is not null && ContextsTable().Remove(key);
    }

    private static ClusterContext? Read(string name, TomlTable table)
    {
        if (!table.TryGetValue(EndpointKey, out var value) || value is not string endpoint)
        {
            return null;
        }

        var options = new AuthOptions();
        foreach (var (key, option) in table)
        {
            if (key is not (EndpointKey or AuthKey or ReadOnlyKey or PowerKey))
            {
                foreach (var text in Strings(option))
                {
                    options.Add(key, text);
                }
            }
        }

        return new ClusterContext(
            name,
            endpoint,
            Strings(table.TryGetValue(AuthKey, out var auth) ? auth : null),
            options,
            table.TryGetValue(ReadOnlyKey, out var readOnly) && readOnly is true,
            table.TryGetValue(PowerKey, out var power) && power is true);
    }

    private static List<string> Strings(object? value) => value switch
    {
        null => [],
        TomlArray array => array.Select(Scalar).ToList(),
        _ => [Scalar(value)],
    };

    private static string Scalar(object? value) => value switch
    {
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "",
    };

    private static TomlArray ToArray(IEnumerable<string> values)
    {
        var array = new TomlArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }
}
