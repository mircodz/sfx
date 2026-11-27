using System.Reflection;
using System.Text;
using Cellar.Terminal;
using Cellar.Widgets;
using Sfx.Cli;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

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
        bool ui;
        AnsiTerminal? terminal = null;
        try
        {
            var (cli, rest) = CliOptions.Split(args);
            options = CommandLine.Parse(rest, flags);
            if (options.ShowVersion)
            {
                Console.WriteLine(Version);
                return 0;
            }

            if (options.Arguments is [var command, ..] && Scripting.IsCommand(command))
            {
                return await RunScriptingAsync(options, cli, () => ContextStore.Load(), connector, CliConsole.Standard(), CancellationToken.None);
            }

            if (cli.Given is { } given)
            {
                throw new UsageException($"{given} only applies to sfx {string.Join(", ", Scripting.Commands)}.");
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

            ui = options.Arguments is ["ui", ..];
            if (ui)
            {
                options = options with { Arguments = options.Arguments.Skip(1).ToList() };
            }

            if (NeedsPick(options, store) && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                terminal = new AnsiTerminal();
                if (await ContextPicker.PickAsync(terminal, store) is not { } picked)
                {
                    terminal.Dispose();
                    return 0;
                }
                store.Active = picked.Name;
                store.Save();
            }

            context = SelectContext(options, store);
            connector.Resolve(context);
        }
        catch (Exception ex) when (ex is UsageException or AuthConfigurationException or CliFailure && args.Any(Scripting.IsCommand))
        {
            terminal?.Dispose();
            return Scripting.Fail(args, ex.Message, Console.Error);
        }
        catch (Exception ex) when (ex is UsageException or AuthConfigurationException or CliFailure)
        {
            terminal?.Dispose();
            return UsageError(ex.Message, connector.Providers);
        }

        if (ui)
        {
            terminal?.Dispose();
            terminal = null;
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
            terminal?.Dispose();
            return UsageError(ex.Message, connector.Providers);
        }
        catch (Exception ex)
        {
            terminal?.Dispose();
            Console.Error.WriteLine($"sfx: could not connect to {context.Name}: {ClusterConnector.Describe(ex)}");
            return 1;
        }

        if (ui)
        {
            var log = new ActionLog(ActionLogPath(store));
            var web = new WebOptions(options.Port, !options.NoBrowser, Environment.GetEnvironmentVariable("SFX_UI_TOKEN"));
            return await WebServer.RunAsync(new UiHost(session, store, connector, log, Version, options.ReadOnly), web, CancellationToken.None);
        }

        using var screen = terminal ?? new AnsiTerminal();
        using var app = new App(screen);
        var shell = new Shell(app, session, connector, store, options.RefreshInterval, options.ReadOnly);
        notify = shell.NotifyAsync;
        await app.RunAsync(CancellationToken.None);
        return 0;
    }

    /// <summary>
    /// A scripting command (<c>sfx get</c>, …): never prompts, and only loads the contexts and connects once the arguments
    /// check out. The cluster: <c>--endpoint</c> (with <c>--auth</c>), else <c>-c</c>, else the active context.
    /// </summary>
    internal static Task<int> RunScriptingAsync(
        CommandLineOptions options,
        CliOptions cli,
        Func<ContextStore> loadStore,
        ClusterConnector connector,
        CliConsole console,
        CancellationToken cancellationToken)
    {
        async Task<UiSession> ConnectAsync(CancellationToken ct)
        {
            var store = loadStore();
            if (cli.Endpoint is null && options.Context is null && store.Active is null)
            {
                throw CliFailure.Usage("No cluster given and no active context. Pass -c <context> or --endpoint <endpoint>, or run 'sfx context add'.");
            }

            ClusterContext context;
            try
            {
                context = SelectContext(options with { Arguments = cli.Endpoint is null ? [] : [cli.Endpoint] }, store);
            }
            catch (UsageException ex)
            {
                throw CliFailure.Usage(ex.Message);
            }
            connector.Resolve(context);

            ClusterSession session;
            try
            {
                session = await connector.ConnectAsync(
                    context,
                    message =>
                    {
                        console.Error.WriteLine(message);
                        return Task.CompletedTask;
                    },
                    ct);
            }
            catch (Exception ex) when (ex is not (AuthConfigurationException or FormatException) && !ct.IsCancellationRequested)
            {
                throw new CliFailure($"could not connect to {context.Name}: {ClusterConnector.Describe(ex)}", ExitCode.CannotConnect);
            }
            return new UiSession(session, new ActionLog(ActionLogPath(store)), Version);
        }

        return Scripting.RunAsync(options.Arguments, cli, options.ShowHelp, ConnectAsync, console, cancellationToken);
    }

    /// <summary>Where every change made through sfx is recorded.</summary>
    public static string ActionLogPath(ContextStore store) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, "actions.log");

    /// <summary>The release version, without the build metadata the SDK may append (<c>+commit</c>).</summary>
    public static string Version =>
        (typeof(SfxHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0];

    /// <summary>Whether to ask which context to open: none given, none active, and some saved.</summary>
    internal static bool NeedsPick(CommandLineOptions options, ContextStore store) =>
        options.Arguments is [] && options.Context is null && store.Active is null && store.Contexts.Count > 0
        && options.AuthMethods.Count == 0 && !options.AuthOptions.Keys.Any();

    /// <summary>An endpoint on the command line, else <c>-c</c>, else the active saved context.</summary>
    internal static ClusterContext SelectContext(CommandLineOptions options, ContextStore store)
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
                var found = store.Find(name) ?? throw new UsageException($"No context named '{name}'. See 'sfx context list'.");
                var saved = store.Resolve(found).Context;
                // A read-only folder makes the context read-only; --readonly tightens it for one run; --power can't loosen either.
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
        text.AppendLine("usage: sfx [-c <context>]                           open a saved context (default: the active one)");
        text.AppendLine("       sfx <endpoint> [--auth <methods>] [options]  open a cluster directly");
        text.AppendLine("       sfx context <command>                        manage saved contexts");
        text.AppendLine("       sfx ui [<endpoint> | -c <context>]           open the cluster in the browser instead");
        text.AppendLine("       sfx get|describe|health|actions|store ...    read the cluster from scripts (see below)");
        text.AppendLine();
        text.AppendLine("  <endpoint>              host, host:port or URL (default port 19080)");
        text.AppendLine("  --auth <methods>        auth methods, applied in order (default: none)");
        text.AppendLine("  --refresh <interval>    auto-refresh interval: 5s, 1m, or off (default: 5s)");
        text.AppendLine("  --readonly              hide every action that changes a cluster, in every context");
        text.AppendLine("  --power                 unlock the riskiest actions (also :power in the UI)");
        text.AppendLine("  --version               print the version");
        text.AppendLine("  --port <n>              sfx ui: port to listen on, on 127.0.0.1 (default: any free one)");
        text.AppendLine("  --no-browser            sfx ui: print the link instead of opening it");
        text.AppendLine();
        text.AppendLine("scripting:");
        foreach (var line in Scripting.Usage.Split('\n'))
        {
            text.AppendLine(line.Trim().Length == 0 ? "" : "  " + line.TrimEnd());
        }
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
