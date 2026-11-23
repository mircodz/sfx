using Sfx.Core;

namespace Sfx.Tui;

/// <summary><c>sfx context list|add|use|move|show|remove</c>: manages the saved contexts.</summary>
internal static class ContextCommand
{
    public const string Usage = """
        sfx context list
        sfx context add <name> <endpoint> [--auth <methods>] [auth options] [--readonly | --power] [--folder <path>] [--use]
        sfx context use <name>
        sfx context move <name> <folder>    file it under a folder like prod/europe; "" or / for the top level
        sfx context show [<name>]
        sfx context remove <name>
        """;

    public static int Run(IReadOnlyList<string> args, CommandLineOptions options, ContextStore store, ClusterConnector connector, TextWriter? output = null)
    {
        output ??= Console.Out;
        switch (args)
        {
            case [] or ["list"]:
                List(store, output);
                return 0;

            case ["add", var name, var endpoint]:
                var folder = FolderPath.Normalize(options.Folder);
                var context = new ClusterContext(name, endpoint, options.AuthMethods, options.AuthOptions, options.ReadOnly, options.Power, folder);
                connector.Resolve(context);
                store.Set(context);
                if (options.Use || store.Active is null)
                {
                    store.Active = name;
                }
                store.Save();
                output.WriteLine(folder is null
                    ? $"Saved context '{name}' to {store.Path}."
                    : $"Saved context '{name}' in folder '{folder}' to {store.Path}.");
                return 0;

            case ["use", var name]:
                store.Active = Require(store, name).Name;
                store.Save();
                output.WriteLine($"Using context '{store.Active}'.");
                return 0;

            case ["move", var name, var target]:
                var moved = Require(store, name) with { Folder = FolderPath.Normalize(target) };
                store.Set(moved);
                store.Save();
                output.WriteLine(moved.Folder is null
                    ? $"Moved context '{moved.Name}' to the top level."
                    : $"Moved context '{moved.Name}' to folder '{moved.Folder}'.");
                return 0;

            case ["show"]:
                Show(store, Require(store, store.Active ?? throw new UsageException("No active context.")), output);
                return 0;

            case ["show", var name]:
                Show(store, Require(store, name), output);
                return 0;

            case ["remove", var name]:
                if (!store.Remove(name))
                {
                    throw new UsageException($"No context named '{name}'.");
                }
                store.Save();
                output.WriteLine($"Removed context '{name}'.");
                return 0;

            default:
                throw new UsageException($"Unknown context command.\n\n{Usage}");
        }
    }

    private static ClusterContext Require(ContextStore store, string name) =>
        store.Find(name) ?? throw new UsageException($"No context named '{name}'. See 'sfx context list'.");

    /// <summary>Top-level contexts, then each folder under a heading, subfolders indented two spaces per level.</summary>
    private static void List(ContextStore store, TextWriter output)
    {
        var contexts = store.Contexts.Select(store.Resolve).ToList();
        if (contexts.Count == 0)
        {
            output.WriteLine($"No contexts in {store.Path}. Add one with 'sfx context add <name> <endpoint>'.");
            return;
        }

        var byFolder = contexts
            .GroupBy(c => c.Context.Folder ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var folders = byFolder.Keys
            .Where(path => path.Length > 0)
            .SelectMany(FolderPath.Ancestors)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(FolderOrder.Comparer)
            .ToList();

        var width = Math.Max(4, contexts.Max(c => Indent(c.Context.Folder).Length + c.Context.Name.Length));
        var endpointWidth = contexts.Any(c => c.Label is not null) ? contexts.Max(c => c.Context.Endpoint.Length) : 0;
        output.WriteLine($"  {"NAME".PadRight(width)}  {"AUTH",-12}  {"MODE",-8}  ENDPOINT");

        void Write(ResolvedContext resolved)
        {
            var context = resolved.Context;
            var marker = string.Equals(context.Name, store.Active, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            var name = (Indent(context.Folder) + context.Name).PadRight(width);
            var auth = context.Auth.Count > 0 ? string.Join(",", context.Auth) : "none";
            var label = resolved.Label is null ? "" : $"  [{resolved.Label}]";
            output.WriteLine($"{marker} {name}  {auth,-12}  {Mode(context),-8}  {context.Endpoint.PadRight(endpointWidth)}{label}".TrimEnd());
        }

        foreach (var context in byFolder.GetValueOrDefault("") ?? [])
        {
            Write(context);
        }
        foreach (var folder in folders)
        {
            var slash = folder.LastIndexOf('/');
            output.WriteLine($"  {Indent(slash < 0 ? null : folder[..slash])}{folder[(slash + 1)..]}/");
            foreach (var context in byFolder.GetValueOrDefault(folder) ?? [])
            {
                Write(context);
            }
        }
    }

    private static string Indent(string? folder) => new(' ', 2 * FolderOrder.Depth(folder));

    public static string Mode(ClusterContext context) => context.ReadOnly ? "readonly" : context.Power ? "power" : "normal";

    /// <summary>The context as saved, with the mode and label its folders give it.</summary>
    private static void Show(ContextStore store, ClusterContext context, TextWriter output)
    {
        var resolved = store.Resolve(context);
        output.WriteLine($"{"name",-18}{context.Name}");
        output.WriteLine($"{"endpoint",-18}{context.Endpoint}");
        output.WriteLine($"{"auth",-18}{(context.Auth.Count > 0 ? string.Join(",", context.Auth) : "none")}");
        output.WriteLine($"{"mode",-18}{Mode(resolved.Context)}");
        if (context.Folder is not null)
        {
            output.WriteLine($"{"folder",-18}{context.Folder}");
        }
        if (resolved.Label is not null)
        {
            output.WriteLine($"{"label",-18}{resolved.Label}");
        }
        foreach (var key in context.Options.Keys)
        {
            output.WriteLine($"{key,-18}{string.Join(", ", context.Options.GetAll(key))}");
        }
    }
}

/// <summary>
/// Orders contexts the way <c>sfx context list</c> shows them: top level first, then folder by folder (each folder
/// followed by its subfolders), then by name.
/// </summary>
internal sealed record FolderOrder(string? Folder, string Name) : IComparable
{
    public static IComparer<string?> Comparer { get; } = Comparer<string?>.Create(Compare);

    public static int Depth(string? folder) => FolderPath.Normalize(folder) is { } path ? path.Count(c => c == '/') + 1 : 0;

    public int CompareTo(object? obj) => obj is FolderOrder other
        ? Compare(Folder, other.Folder) is var folder and not 0 ? folder : StringComparer.OrdinalIgnoreCase.Compare(Name, other.Name)
        : 1;

    private static int Compare(string? a, string? b)
    {
        var left = FolderPath.Normalize(a)?.Split('/') ?? [];
        var right = FolderPath.Normalize(b)?.Split('/') ?? [];
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var segment = StringComparer.OrdinalIgnoreCase.Compare(left[i], right[i]);
            if (segment != 0)
            {
                return segment;
            }
        }
        return left.Length.CompareTo(right.Length);
    }
}
