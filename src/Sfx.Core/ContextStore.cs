using System.Globalization;
using Sfx.Core.Auth;
using Tomlyn;
using Tomlyn.Model;

namespace Sfx.Core;

/// <summary>
/// Saved cluster contexts in a TOML file: <c>$SFX_CONFIG</c>, or <c>~/.sfx/config.toml</c>.
/// Besides <c>endpoint</c>, <c>auth</c>, <c>readonly</c>, <c>power</c> and <c>folder</c>, a context's keys are the options of its auth methods.
/// <c>[folders.&lt;path&gt;]</c> tables set a <c>label</c>, <c>color</c> and <c>readonly</c> for the contexts in a folder and its subfolders.
/// </summary>
public sealed class ContextStore
{
    private const string ActiveKey = "context";
    private const string ContextsKey = "contexts";
    private const string EndpointKey = "endpoint";
    private const string AuthKey = "auth";
    private const string ReadOnlyKey = "readonly";
    private const string PowerKey = "power";
    private const string FolderKey = "folder";
    private const string FoldersKey = "folders";
    private const string LabelKey = "label";
    private const string ColorKey = "color";

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
        var root = File.Exists(path) ? TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path)) ?? new TomlTable() : new TomlTable();
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

    /// <summary>
    /// The <c>[folders.&lt;path&gt;]</c> tables, by path. A path may be quoted (<c>[folders."prod/europe"]</c>) or nested
    /// (<c>[folders.prod.europe]</c>). Unknown keys and colors are ignored.
    /// </summary>
    public IReadOnlyList<FolderSettings> Folders =>
        (_root.TryGetValue(FoldersKey, out var value) && value is TomlTable table ? ReadFolders(table, "") : [])
            .GroupBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FolderSettings(
                group.Key,
                group.Select(f => f.Label).FirstOrDefault(label => label is not null),
                group.Select(f => f.Color).FirstOrDefault(color => color is not null),
                group.Any(f => f.ReadOnly)))
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Applies the rules of the context's folders: label and color come from the nearest folder that sets them, and a
    /// read-only folder makes the context read-only (and not power), whatever the context says.
    /// </summary>
    public ResolvedContext Resolve(ClusterContext context)
    {
        if (FolderPath.Normalize(context.Folder) is not { } path)
        {
            return new ResolvedContext(context with { Folder = null }, null, null);
        }

        var folders = Folders.ToDictionary(folder => folder.Path, StringComparer.OrdinalIgnoreCase);
        string? label = null;
        string? color = null;
        var readOnly = context.ReadOnly;
        foreach (var ancestor in FolderPath.Ancestors(path))
        {
            if (folders.TryGetValue(ancestor, out var folder))
            {
                label = folder.Label ?? label;
                color = folder.Color ?? color;
                readOnly |= folder.ReadOnly;
            }
        }

        return new ResolvedContext(context with { Folder = path, ReadOnly = readOnly, Power = !readOnly && context.Power }, label, color);
    }

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
        if (FolderPath.Normalize(context.Folder) is { } folder)
        {
            table[FolderKey] = folder;
        }

        foreach (var key in context.Options.Keys)
        {
            var values = context.Options.GetAll(key);
            table[key] = values.Count == 1 ? values[0] : ToArray(values);
        }

        // In place, so changing one context doesn't move it to the end of the file.
        var contexts = ContextsTable();
        var existing = contexts.Keys.FirstOrDefault(k => k.Equals(context.Name, StringComparison.OrdinalIgnoreCase) && contexts[k] is TomlTable);
        if (existing is null)
        {
            contexts[context.Name] = table;
            return;
        }

        var entries = contexts.ToList();
        contexts.Clear();
        foreach (var (key, value) in entries)
        {
            contexts[key == existing ? context.Name : key] = key == existing ? table : value;
        }
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
        File.WriteAllText(Path, Tidy(TomlSerializer.Serialize(_root, WriteOptions)));
    }

    private static readonly TomlSerializerOptions WriteOptions = new() { BlankLineBeforeTableHeaders = true };

    /// <summary>
    /// As people write the file: no header for a table that only holds other tables (<c>[contexts]</c> above
    /// <c>[contexts.prod]</c>), and one blank line between tables.
    /// </summary>
    internal static string Tidy(string toml)
    {
        var lines = toml.ReplaceLineEndings("\n").Split('\n').ToList();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (TableName(lines[i]) is not { } name)
            {
                continue;
            }
            var next = i + 1;
            while (next < lines.Count && lines[next].Trim().Length == 0)
            {
                next++;
            }
            if (next < lines.Count && TableName(lines[next]) is { } child && child.StartsWith(name + ".", StringComparison.Ordinal))
            {
                lines.RemoveRange(i, next - i);
            }
        }
        return string.Join('\n', lines).Trim() + "\n";
    }

    private static string? TableName(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('[') && !trimmed.StartsWith("[[", StringComparison.Ordinal) && trimmed.EndsWith(']') ? trimmed[1..^1].Trim() : null;
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
            if (key is not (EndpointKey or AuthKey or ReadOnlyKey or PowerKey or FolderKey))
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
            table.TryGetValue(PowerKey, out var power) && power is true,
            FolderPath.Normalize(table.TryGetValue(FolderKey, out var folder) ? folder as string : null));
    }

    private static IEnumerable<FolderSettings> ReadFolders(TomlTable table, string parent)
    {
        foreach (var (key, value) in table)
        {
            if (value is not TomlTable folder || FolderPath.Normalize($"{parent}/{key}") is not { } path)
            {
                continue;
            }

            var label = folder.TryGetValue(LabelKey, out var text) && text is string l && l.Trim() is { Length: > 0 } trimmed ? trimmed : null;
            var color = folder.TryGetValue(ColorKey, out var name) && name is string c
                ? FolderPath.Colors.FirstOrDefault(allowed => allowed.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            var readOnly = folder.TryGetValue(ReadOnlyKey, out var flag) && flag is true;
            yield return new FolderSettings(path, label, color, readOnly);

            foreach (var child in ReadFolders(folder, path))
            {
                yield return child;
            }
        }
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
