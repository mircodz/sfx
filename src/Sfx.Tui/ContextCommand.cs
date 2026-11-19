using Sfx.Core;

namespace Sfx.Tui;

/// <summary><c>sfx context list|add|use|show|remove</c>: manages the saved contexts.</summary>
internal static class ContextCommand
{
    public const string Usage = """
        sfx context list
        sfx context add <name> <endpoint> [--auth <methods>] [auth options] [--readonly | --power] [--use]
        sfx context use <name>
        sfx context show [<name>]
        sfx context remove <name>
        """;

    public static int Run(IReadOnlyList<string> args, CommandLineOptions options, ContextStore store, ClusterConnector connector)
    {
        switch (args)
        {
            case [] or ["list"]:
                List(store);
                return 0;

            case ["add", var name, var endpoint]:
                var context = new ClusterContext(name, endpoint, options.AuthMethods, options.AuthOptions, options.ReadOnly, options.Power);
                connector.Resolve(context);
                store.Set(context);
                if (options.Use || store.Active is null)
                {
                    store.Active = name;
                }
                store.Save();
                Console.WriteLine($"Saved context '{name}' to {store.Path}.");
                return 0;

            case ["use", var name]:
                store.Active = Require(store, name).Name;
                store.Save();
                Console.WriteLine($"Using context '{store.Active}'.");
                return 0;

            case ["show"]:
                Show(Require(store, store.Active ?? throw new UsageException("No active context.")));
                return 0;

            case ["show", var name]:
                Show(Require(store, name));
                return 0;

            case ["remove", var name]:
                if (!store.Remove(name))
                {
                    throw new UsageException($"No context named '{name}'.");
                }
                store.Save();
                Console.WriteLine($"Removed context '{name}'.");
                return 0;

            default:
                throw new UsageException($"Unknown context command.\n\n{Usage}");
        }
    }

    private static ClusterContext Require(ContextStore store, string name) =>
        store.Find(name) ?? throw new UsageException($"No context named '{name}'. See 'sfx context list'.");

    private static void List(ContextStore store)
    {
        var contexts = store.Contexts;
        if (contexts.Count == 0)
        {
            Console.WriteLine($"No contexts in {store.Path}. Add one with 'sfx context add <name> <endpoint>'.");
            return;
        }

        var width = Math.Max(4, contexts.Max(c => c.Name.Length));
        Console.WriteLine($"  {"NAME".PadRight(width)}  {"AUTH",-12}  {"MODE",-8}  ENDPOINT");
        foreach (var context in contexts)
        {
            var marker = string.Equals(context.Name, store.Active, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            var auth = context.Auth.Count > 0 ? string.Join(",", context.Auth) : "none";
            Console.WriteLine($"{marker} {context.Name.PadRight(width)}  {auth,-12}  {Mode(context),-8}  {context.Endpoint}");
        }
    }

    public static string Mode(ClusterContext context) => context.ReadOnly ? "readonly" : context.Power ? "power" : "normal";

    private static void Show(ClusterContext context)
    {
        Console.WriteLine($"{"name",-18}{context.Name}");
        Console.WriteLine($"{"endpoint",-18}{context.Endpoint}");
        Console.WriteLine($"{"auth",-18}{(context.Auth.Count > 0 ? string.Join(",", context.Auth) : "none")}");
        Console.WriteLine($"{"mode",-18}{Mode(context)}");
        foreach (var key in context.Options.Keys)
        {
            Console.WriteLine($"{key,-18}{string.Join(", ", context.Options.GetAll(key))}");
        }
    }
}
