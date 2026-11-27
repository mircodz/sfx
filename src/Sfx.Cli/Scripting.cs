using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Cli;

/// <summary>The scripting commands: <c>sfx get</c>, <c>describe</c>, <c>health</c>, <c>actions</c> and <c>store</c>.</summary>
public static class Scripting
{
    public static readonly IReadOnlyList<string> Commands = ["get", "describe", "health", "actions", "store"];

    public static bool IsCommand(string? word) => word is not null && Commands.Contains(word);

    /// <summary>The scripting section of <c>sfx --help</c>.</summary>
    public const string Usage = """
        sfx get <kind> [<name>]     list nodes, apps, services, partitions, replicas, types, upgrades, repairs or operations
        sfx describe <entity>       what the entity's page shows
        sfx health <entity>         why it's unhealthy, down to the events
        sfx actions <entity>        what can be done to it, and the arguments each takes
        sfx store ls|du [<path>]    the image store: what's in it, or sizes largest first

        -o <format>    get: table, wide, json or name; describe, health: text or json; actions: table, wide or json; store: json
        <entity>       cluster, node/<name>, fabric:/<app>[/<service>] (fabric:/ optional), partition/<id>,
                       replica/<partition id>/<id>, type/<name>[@<version>], store:/<path>
        The cluster: -c <context>, --endpoint <endpoint> [--auth <methods>], or the active context. Never prompts.
        Exit codes: 0 ok, 1 the cluster call failed or found nothing, 2 usage, 5 couldn't connect or sign in.
        Errors go to stderr: "sfx: <message>", or {"error": "<message>"} with -o json. sfx <command> --help for more.
        """;

    public static string Help(string command) => command switch
    {
        "get" => GetCommand.Help,
        "describe" => DescribeCommand.Help,
        "health" => HealthCommand.Help,
        "actions" => ActionsCommand.Help,
        _ => StoreCommand.Help,
    };

    /// <summary>
    /// Runs <paramref name="args"/> (the command first). Checks the arguments before <paramref name="connect"/> is called; never
    /// prompts. Returns the exit code; see <see cref="ExitCode"/>.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliOptions options,
        bool help,
        Func<CancellationToken, Task<UiSession>> connect,
        CliConsole console,
        CancellationToken ct)
    {
        var command = args[0];
        if (help)
        {
            console.Out.WriteLine(Help(command).TrimEnd());
            return ExitCode.Ok;
        }
        var json = options.Output == "json";
        try
        {
            var rest = args.Skip(1).ToList();
            var run = command switch
            {
                "get" => GetCommand.Prepare(rest, options, console),
                "describe" => DescribeCommand.Prepare(rest, options, console),
                "health" => HealthCommand.Prepare(rest, options, console),
                "actions" => ActionsCommand.Prepare(rest, options, console),
                "store" => StoreCommand.Prepare(rest, options, console),
                _ => throw CliFailure.Usage($"No command '{command}'."),
            };

            UiSession session;
            try
            {
                session = await connect(ct);
            }
            catch (Exception ex) when (ex is not (CliFailure or AuthConfigurationException or FormatException) && !ct.IsCancellationRequested)
            {
                throw new CliFailure($"could not connect: {ClusterConnector.Describe(ex)}", ExitCode.CannotConnect);
            }
            await run(session, ct);
            return ExitCode.Ok;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var (message, code) = Failure(ex);
            console.Error.WriteLine(json ? Output.JsonError(message) : $"sfx: {message}");
            return code;
        }
    }

    /// <summary>A usage error found before the command ran, written as <paramref name="args"/> asks: one line, or JSON for <c>-o json</c>.</summary>
    public static int Fail(IReadOnlyList<string> args, string message, TextWriter error)
    {
        var json = false;
        try
        {
            json = CliOptions.Split(args).Options.Output == "json";
        }
        catch (CliFailure)
        {
            // Then it isn't asking for JSON either.
        }
        error.WriteLine(json ? Output.JsonError(message) : $"sfx: {message}");
        return ExitCode.Usage;
    }

    internal static (string Message, int Code) Failure(Exception ex) => ex switch
    {
        CliFailure f => (f.Message, f.Code),
        AuthConfigurationException or FormatException => (ex.Message, ExitCode.Usage),
        ActionException or ServiceFabricException => (ex.Message, ExitCode.ClusterFailed),
        OperationCanceledException or TimeoutException => ($"the cluster didn't answer: {ClusterConnector.Describe(ex)}", ExitCode.ClusterFailed),
        _ => (ex.GetBaseException().Message, ExitCode.ClusterFailed),
    };

    /// <summary>The <c>-o</c> asked for, if the command has it, else <paramref name="fallback"/>.</summary>
    internal static string OutputOf(CliOptions options, IReadOnlyList<string> allowed, string fallback) => options.Output switch
    {
        null => fallback,
        var o when allowed.Contains(o) => o,
        var o => throw CliFailure.Usage($"-o {o} isn't one of {string.Join(", ", allowed)}."),
    };

    /// <summary>A usage error for any option <paramref name="command"/> doesn't take; <c>-o</c> and <c>--endpoint</c> are checked elsewhere.</summary>
    internal static void Only(CliOptions options, string command, params string[] applies)
    {
        (string Name, bool Given)[] all =
        [
            ("--filter", options.Filter is not null),
            ("--health", options.Health is not null),
            ("--no-headers", options.NoHeaders),
            ("-l", options.Long),
        ];
        if (all.FirstOrDefault(o => o.Given && !applies.Contains(o.Name)) is { Name: { } wrong })
        {
            throw CliFailure.Usage($"{wrong} doesn't apply to sfx {command}.");
        }
    }
}
