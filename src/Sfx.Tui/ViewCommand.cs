using Sfx.Core;

namespace Sfx.Tui;

/// <summary><c>sfx view list</c>: the views the config defines, and which contexts open with each.</summary>
internal static class ViewCommand
{
    public const string Usage = """
        sfx view list                       the views in the config, their patterns and the contexts that open with them
        """;

    public static int Run(IReadOnlyList<string> args, ContextStore store, TextWriter? output = null)
    {
        output ??= Console.Out;
        switch (args)
        {
            case [] or ["list"]:
                List(store, output);
                return 0;

            default:
                throw new UsageException($"Unknown view command.\n\n{Usage}");
        }
    }

    /// <summary>Each view with its patterns and the contexts that open with it, then contexts that name a view that isn't defined.</summary>
    private static void List(ContextStore store, TextWriter output)
    {
        var catalog = ViewCatalog.From(store);
        var contexts = store.Contexts.Where(c => c.View is not null).ToList();
        if (catalog.Views.Count == 0)
        {
            output.WriteLine($"No views in {store.Path}. Add a [views.<name>] table with apps or exclude patterns, like apps = [\"fabric:/payments/*\"].");
        }

        foreach (var view in catalog.Definitions)
        {
            output.WriteLine(view.Name);
            if (view.Apps.Count > 0)
            {
                output.WriteLine($"  {"apps",-10}{string.Join(", ", view.Apps)}");
            }
            if (view.Exclude.Count > 0)
            {
                output.WriteLine($"  {"exclude",-10}{string.Join(", ", view.Exclude)}");
            }
            var users = contexts.Where(c => catalog.Find(c.View)?.Name == view.Name).Select(c => c.Name).ToList();
            if (users.Count > 0)
            {
                output.WriteLine($"  {"contexts",-10}{string.Join(", ", users)}");
            }
        }

        foreach (var context in contexts.Where(c => catalog.Find(c.View) is null))
        {
            output.WriteLine($"Context '{context.Name}' uses view '{context.View}', which isn't defined; it opens without a view.");
        }
    }
}
