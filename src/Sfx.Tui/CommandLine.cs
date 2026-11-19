using Sfx.Core.Auth;

namespace Sfx.Tui;

/// <param name="Arguments">Positional arguments: an endpoint, or a subcommand and its arguments.</param>
public sealed record CommandLineOptions(
    IReadOnlyList<string> Arguments,
    string? Context,
    IReadOnlyList<string> AuthMethods,
    AuthOptions AuthOptions,
    TimeSpan RefreshInterval,
    bool Use,
    bool ShowHelp,
    bool ReadOnly = false,
    bool Power = false,
    bool ShowVersion = false);

public sealed class UsageException(string message) : Exception(message);

public static class CommandLine
{
    /// <summary>
    /// Host options (<c>--auth</c>, <c>-c/--context</c>, <c>--refresh</c>, <c>--use</c>, <c>--readonly</c>, <c>--power</c>,
    /// <c>--help</c>, <c>--version</c>) are read here; every other <c>--key value</c> is collected for the auth providers.
    /// <paramref name="flags"/> names options that take no value.
    /// </summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args, IReadOnlySet<string> flags)
    {
        var arguments = new List<string>();
        string? context = null;
        var methods = new List<string>();
        var authOptions = new AuthOptions();
        var refresh = TimeSpan.FromSeconds(5);
        var use = false;
        var help = false;
        var version = false;
        var readOnly = false;
        var power = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    help = true;
                    continue;
                case "--version":
                    version = true;
                    continue;
                case "--use":
                    use = true;
                    continue;
                case "--readonly":
                    readOnly = true;
                    continue;
                case "--power":
                    power = true;
                    continue;
                case "-c":
                    context = i + 1 < args.Count ? args[++i] : throw new UsageException("-c needs a context name.");
                    continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                arguments.Add(arg);
                continue;
            }

            var key = arg[2..];
            string? value = null;
            var equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (flags.Contains(key))
            {
                value = "true";
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (value is null)
            {
                throw new UsageException($"--{key} needs a value.");
            }

            switch (key)
            {
                case "auth":
                    methods.AddRange(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "context":
                    context = value;
                    break;
                case "refresh":
                    refresh = RefreshInterval.TryParse(value, out var interval)
                        ? interval
                        : throw new UsageException($"--refresh expects an interval like 5s, 1m or off, got '{value}'.");
                    break;
                default:
                    authOptions.Add(key, value);
                    break;
            }
        }

        return new CommandLineOptions(arguments, context, methods, authOptions, refresh, use, help, readOnly, power, version);
    }
}
