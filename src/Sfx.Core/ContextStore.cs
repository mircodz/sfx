using System.Globalization;
using Sfx.Core.Auth;
using Tomlyn;
using Tomlyn.Model;

namespace Sfx.Core;

/// <summary>
/// Saved cluster contexts in a TOML file: <c>$SFX_CONFIG</c>, or <c>~/.sfx/config.toml</c>.
/// Besides <c>endpoint</c>, <c>auth</c>, <c>readonly</c>, <c>power</c> and <c>folder</c>, a context's keys are the options of its auth methods.
/// <c>[folders.&lt;path&gt;]</c> tables set a <c>label</c>, <c>color</c> and <c>readonly</c> for the contexts in a folder and its subfolders.
/// Changes are kept until <see cref="Save"/>, which applies them to the file as it is then, so edits made since the load survive.
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

    private readonly List<Change> _changes = [];
    private TomlTable _root;

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
        get => ActiveIn(_root);
        set => Make(new ActiveChange(value));
    }

    private static string? ActiveIn(TomlTable root) => root.TryGetValue(ActiveKey, out var value) ? value as string : null;

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
    public void Set(ClusterContext context) => Make(new ContextChange(Find(context.Name), context));

    /// <summary>Removes the context; if it was active, no context is active afterwards.</summary>
    public bool Remove(string name)
    {
        if (Find(name) is null)
        {
            return false;
        }

        Make(new RemoveChange(name));
        return true;
    }

    /// <summary>
    /// Applies the changes since the load (or the last save) to the file as it is now, keeping its comments and layout,
    /// and replaces it in one step. Afterwards the store holds what the file does, edits made elsewhere included.
    /// </summary>
    public void Save()
    {
        var path = System.IO.Path.GetFullPath(Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Through a symlink (dotfiles), so the replacement doesn't turn the link into a file.
        var link = new FileInfo(path);
        path = link.LinkTarget is null ? path : link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var exists = File.Exists(path);
        var disk = exists ? File.ReadAllText(path) : "";
        var merged = Merge(disk, _changes);
        if (!exists || merged != disk)
        {
            Write(path, merged);
        }

        _root = TomlSerializer.Deserialize<TomlTable>(merged) ?? new TomlTable();
        _changes.Clear();
    }

    /// <summary>
    /// The changes applied to <paramref name="toml"/> as text. If the edited text doesn't come out meaning what the
    /// changes do to its model (a layout the edits don't handle, like inline tables), the model is written out instead.
    /// </summary>
    internal static string Merge(string toml, IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0)
        {
            return toml;
        }

        var expected = TomlSerializer.Deserialize<TomlTable>(toml) ?? new TomlTable();
        string? text = toml;
        foreach (var change in changes)
        {
            text = text is null ? null : Edit(text, change);
            Apply(expected, change);
        }

        return text is not null && TomlText.Parse(text) is { } edited && TomlText.Canonical(edited) == TomlText.Canonical(expected)
            ? text
            : Tidy(TomlSerializer.Serialize(expected, WriteOptions));
    }

    private static void Write(string path, string text)
    {
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, text);
            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
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

    /// <summary>A change to the file: kept to apply again, at save, to the file as it is then.</summary>
    internal abstract record Change;

    /// <summary>Only the keys that differ from <paramref name="Before"/> change, so edits to the others made elsewhere stay.</summary>
    internal sealed record ContextChange(ClusterContext? Before, ClusterContext After) : Change;

    internal sealed record RemoveChange(string Name) : Change;

    internal sealed record ActiveChange(string? Name) : Change;

    private void Make(Change change)
    {
        Apply(_root, change);
        _changes.Add(change);
    }

    private static void Apply(TomlTable root, Change change)
    {
        switch (change)
        {
            case ActiveChange { Name: null }:
                root.Remove(ActiveKey);
                break;
            case ActiveChange { Name: { } name }:
                root[ActiveKey] = name;
                break;
            case RemoveChange remove:
                if (ContextKey(root, remove.Name) is { } found)
                {
                    ContextsTable(root).Remove(found);
                    if (string.Equals(ActiveIn(root), remove.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        root.Remove(ActiveKey);
                    }
                }
                break;
            case ContextChange set:
                var contexts = ContextsTable(root);
                if (ContextKey(root, set.After.Name) is not { } existing)
                {
                    contexts[set.After.Name] = ToTable(set.After);
                    break;
                }

                var table = (TomlTable)contexts[existing];
                foreach (var (key, value) in Diff(set.Before, set.After, table))
                {
                    if (value is null)
                    {
                        table.Remove(key);
                    }
                    else
                    {
                        table[key] = value;
                    }
                }

                // In place, so renaming one context doesn't move it to the end of the file.
                var entries = contexts.ToList();
                contexts.Clear();
                foreach (var (key, value) in entries)
                {
                    contexts[key == existing ? set.After.Name : key] = value;
                }
                break;
        }
    }

    /// <summary>The change made to the text, or null if the text isn't laid out in a way <see cref="TomlText"/> edits.</summary>
    private static string? Edit(string text, Change change)
    {
        if (TomlText.Parse(text) is not { } model)
        {
            return null;
        }

        switch (change)
        {
            case ActiveChange active:
                return TomlText.SetKey(text, [], ActiveKey, active.Name);

            case RemoveChange remove:
                if (ContextKey(model, remove.Name) is not { } key)
                {
                    return text;
                }
                var removed = TomlText.RemoveTable(text, [ContextsKey, key]);
                return removed is not null && string.Equals(ActiveIn(model), remove.Name, StringComparison.OrdinalIgnoreCase)
                    ? TomlText.SetKey(removed, [], ActiveKey, null)
                    : removed;

            case ContextChange set:
                if (ContextKey(model, set.After.Name) is not { } existing)
                {
                    return TomlText.AddTable(text, [ContextsKey, set.After.Name], ToTable(set.After));
                }
                string? edited = text;
                foreach (var (name, value) in Diff(set.Before, set.After, ContextTables(model).First(entry => entry.Key == existing).Value))
                {
                    edited = edited is null ? null : TomlText.SetKey(edited, [ContextsKey, existing], name, value);
                }
                return edited is not null && existing != set.After.Name
                    ? TomlText.RenameTable(edited, [ContextsKey, existing], [ContextsKey, set.After.Name])
                    : edited;

            default:
                return null;
        }
    }

    /// <summary>
    /// The keys to change in <paramref name="table"/> (null: remove) to make it <paramref name="after"/>: those that
    /// differ from <paramref name="before"/>, or with no before, all that differ from the table.
    /// </summary>
    private static List<KeyValuePair<string, object?>> Diff(ClusterContext? before, ClusterContext after, TomlTable table)
    {
        var from = before is null ? table : ToTable(before);
        var to = ToTable(after);
        return
        [
            .. from.Keys.Where(key => !to.ContainsKey(key)).Select(key => KeyValuePair.Create(key, (object?)null)),
            .. to.Where(entry => !from.TryGetValue(entry.Key, out var old) || TomlText.Canonical(old) != TomlText.Canonical(entry.Value))
                .Select(entry => KeyValuePair.Create(entry.Key, (object?)entry.Value)),
        ];
    }

    private static TomlTable ContextsTable(TomlTable root)
    {
        if (root.TryGetValue(ContextsKey, out var value) && value is TomlTable table)
        {
            return table;
        }

        table = new TomlTable();
        root[ContextsKey] = table;
        return table;
    }

    private static string? ContextKey(TomlTable root, string name) =>
        ContextTables(root).Select(entry => entry.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<KeyValuePair<string, TomlTable>> ContextTables() => ContextTables(_root);

    private static IEnumerable<KeyValuePair<string, TomlTable>> ContextTables(TomlTable root) =>
        root.TryGetValue(ContextsKey, out var value) && value is TomlTable table
            ? table.Where(entry => entry.Value is TomlTable).Select(entry => KeyValuePair.Create(entry.Key, (TomlTable)entry.Value))
            : [];

    private static TomlTable ToTable(ClusterContext context)
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
        return table;
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
