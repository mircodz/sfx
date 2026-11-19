using System.Reflection;
using System.Text;
using Cellar.Terminal;
using Cellar.Widgets;
using Sfx.Core;
using Sfx.Core.Auth;

namespace Sfx.Tui;

public static class SfxHost
{
    public static Task<int> RunAsync(string[] args) => RunAsync(args, BuiltInAuth.Providers);

    /// <summary>Runs sfx with the given auth methods; later providers replace earlier ones with the same name.</summary>
    public static async Task<int> RunAsync(string[] args, IEnumerable<IClusterAuthProvider> authProviders)
    {
        var connector = new ClusterConnector(authProviders);
        var flags = connector.Providers
            .SelectMany(p => p.Options)
            .Where(o => o.IsFlag)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CommandLineOptions options;
        ContextStore store;
        ClusterContext context;
        try
        {
            options = CommandLine.Parse(args, flags);
            if (options.ShowVersion)
            {
                Console.WriteLine(Version);
                return 0;
            }

            if (options.ShowHelp)
            {
                Console.WriteLine(Usage(connector.Providers));
                return 0;
            }

            store = ContextStore.Load();
            if (options.Arguments is ["context", ..])
            {
                return ContextCommand.Run(options.Arguments.Skip(1).ToList(), options, store, connector);
            }

            context = SelectContext(options, store);
            connector.Resolve(context);
        }
        catch (Exception ex) when (ex is UsageException or AuthConfigurationException)
        {
            return UsageError(ex.Message, connector.Providers);
        }

        // Auth messages (e.g. device codes) go to the console until the UI is up, then to its status line.
        Func<string, Task> notify = message =>
        {
            Console.WriteLine(message);
            return Task.CompletedTask;
        };

        ClusterSession session;
        try
        {
            Console.WriteLine($"Connecting to {context.Name} ...");
            session = await connector.ConnectAsync(context, message => notify(message), CancellationToken.None);
        }
        catch (Exception ex) when (ex is AuthConfigurationException or FormatException)
        {
            return UsageError(ex.Message, connector.Providers);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sfx: could not connect to {context.Name}: {ex.GetBaseException().Message}");
            return 1;
        }

        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);
        var shell = new Shell(app, terminal, session, connector, store, options.RefreshInterval);
        notify = shell.NotifyAsync;
        await app.RunAsync(CancellationToken.None);
        return 0;
    }

    /// <summary>The release version, without the build metadata the SDK may append (<c>+commit</c>).</summary>
    public static string Version =>
        (typeof(SfxHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    /// <summary>An endpoint on the command line, else <c>-c</c>, else the active saved context.</summary>
    private static ClusterContext SelectContext(CommandLineOptions options, ContextStore store)
    {
        switch (options.Arguments)
        {
            case [var endpoint]:
                if (options.Context is not null)
                {
                    throw new UsageException("Pass either an endpoint or -c <context>, not both.");
                }
                return new ClusterContext(endpoint, endpoint, options.AuthMethods, options.AuthOptions, options.ReadOnly, options.Power);

            case []:
                if (options.AuthMethods.Count > 0 || options.AuthOptions.Keys.Any())
                {
                    throw new UsageException("Auth options apply to an endpoint; to change a saved context use 'sfx context add'.");
                }

                var name = options.Context ?? store.Active
                    ?? throw new UsageException("No cluster given and no active context. Pass an endpoint or run 'sfx context add'.");
                var saved = store.Find(name) ?? throw new UsageException($"No context named '{name}'. See 'sfx context list'.");
                // --readonly tightens a saved context for one run; --power can't loosen a read-only one.
                return saved with { ReadOnly = saved.ReadOnly || options.ReadOnly, Power = !saved.ReadOnly && (saved.Power || options.Power) };

            default:
                throw new UsageException($"Unexpected argument '{options.Arguments[1]}'.");
        }
    }

    private static int UsageError(string message, IEnumerable<IClusterAuthProvider> providers)
    {
        Console.Error.WriteLine($"sfx: {message}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage(providers));
        return 2;
    }

    private static string Usage(IEnumerable<IClusterAuthProvider> providers)
    {
        var text = new StringBuilder();
        text.AppendLine("usage: sfx [-c <context>]                         open a saved context (default: the active one)");
        text.AppendLine("       sfx <endpoint> [--auth <methods>] [options]  open a cluster directly");
        text.AppendLine("       sfx context <command>                       manage saved contexts");
        text.AppendLine();
        text.AppendLine("  <endpoint>              host, host:port or URL (default port 19080)");
        text.AppendLine("  --auth <methods>        auth methods, applied in order (default: none)");
        text.AppendLine("  --refresh <seconds>     auto-refresh interval, 0 to disable (default: 5)");
        text.AppendLine("  --readonly              hide every action that changes the cluster");
        text.AppendLine("  --power                 unlock the riskiest actions (also :power in the UI)");
        text.AppendLine("  --version               print the version");
        text.AppendLine();
        text.AppendLine("contexts:");
        foreach (var line in ContextCommand.Usage.Split('\n'))
        {
            text.AppendLine("  " + line.TrimEnd());
        }
        text.AppendLine();
        text.AppendLine("auth methods:");
        foreach (var provider in providers)
        {
            text.AppendLine($"  {provider.Name,-10} {provider.Description}");
            foreach (var option in provider.Options)
            {
                var name = option.IsFlag ? $"--{option.Name}" : $"--{option.Name} <value>";
                text.AppendLine($"      {name,-28} {option.Description}");
            }
        }
        return text.ToString();
    }
}
